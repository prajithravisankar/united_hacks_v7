namespace Boys.Ledger.Api.Verification;

using System.Text.Json;
using Boys.Contracts.Brain.V1;
using Boys.Ledger.Api.Commitments;
using Boys.Ledger.Api.Grpc;
using Boys.Ledger.Api.Infrastructure;
using Boys.Ledger.Domain.Abstractions;
using Boys.Ledger.Domain.Commitments;
using Boys.Ledger.Domain.Errors;
using Dapper;
using Google.Protobuf;

/// <summary>The proof loop: submit evidence → brain's AI checks it → a human referee decides with final
/// authority. AI recommends; the referee decides. When brain is unreachable the submission is still
/// accepted (verdict = PendingAi, degraded) and the referee can decide manually. This is the seam between
/// two services, so it carries the graceful-degradation and human-authority guarantees.</summary>
public sealed class VerificationService
{
    public const int MaxEvidenceBytes = 5_000_000;
    private static readonly IReadOnlySet<string> AllowedMimes =
        new HashSet<string> { "image/png", "image/jpeg", "application/pdf" };

    private readonly IDbConnectionFactory _factory;
    private readonly ICommitmentRepository _commitments;
    private readonly IBrainClient _brain;
    private readonly IEvidenceStore _evidence;
    private readonly IClock _clock;
    private readonly ILogger<VerificationService> _logger;

    public VerificationService(
        IDbConnectionFactory factory,
        ICommitmentRepository commitments,
        IBrainClient brain,
        IEvidenceStore evidence,
        IClock clock,
        ILogger<VerificationService> logger)
    {
        _factory = factory;
        _commitments = commitments;
        _brain = brain;
        _evidence = evidence;
        _clock = clock;
        _logger = logger;
    }

    private sealed record MilestoneRow(int CommitmentId, int Ordinal, string State);

    /// <summary>Submit proof for a milestone: store the evidence, move the leg into verification, and run the
    /// AI first pass (or record a degraded PendingAi verdict if brain is down). Resubmissions after an
    /// insufficient verdict are allowed and re-run the AI.</summary>
    public async Task<SubmitProofResult> SubmitProofAsync(
        int commitmentId,
        int milestoneId,
        string claim,
        byte[] evidence,
        string mime,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (evidence.Length > MaxEvidenceBytes)
        {
            throw new OversizedEvidenceException(evidence.Length, MaxEvidenceBytes);
        }

        if (!AllowedMimes.Contains(mime))
        {
            throw new UnsupportedMimeException(mime);
        }

        var milestone = await GetMilestoneAsync(milestoneId, cancellationToken)
                        ?? throw new MilestoneNotFoundException(milestoneId);
        if (milestone.CommitmentId != commitmentId)
        {
            throw new MilestoneNotFoundException(milestoneId);  // not this commitment's milestone
        }

        // First submission moves the leg into verification; a resubmission (already pending) just re-runs AI.
        var state = (await _commitments.GetAsync(commitmentId, cancellationToken)).State;
        if (state is CommitmentState.Active or CommitmentState.Riding)
        {
            await _commitments.TransitionAsync(
                commitmentId, CommitmentCommand.SubmitProof, isFinalLeg: false, idempotencyKey, cancellationToken: cancellationToken);
        }
        else if (state != CommitmentState.PendingVerification)
        {
            throw new IllegalTransitionException(state.ToDb(), CommitmentCommand.SubmitProof.ToDb());
        }

        var evidenceUri = await _evidence.StoreAsync(evidence, mime, cancellationToken);
        var verdict = await RunAiCheckAsync(milestoneId, claim, evidence, mime, cancellationToken);

        var resubmissionCount = await InsertVerificationAsync(milestoneId, evidenceUri, verdict, cancellationToken);
        await SetMilestoneStateAsync(milestoneId, "pending_verification", cancellationToken);

        _logger.LogInformation(
            "proof submitted for milestone {MilestoneId}: evidence={EvidenceUri} ai={Status} degraded={Degraded} attempt={Attempt}",
            milestoneId, evidenceUri, verdict.Status, verdict.Degraded, resubmissionCount);

        return new SubmitProofResult(
            CommitmentState.PendingVerification, "pending_verification", verdict, resubmissionCount, evidenceUri);
    }

    /// <summary>The referee's final decision. Approve clears the milestone; reject fails the commitment
    /// (hard gate). The referee can overrule the AI in either direction. Idempotent (double-click safe) and
    /// restricted to users with the referee role.</summary>
    public async Task<RefereeDecisionResult> RefereeDecideAsync(
        int milestoneId,
        RefereeDecision decision,
        int refereeUserId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var role = await GetUserRoleAsync(refereeUserId, cancellationToken);
        if (role != "referee")
        {
            throw new ForbiddenException("only a referee may decide a proof");
        }

        var milestone = await GetMilestoneAsync(milestoneId, cancellationToken)
                        ?? throw new MilestoneNotFoundException(milestoneId);

        var command = decision == RefereeDecision.Approve
            ? CommitmentCommand.ClearMilestone
            : CommitmentCommand.RejectMilestone;

        // The commitment transition is the source of truth + idempotency; a double-click (same key) is a no-op.
        var result = await _commitments.TransitionAsync(
            milestone.CommitmentId, command, isFinalLeg: false, idempotencyKey, cancellationToken: cancellationToken);

        var milestoneState = decision == RefereeDecision.Approve ? "cleared" : "failed";
        await SetMilestoneStateAsync(milestoneId, milestoneState, cancellationToken);
        await RecordRefereeDecisionAsync(
            milestoneId, decision == RefereeDecision.Approve ? "approved" : "rejected", cancellationToken);

        _logger.LogInformation(
            "referee {RefereeId} {Decision} milestone {MilestoneId} -> commitment {State}",
            refereeUserId, decision, milestoneId, result.ToState.ToDb());

        return new RefereeDecisionResult(result.ToState, milestoneState, result.WasApplied);
    }

    private async Task<AiVerdict> RunAiCheckAsync(
        int milestoneId, string claim, byte[] evidence, string mime, CancellationToken cancellationToken)
    {
        try
        {
            var proof = await _brain.CheckProofAsync(new CheckProofRequest
            {
                MilestoneId = milestoneId.ToString(),
                Claim = claim,
                Evidence = ByteString.CopyFrom(evidence),
                Mime = mime,
            }, cancellationToken);

            return new AiVerdict(
                proof.SupportsClaim ? AiVerdictStatus.Supported : AiVerdictStatus.Insufficient,
                proof.Confidence,
                proof.Reasoning,
                proof.InsufficiencyReason,
                Degraded: false);
        }
        catch (BrainUnavailableException)
        {
            // Graceful degradation: accept the submission, mark it PendingAi; the referee can still decide.
            _logger.LogWarning("brain unavailable; recording PendingAi (degraded) verdict for milestone {MilestoneId}", milestoneId);
            return new AiVerdict(AiVerdictStatus.PendingAi, 0.0,
                "brain unavailable — awaiting AI or a manual referee decision", string.Empty, Degraded: true);
        }
    }

    private async Task<MilestoneRow?> GetMilestoneAsync(int milestoneId, CancellationToken cancellationToken)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        return await conn.QuerySingleOrDefaultAsync<MilestoneRow>(new CommandDefinition(
            "SELECT commitment_id AS CommitmentId, ordinal AS Ordinal, state AS State "
            + "FROM milestones WHERE milestone_id = @id",
            new { id = milestoneId }, cancellationToken: cancellationToken));
    }

    private async Task<string?> GetUserRoleAsync(int userId, CancellationToken cancellationToken)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        return await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT role FROM users WHERE user_id = @id", new { id = userId }, cancellationToken: cancellationToken));
    }

    private async Task<int> InsertVerificationAsync(
        int milestoneId, string evidenceUri, AiVerdict verdict, CancellationToken cancellationToken)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO verifications (milestone_id, evidence_uri, ai_verdict) VALUES (@milestoneId, @uri, @verdict)",
            new { milestoneId, uri = evidenceUri, verdict = JsonSerializer.Serialize(verdict) },
            cancellationToken: cancellationToken));
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM verifications WHERE milestone_id = @id",
            new { id = milestoneId }, cancellationToken: cancellationToken));
    }

    private async Task SetMilestoneStateAsync(int milestoneId, string state, CancellationToken cancellationToken)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE milestones SET state = @state WHERE milestone_id = @id",
            new { state, id = milestoneId }, cancellationToken: cancellationToken));
    }

    private async Task RecordRefereeDecisionAsync(int milestoneId, string decision, CancellationToken cancellationToken)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        // Record on the latest submission for this milestone (idempotent — re-decision writes the same values).
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE verifications SET referee_decision = @decision, decided_at = @decidedAt "
            + "WHERE verification_id = (SELECT MAX(verification_id) FROM verifications WHERE milestone_id = @id)",
            new { decision, decidedAt = _clock.UtcNow.UtcDateTime, id = milestoneId },
            cancellationToken: cancellationToken));
    }
}
