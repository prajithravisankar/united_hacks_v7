namespace Boys.Ledger.Api.PublicApi;

using Boys.Contracts.Brain.V1;
using Boys.Ledger.Api.Commitments;
using Boys.Ledger.Api.Grpc;
using Boys.Ledger.Api.Infrastructure;
using Boys.Ledger.Api.Ledger;
using Boys.Ledger.Domain.Abstractions;
using Boys.Ledger.Domain.Commitments;
using Boys.Ledger.Domain.Errors;
using Boys.Ledger.Domain.Ledger;
using Dapper;

/// <summary>The outcome of a goal-creation attempt. When <see cref="Accepted"/> is false the AI gate wants a
/// revision and <see cref="SuggestedRewrite"/> carries it (the endpoint returns 422 + the rewrite).</summary>
public sealed record GoalCreationResult(
    bool Accepted, int? CommitmentId, string Verdict, string SuggestedRewrite, string Reasoning, bool Degraded);

/// <summary>Creates goals through the AI SMART-goal gate and activates them (escrow + state). Product limits
/// are validated here with rule-naming messages before the DB CHECK constraints ever fire.</summary>
public sealed class GoalService
{
    public const long MinStakeCents = 2_000;
    public const long MaxStakeCents = 50_000;

    private readonly IDbConnectionFactory _factory;
    private readonly IBrainClient _brain;
    private readonly ICommitmentRepository _commitments;
    private readonly ILedgerRepository _ledger;
    private readonly LedgerService _ledgerService;
    private readonly IClock _clock;
    private readonly ILogger<GoalService> _logger;

    public GoalService(
        IDbConnectionFactory factory,
        IBrainClient brain,
        ICommitmentRepository commitments,
        ILedgerRepository ledger,
        LedgerService ledgerService,
        IClock clock,
        ILogger<GoalService> logger)
    {
        _factory = factory;
        _brain = brain;
        _commitments = commitments;
        _ledger = ledger;
        _ledgerService = ledgerService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<GoalCreationResult> CreateAsync(int userId, CreateGoalRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);

        var (verdict, rewrite, reasoning, degraded) = await RunGoalGateAsync(request, cancellationToken);
        if (!degraded && verdict is Verdict.Reject or Verdict.Revise)
        {
            return new GoalCreationResult(false, null, verdict.ToString(), rewrite, reasoning, degraded);  // needs a revision
        }

        var commitmentId = await InsertCommitmentAsync(userId, request, cancellationToken);
        _logger.LogInformation("created goal {CommitmentId} for user {UserId} (ai={Verdict} degraded={Degraded})",
            commitmentId, userId, verdict, degraded);
        return new GoalCreationResult(true, commitmentId, verdict.ToString(), rewrite, reasoning, degraded);
    }

    public async Task<CommitmentState> ActivateAsync(int commitmentId, CancellationToken cancellationToken = default)
    {
        var stake = await GetStakeAsync(commitmentId, cancellationToken);
        var key = $"activate:{commitmentId}";  // derived -> activating twice is idempotent

        // Escrow the stake, then move to active. Both keyed by the same derived key.
        await _ledger.PostAsync(_ledgerService.DepositAndEscrow(commitmentId, stake, key), cancellationToken);
        var result = await _commitments.TransitionAsync(
            commitmentId, CommitmentCommand.Activate, isFinalLeg: false, key, cancellationToken);
        return result.ToState;
    }

    private void Validate(CreateGoalRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.GoalText))
        {
            throw new LedgerValidationException("goal_text is required");
        }

        if (request.StakeCents < MinStakeCents || request.StakeCents > MaxStakeCents)
        {
            throw new LedgerValidationException($"stake must be between {MinStakeCents} and {MaxStakeCents} cents ($20–$500)");
        }

        if (request.Milestones is null || request.Milestones.Count is < 1 or > 5)
        {
            throw new LedgerValidationException("a goal must have between 1 and 5 milestones");
        }

        var now = _clock.UtcNow;
        if (request.Deadline < now.AddDays(7) || request.Deadline > now.AddMonths(6))
        {
            throw new LedgerValidationException("deadline must be between 1 week and 6 months from now");
        }
    }

    private async Task<(Verdict Verdict, string Rewrite, string Reasoning, bool Degraded)> RunGoalGateAsync(
        CreateGoalRequest request, CancellationToken cancellationToken)
    {
        var gate = new ValidateGoalRequest { GoalText = request.GoalText, Deadline = request.Deadline.ToString("yyyy-MM-dd") };
        var ordinal = 1;
        foreach (var m in request.Milestones)
        {
            gate.Milestones.Add(new MilestoneSpec
            {
                Ordinal = ordinal++,
                Description = m.Description,
                TargetMetric = m.TargetMetric,
                DueDate = m.DueDate.ToString("yyyy-MM-dd"),
            });
        }

        try
        {
            var verdict = await _brain.ValidateGoalAsync(gate, cancellationToken);
            return (verdict.Verdict, verdict.SuggestedRewrite, verdict.Reasoning, false);
        }
        catch (BrainUnavailableException)
        {
            // Degraded: don't block goal creation on the AI gate — accept and let a human review later.
            _logger.LogWarning("brain unavailable during goal gate; accepting (degraded)");
            return (Verdict.Unspecified, string.Empty, "AI gate unavailable — accepted pending review", true);
        }
    }

    private async Task<int> InsertCommitmentAsync(int userId, CreateGoalRequest request, CancellationToken cancellationToken)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        var commitmentId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "INSERT INTO commitments (user_id, goal_text, stake_cents, charity_id, drive_mode, state, deadline) "
            + "OUTPUT INSERTED.commitment_id "
            + "VALUES (@userId, @goal, @stake, @charity, @drive, 'draft', @deadline)",
            new
            {
                userId,
                goal = request.GoalText,
                stake = request.StakeCents,
                charity = request.CharityId,
                drive = request.DriveMode,
                deadline = request.Deadline.UtcDateTime,
            }, tx, cancellationToken: cancellationToken));

        var ordinal = 1;
        foreach (var m in request.Milestones)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO milestones (commitment_id, ordinal, description, target_metric, due_date, state) "
                + "VALUES (@commitmentId, @ordinal, @desc, @target, @due, 'pending')",
                new { commitmentId, ordinal = ordinal++, desc = m.Description, target = m.TargetMetric, due = m.DueDate.UtcDateTime },
                tx, cancellationToken: cancellationToken));
        }

        await tx.CommitAsync(cancellationToken);
        return commitmentId;
    }

    private async Task<long> GetStakeAsync(int commitmentId, CancellationToken cancellationToken)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<(long Stake, string State)?>(new CommandDefinition(
            "SELECT stake_cents, state FROM commitments WHERE commitment_id = @id",
            new { id = commitmentId }, cancellationToken: cancellationToken));
        if (row is null)
        {
            throw new CommitmentNotFoundException(commitmentId);
        }

        return row.Value.Stake;
    }
}
