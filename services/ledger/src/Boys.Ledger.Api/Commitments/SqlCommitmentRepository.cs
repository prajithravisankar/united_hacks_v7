namespace Boys.Ledger.Api.Commitments;

using Boys.Ledger.Api.Infrastructure;
using Boys.Ledger.Domain.Abstractions;
using Boys.Ledger.Domain.Commitments;
using Boys.Ledger.Domain.Errors;
using Dapper;
using Microsoft.Data.SqlClient;

/// <summary>Dapper persistence for the commitment state machine. The deadline gate is evaluated inside the
/// same transaction as the command (guarded by ROWVERSION), so a command is never applied to a live leg
/// whose deadline has passed — even under a concurrent transition. Idempotency is one append-only
/// <c>commitment_events</c> row per transition, keyed by a UNIQUE idempotency_key; system-generated keys use
/// a reserved <c>sys:</c> prefix that callers may not use, so caller and system keys never collide.</summary>
public sealed class SqlCommitmentRepository : ICommitmentRepository
{
    private const int UniqueViolation = 2627;
    private const int DuplicateKey = 2601;
    private const string SystemKeyPrefix = "sys:";
    private const int SweepAttempts = 3;

    private readonly IDbConnectionFactory _factory;
    private readonly IClock _clock;

    public SqlCommitmentRepository(IDbConnectionFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    private sealed record StateRow(string State, DateTime Deadline, byte[] RowVersion);

    private sealed record RecordedTransition(string FromState, string ToState);

    public async Task<CommitmentView> GetAsync(int commitmentId, CancellationToken cancellationToken = default)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        var (state, deadline) = await SweepAsync(conn, commitmentId, cancellationToken);
        return new CommitmentView(commitmentId, state, deadline);
    }

    public async Task<TransitionResult> TransitionAsync(
        int commitmentId,
        CommitmentCommand command,
        bool isFinalLeg,
        string idempotencyKey,
        bool systemKey = false,
        CancellationToken cancellationToken = default)
    {
        // A caller may never use the reserved system namespace — that is what makes the system's derived keys
        // (sys:settle:{id}, sys:activate:{id}, ...) unforgeable, so no caller can poison another commitment.
        if (!systemKey && idempotencyKey.StartsWith(SystemKeyPrefix, StringComparison.Ordinal))
        {
            throw new LedgerValidationException($"idempotency key must not use the reserved '{SystemKeyPrefix}' prefix");
        }

        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);

        // Idempotency (authoritative, in-tx): if this key already produced a transition, return it as a no-op.
        var recorded = await conn.QuerySingleOrDefaultAsync<RecordedTransition>(new CommandDefinition(
            "SELECT from_state AS FromState, to_state AS ToState FROM commitment_events WHERE idempotency_key = @key",
            new { key = idempotencyKey }, tx, cancellationToken: cancellationToken));
        if (recorded is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return new TransitionResult(
                CommitmentStates.FromDb(recorded.FromState), CommitmentStates.FromDb(recorded.ToState), WasApplied: false);
        }

        var row = await ReadStateRowAsync(conn, tx, commitmentId, cancellationToken)
                  ?? throw new CommitmentNotFoundException(commitmentId);
        var current = CommitmentStates.FromDb(row.State);
        var deadlineUtc = DateTime.SpecifyKind(row.Deadline, DateTimeKind.Utc);
        var passed = _clock.UtcNow.UtcDateTime > deadlineUtc;  // strict: the deadline instant itself is not a miss

        // Deadline gate in the SAME tx as the command: an expired live leg fails here, and the command is
        // rejected. The ROWVERSION guard means we can never commit against a stale read.
        if (passed && CommitmentMachine.IsDeadlineTrippable(current))
        {
            var tripped = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE commitments SET state = 'failed' WHERE commitment_id = @id AND row_version = @rowVersion",
                new { id = commitmentId, rowVersion = row.RowVersion }, tx, cancellationToken: cancellationToken));
            if (tripped == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                throw new ConcurrencyConflictException(commitmentId);  // row moved under us; caller re-reads and retries
            }

            await InsertDeadlineEventAsync(conn, tx, commitmentId, current, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            // The requested command hit a leg that just failed on the deadline — reject it.
            throw new IllegalTransitionException(CommitmentState.Failed.ToDb(), command.ToDb());
        }

        var next = CommitmentMachine.Next(current, command, isFinalLeg);  // throws IllegalTransitionException

        var updated = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE commitments SET state = @next WHERE commitment_id = @id AND row_version = @rowVersion",
            new { next = next.ToDb(), id = commitmentId, rowVersion = row.RowVersion },
            tx, cancellationToken: cancellationToken));
        if (updated == 0)
        {
            await tx.RollbackAsync(cancellationToken);
            throw new ConcurrencyConflictException(commitmentId);
        }

        try
        {
            await InsertEventAsync(conn, tx, commitmentId, current, next, command.ToDb(), idempotencyKey, cancellationToken);
        }
        catch (SqlException ex) when (ex.Number is UniqueViolation or DuplicateKey)
        {
            await tx.RollbackAsync(cancellationToken);
            return new TransitionResult(current, current, WasApplied: false);  // concurrent same-key winner
        }

        await tx.CommitAsync(cancellationToken);
        return new TransitionResult(current, next, WasApplied: true);
    }

    public async Task<IReadOnlyList<CommitmentEventRecord>> GetEventsAsync(
        int commitmentId, CancellationToken cancellationToken = default)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<CommitmentEventRecord>(new CommandDefinition(
            "SELECT event_id AS EventId, from_state AS FromState, to_state AS ToState, command AS Command, "
            + "occurred_at AS OccurredAt FROM commitment_events WHERE commitment_id = @id ORDER BY event_id",
            new { id = commitmentId }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    /// <summary>Reads the commitment and, if a leg is live past its deadline, trips it to failed. On a
    /// ROWVERSION conflict (a concurrent transition moved the row) it re-reads and retries, so it always
    /// returns the true current state — never a stale "failed" for a row that actually moved elsewhere.</summary>
    private async Task<(CommitmentState State, DateTimeOffset Deadline)> SweepAsync(
        SqlConnection conn, int commitmentId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < SweepAttempts; attempt++)
        {
            var row = await ReadStateRowAsync(conn, null, commitmentId, cancellationToken)
                      ?? throw new CommitmentNotFoundException(commitmentId);
            var state = CommitmentStates.FromDb(row.State);
            var deadlineUtc = DateTime.SpecifyKind(row.Deadline, DateTimeKind.Utc);
            var deadline = new DateTimeOffset(deadlineUtc, TimeSpan.Zero);

            if (!(_clock.UtcNow.UtcDateTime > deadlineUtc && CommitmentMachine.IsDeadlineTrippable(state)))
            {
                return (state, deadline);
            }

            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);
            var tripped = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE commitments SET state = 'failed' WHERE commitment_id = @id AND row_version = @rowVersion",
                new { id = commitmentId, rowVersion = row.RowVersion }, tx, cancellationToken: cancellationToken));
            if (tripped == 1)
            {
                await InsertDeadlineEventAsync(conn, tx, commitmentId, state, cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return (CommitmentState.Failed, deadline);
            }

            await tx.RollbackAsync(cancellationToken);  // row moved under us; re-read and re-evaluate
        }

        var final = await ReadStateRowAsync(conn, null, commitmentId, cancellationToken)
                    ?? throw new CommitmentNotFoundException(commitmentId);
        return (CommitmentStates.FromDb(final.State),
            new DateTimeOffset(DateTime.SpecifyKind(final.Deadline, DateTimeKind.Utc), TimeSpan.Zero));
    }

    private static Task<StateRow?> ReadStateRowAsync(
        SqlConnection conn, SqlTransaction? tx, int commitmentId, CancellationToken cancellationToken)
        => conn.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
            "SELECT state AS State, deadline AS Deadline, row_version AS RowVersion FROM commitments WHERE commitment_id = @id",
            new { id = commitmentId }, tx, cancellationToken: cancellationToken));

    /// <summary>Inserts the deadline-gate event under a reserved system key. A duplicate (a concurrent sweep
    /// already recorded it) is harmless — the state trip still stands.</summary>
    private static async Task InsertDeadlineEventAsync(
        SqlConnection conn, SqlTransaction tx, int commitmentId, CommitmentState from, CancellationToken cancellationToken)
    {
        try
        {
            await InsertEventAsync(conn, tx, commitmentId, from, CommitmentState.Failed, "deadline_gate",
                $"{SystemKeyPrefix}deadline-gate:{commitmentId}", cancellationToken);
        }
        catch (SqlException ex) when (ex.Number is UniqueViolation or DuplicateKey)
        {
            // Already recorded — nothing to do.
        }
    }

    private static Task InsertEventAsync(
        SqlConnection conn, SqlTransaction tx, int commitmentId, CommitmentState from, CommitmentState to,
        string command, string idempotencyKey, CancellationToken cancellationToken)
        => conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO commitment_events (commitment_id, from_state, to_state, command, idempotency_key) "
            + "VALUES (@id, @from, @to, @command, @key)",
            new { id = commitmentId, from = from.ToDb(), to = to.ToDb(), command, key = idempotencyKey },
            tx, cancellationToken: cancellationToken));
}
