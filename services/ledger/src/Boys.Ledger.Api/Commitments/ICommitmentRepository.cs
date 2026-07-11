namespace Boys.Ledger.Api.Commitments;

using Boys.Ledger.Domain.Commitments;

/// <summary>A commitment's current state after the lazy deadline sweep has been applied.</summary>
public sealed record CommitmentView(int CommitmentId, CommitmentState State, DateTimeOffset Deadline);

/// <summary>The outcome of a transition. <see cref="WasApplied"/> is false when the idempotency key had
/// already been applied — the call was a no-op returning the recorded result.</summary>
public sealed record TransitionResult(CommitmentState FromState, CommitmentState ToState, bool WasApplied);

/// <summary>One row of the append-only audit trail. <see cref="OccurredAt"/> is a UTC instant
/// (the column is DATETIME2, written with SYSUTCDATETIME()).</summary>
public sealed record CommitmentEventRecord(
    long EventId, string FromState, string ToState, string Command, DateTime OccurredAt);

/// <summary>Persists the commitment state machine with optimistic concurrency (rowversion) and an
/// append-only event trail. Every read and every command first applies the deadline gate.</summary>
public interface ICommitmentRepository
{
    Task<CommitmentView> GetAsync(int commitmentId, CancellationToken cancellationToken = default);

    /// <summary>Apply a command. <paramref name="systemKey"/> is true only for internal callers that pass a
    /// reserved <c>sys:</c> key (settlement, activation, cash-out/ride); caller-facing paths leave it false so
    /// a caller can never forge a system key and poison another commitment's transition.</summary>
    Task<TransitionResult> TransitionAsync(
        int commitmentId,
        CommitmentCommand command,
        bool isFinalLeg,
        string idempotencyKey,
        bool systemKey = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommitmentEventRecord>> GetEventsAsync(
        int commitmentId, CancellationToken cancellationToken = default);
}
