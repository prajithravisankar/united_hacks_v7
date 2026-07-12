namespace Boys.Ledger.Api.PublicApi;

/// <summary>Create a goal through the AI gate.</summary>
public sealed record CreateGoalRequest(
    string GoalText,
    long StakeCents,
    int CharityId,
    string DriveMode,
    DateTimeOffset Deadline,
    IReadOnlyList<CreateMilestone> Milestones);

public sealed record CreateMilestone(string Description, string TargetMetric, DateTimeOffset DueDate);

/// <summary>Submit proof for a milestone (evidence base64-encoded for JSON transport).</summary>
public sealed record ProofRequest(int MilestoneId, string Claim, string EvidenceBase64, string Mime, string IdempotencyKey);

/// <summary>A referee's decision on a milestone ("approve" | "reject").</summary>
public sealed record DecisionRequest(string Decision, string IdempotencyKey);

/// <summary>Community-pool backdrop stats.</summary>
public sealed record CommunityPoolStats(int CommittedPeople, long PoolCents);

/// <summary>A vetted charity.</summary>
public sealed record Charity(int CharityId, string Name);

/// <summary>A commitment's milestone, as returned by GET /api/goals/{id} (so callers learn milestone ids).</summary>
public sealed record MilestoneView(int MilestoneId, int Ordinal, string Description, string TargetMetric, DateTime DueDate, string State);
