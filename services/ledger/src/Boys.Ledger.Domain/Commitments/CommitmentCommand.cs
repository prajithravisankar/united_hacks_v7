namespace Boys.Ledger.Domain.Commitments;

/// <summary>The commands that drive the machine. Every transition happens through exactly one of these —
/// there is no other way to change a commitment's state.</summary>
public enum CommitmentCommand
{
    Activate,          // draft -> active (escrow the stake)
    SubmitProof,       // active/riding -> pending_verification
    ClearMilestone,    // pending_verification -> milestone_cleared (referee approved)
    RejectMilestone,   // pending_verification -> failed (hard gate: a verified miss ends the ticket)
    CashOut,           // milestone_cleared (non-final) -> cashed_out (bow out gracefully)
    Ride,              // milestone_cleared (non-final) -> riding (compound to the next leg)
    Complete,          // milestone_cleared (final) -> succeeded
    Settle,            // cashed_out/succeeded/failed -> settled (money released, B15)
}

public static class CommitmentCommands
{
    public static string ToDb(this CommitmentCommand command) => command switch
    {
        CommitmentCommand.Activate => "activate",
        CommitmentCommand.SubmitProof => "submit_proof",
        CommitmentCommand.ClearMilestone => "clear_milestone",
        CommitmentCommand.RejectMilestone => "reject_milestone",
        CommitmentCommand.CashOut => "cash_out",
        CommitmentCommand.Ride => "ride",
        CommitmentCommand.Complete => "complete",
        CommitmentCommand.Settle => "settle",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };
}
