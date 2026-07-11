namespace Boys.Ledger.Domain.Commitments;

using Boys.Ledger.Domain.Errors;

/// <summary>The commitment lifecycle as pure code: (state, command) → new state, or a typed error. Hard
/// gates are baked in — a verified miss ends the ticket, cash-out/ride are only reachable from a cleared
/// non-final milestone, and clearing the final leg leads only to success. Zero I/O, so the whole transition
/// table is unit-tested exhaustively.</summary>
public static class CommitmentMachine
{
    /// <summary>States where a leg is still in flight, so a passed deadline trips the commitment to failed.</summary>
    private static readonly IReadOnlySet<CommitmentState> LiveStates = new HashSet<CommitmentState>
    {
        CommitmentState.Active,
        CommitmentState.PendingVerification,
        CommitmentState.MilestoneCleared,
        CommitmentState.Riding,
    };

    /// <summary>The lazy deadline gate: if the deadline has passed and a leg is still live, the commitment is
    /// failed — no command needed. Applied on every read and before every command. The deadline instant
    /// itself is NOT yet passed (boundary is strict <c>&gt;</c>); anything after it is a miss.</summary>
    public static CommitmentState ApplyDeadline(CommitmentState current, bool deadlinePassed) =>
        deadlinePassed && LiveStates.Contains(current) ? CommitmentState.Failed : current;

    /// <summary>Returns whether a passed deadline would trip this state (so callers know a read caused a
    /// change worth persisting).</summary>
    public static bool IsDeadlineTrippable(CommitmentState current) => LiveStates.Contains(current);

    /// <summary>Apply a command. <paramref name="isFinalLeg"/> distinguishes the last milestone (whose only
    /// clear-path is success) from earlier ones (cash-out or ride). Throws
    /// <see cref="IllegalTransitionException"/> for any (state, command) not in the table.</summary>
    public static CommitmentState Next(CommitmentState current, CommitmentCommand command, bool isFinalLeg) =>
        (current, command) switch
        {
            (CommitmentState.Draft, CommitmentCommand.Activate) => CommitmentState.Active,

            (CommitmentState.Active, CommitmentCommand.SubmitProof) => CommitmentState.PendingVerification,
            (CommitmentState.Riding, CommitmentCommand.SubmitProof) => CommitmentState.PendingVerification,

            (CommitmentState.PendingVerification, CommitmentCommand.ClearMilestone) => CommitmentState.MilestoneCleared,
            (CommitmentState.PendingVerification, CommitmentCommand.RejectMilestone) => CommitmentState.Failed,

            (CommitmentState.MilestoneCleared, CommitmentCommand.CashOut) when !isFinalLeg => CommitmentState.CashedOut,
            (CommitmentState.MilestoneCleared, CommitmentCommand.Ride) when !isFinalLeg => CommitmentState.Riding,
            (CommitmentState.MilestoneCleared, CommitmentCommand.Complete) when isFinalLeg => CommitmentState.Succeeded,

            (CommitmentState.CashedOut, CommitmentCommand.Settle) => CommitmentState.Settled,
            (CommitmentState.Succeeded, CommitmentCommand.Settle) => CommitmentState.Settled,
            (CommitmentState.Failed, CommitmentCommand.Settle) => CommitmentState.Settled,

            _ => throw new IllegalTransitionException(current.ToDb(), command.ToDb()),
        };
}
