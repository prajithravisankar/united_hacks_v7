namespace Boys.Ledger.Domain.Commitments;

/// <summary>The commitment lifecycle states — exactly the set the DB <c>ck_commit_state</c> check allows.
/// <c>Active</c> = a leg is running (the fund rides, awaiting proof). Terminal money states are
/// <c>CashedOut</c>/<c>Succeeded</c>/<c>Failed</c>, each of which settles once into <c>Settled</c>.</summary>
public enum CommitmentState
{
    Draft,
    Active,
    PendingVerification,
    MilestoneCleared,
    Riding,
    CashedOut,
    Succeeded,
    Failed,
    Settled,
}

public static class CommitmentStates
{
    private static readonly IReadOnlyDictionary<CommitmentState, string> ToDbName = new Dictionary<CommitmentState, string>
    {
        [CommitmentState.Draft] = "draft",
        [CommitmentState.Active] = "active",
        [CommitmentState.PendingVerification] = "pending_verification",
        [CommitmentState.MilestoneCleared] = "milestone_cleared",
        [CommitmentState.Riding] = "riding",
        [CommitmentState.CashedOut] = "cashed_out",
        [CommitmentState.Succeeded] = "succeeded",
        [CommitmentState.Failed] = "failed",
        [CommitmentState.Settled] = "settled",
    };

    private static readonly IReadOnlyDictionary<string, CommitmentState> FromDbName =
        ToDbName.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string ToDb(this CommitmentState state) => ToDbName[state];

    public static CommitmentState FromDb(string name) =>
        FromDbName.TryGetValue(name, out var state)
            ? state
            : throw new ArgumentOutOfRangeException(nameof(name), name, "unknown commitment state");
}
