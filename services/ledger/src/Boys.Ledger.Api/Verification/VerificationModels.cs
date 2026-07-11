namespace Boys.Ledger.Api.Verification;

using Boys.Ledger.Domain.Commitments;

/// <summary>The AI first-pass outcome. The status is <c>PendingAi</c> (with a degraded flag) when brain was
/// unreachable — the referee can still decide manually.</summary>
public enum AiVerdictStatus
{
    Supported,     // AI thinks the evidence supports the claim
    Insufficient,  // AI thinks it does not (resubmission invited)
    PendingAi,     // brain unavailable — no AI opinion yet (degraded)
}

public sealed record AiVerdict(
    AiVerdictStatus Status,
    double Confidence,
    string Reasoning,
    string InsufficiencyReason,
    bool Degraded);

/// <summary>The referee's final call. Human authority is absolute — it can overrule the AI in either direction.</summary>
public enum RefereeDecision
{
    Approve,
    Reject,
}

public sealed record SubmitProofResult(
    CommitmentState CommitmentState,
    string MilestoneState,
    AiVerdict AiVerdict,
    int ResubmissionCount,
    string EvidenceUri);

public sealed record RefereeDecisionResult(
    CommitmentState CommitmentState,
    string MilestoneState,
    bool WasApplied);

/// <summary>Internal submit-proof request (evidence bytes are base64 for JSON transport; B16 formalizes this).</summary>
public sealed record SubmitProofRequest(string Claim, string EvidenceBase64, string Mime, string IdempotencyKey);

/// <summary>Internal referee-decision request. <c>Decision</c> is "approve" or "reject".</summary>
public sealed record RefereeDecisionRequest(string Decision, int RefereeUserId, string IdempotencyKey);
