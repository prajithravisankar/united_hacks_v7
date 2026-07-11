namespace Boys.Ledger.Api.Ledger;

using Boys.Ledger.Api.Infrastructure;
using Boys.Ledger.Domain.Errors;
using Boys.Ledger.Domain.Ledger;
using Dapper;
using Microsoft.Data.SqlClient;

/// <summary>Dapper persistence for the ledger. A post is one transaction that: takes a per-commitment
/// application lock (so posts to the same commitment serialize while different commitments run in
/// parallel — no deadlocks); returns the existing result if the idempotency key was already used (no-op);
/// rejects a plan that would drive this commitment's USER_ESCROW below zero; then writes the header and
/// all lines together. The UNIQUE idempotency key is the belt-and-suspenders backstop for any post that
/// isn't commitment-scoped.</summary>
public sealed class SqlLedgerRepository : ILedgerRepository
{
    // SQL Server error numbers for a unique-key violation (idempotency race).
    private const int UniqueViolation = 2627;
    private const int DuplicateKey = 2601;

    private readonly IDbConnectionFactory _factory;

    public SqlLedgerRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<PostedTransaction> PostAsync(PostingPlan plan, CancellationToken cancellationToken = default)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);

        // Serialize posts for this commitment (or globally when unscoped). Held until commit/rollback, so
        // the idempotency check and the escrow guard below can't race a concurrent writer.
        await AcquireLockAsync(conn, tx, plan.CommitmentId, cancellationToken);

        var existing = await FindByKeyAsync(conn, tx, plan.IdempotencyKey);
        if (existing is not null)
        {
            await tx.RollbackAsync(cancellationToken);  // nothing to write; the key already posted
            return new PostedTransaction(existing.Value, plan.IdempotencyKey, WasAlreadyApplied: true);
        }

        await GuardEscrowNotNegativeAsync(conn, tx, plan);

        var txnId = Guid.NewGuid();
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO ledger_transactions (txn_id, idempotency_key, commitment_id) VALUES (@txnId, @key, @commitmentId)",
                new { txnId, key = plan.IdempotencyKey, commitmentId = plan.CommitmentId },
                tx, cancellationToken: cancellationToken));

            foreach (var posting in plan.Postings)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO ledger_postings (txn_id, account, delta_cents) VALUES (@txnId, @account, @delta)",
                    new { txnId, account = posting.Account.ToDb(), delta = posting.DeltaCents },
                    tx, cancellationToken: cancellationToken));
            }

            await tx.CommitAsync(cancellationToken);
            return new PostedTransaction(txnId, plan.IdempotencyKey, WasAlreadyApplied: false);
        }
        catch (SqlException ex) when (ex.Number is UniqueViolation or DuplicateKey)
        {
            // A concurrent writer won the idempotency race; return their transaction.
            await tx.RollbackAsync(cancellationToken);
            var winner = await FindByKeyOwnConnectionAsync(plan.IdempotencyKey, cancellationToken)
                         ?? throw new InvalidOperationException("idempotency race but no winning transaction found");
            return new PostedTransaction(winner, plan.IdempotencyKey, WasAlreadyApplied: true);
        }
    }

    public async Task<long> GetAccountBalanceAsync(LedgerAccount account, CancellationToken cancellationToken = default)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT ISNULL(SUM(delta_cents), 0) FROM ledger_postings WHERE account = @account",
            new { account = account.ToDb() }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyDictionary<LedgerAccount, long>> GetCommitmentBalancesAsync(
        int commitmentId, CancellationToken cancellationToken = default)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<(string Account, long Balance)>(new CommandDefinition(
            "SELECT p.account, ISNULL(SUM(p.delta_cents), 0) AS Balance "
            + "FROM ledger_postings p JOIN ledger_transactions t ON p.txn_id = t.txn_id "
            + "WHERE t.commitment_id = @commitmentId GROUP BY p.account",
            new { commitmentId }, cancellationToken: cancellationToken));

        return rows.ToDictionary(r => LedgerAccounts.FromDb(r.Account), r => r.Balance);
    }

    /// <summary>Takes an exclusive transaction-scoped application lock keyed on the commitment (or a global
    /// key when the plan is unscoped). Serializes concurrent posts for the same commitment without the
    /// deadlock risk of SERIALIZABLE isolation.</summary>
    private static async Task AcquireLockAsync(
        SqlConnection conn, SqlTransaction tx, int? commitmentId, CancellationToken cancellationToken)
    {
        var resource = commitmentId is int id ? $"ledger:commitment:{id}" : "ledger:global";
        var result = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "DECLARE @r INT; "
            + "EXEC @r = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', "
            + "@LockOwner = 'Transaction', @LockTimeout = 5000; "
            + "SELECT @r;",
            new { resource }, tx, cancellationToken: cancellationToken));

        if (result < 0)
        {
            // Couldn't serialize in time — surface as a transient conflict rather than corrupting balances.
            throw new IdempotencyConflictException(resource);
        }
    }

    private static async Task<Guid?> FindByKeyAsync(SqlConnection conn, SqlTransaction tx, string idempotencyKey)
    {
        var id = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT txn_id FROM ledger_transactions WHERE idempotency_key = @key",
            new { key = idempotencyKey }, tx));
        return id;
    }

    private async Task<Guid?> FindByKeyOwnConnectionAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        return await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT txn_id FROM ledger_transactions WHERE idempotency_key = @key",
            new { key = idempotencyKey }, cancellationToken: cancellationToken));
    }

    /// <summary>A commitment's USER_ESCROW may never go negative — you cannot release more principal than it
    /// escrowed. Escrow postings are always commitment-scoped (enforced in <see cref="PostingPlan"/>), so the
    /// guard reads exactly that commitment's escrow, and the per-commitment application lock held for this
    /// transaction serializes every other escrow post for the same commitment. That mutual exclusion — not
    /// the isolation level (this runs at the default READ COMMITTED) — is what makes the check race-free.</summary>
    private static async Task GuardEscrowNotNegativeAsync(SqlConnection conn, SqlTransaction tx, PostingPlan plan)
    {
        var escrowDelta = plan.Postings
            .Where(p => p.Account == LedgerAccount.UserEscrow)
            .Sum(p => p.DeltaCents);

        if (escrowDelta >= 0)
        {
            return;  // escrow only grows (or is untouched); nothing to guard
        }

        // PostingPlan guarantees a commitment whenever escrow is touched, so this scope is always correct.
        var current = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT ISNULL(SUM(p.delta_cents), 0) FROM ledger_postings p "
            + "JOIN ledger_transactions t ON p.txn_id = t.txn_id "
            + "WHERE p.account = 'USER_ESCROW' AND t.commitment_id = @commitmentId",
            new { commitmentId = plan.CommitmentId }, tx));

        var resulting = current + escrowDelta;
        if (resulting < 0)
        {
            throw new NegativeBalanceException(resulting);
        }
    }
}
