namespace Boys.Ledger.IntegrationTests.Verification;

using System.Text;
using Boys.Contracts.Brain.V1;
using Boys.Ledger.Api.Commitments;
using Boys.Ledger.Api.Configuration;
using Boys.Ledger.Api.Grpc;
using Boys.Ledger.Api.Infrastructure;
using Boys.Ledger.Api.Verification;
using Boys.Ledger.Domain.Abstractions;
using Boys.Ledger.Domain.Commitments;
using Boys.Ledger.Domain.Errors;
using Dapper;
using FluentAssertions;
using global::Grpc.Net.Client;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>B14 proof loop against real SQL Server, with the brain client faked at the gRPC interface:
/// happy path, resubmission, referee overruling the AI both ways, idempotency, authorization, the degraded
/// (brain-down) path, and evidence validation — plus one real cross-service round-trip to the brain container.</summary>
public sealed class VerificationWorkflowTests : IClassFixture<SqlServerFixture>
{
    private static readonly byte[] Evidence = Encoding.UTF8.GetBytes("a grade screenshot");
    private const string Mime = "image/png";

    private sealed class TestConnectionFactory : IDbConnectionFactory
    {
        public SqlConnection Create() => new(Migrations.DbConfig.BoysConnectionString());
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) => UtcNow = now;

        public DateTimeOffset UtcNow { get; }
    }

    // A brain that returns a canned proof verdict, or simulates brain being down.
    private sealed class FakeBrain : IBrainClient
    {
        private readonly ProofVerdict? _proof;
        private readonly bool _unavailable;

        public FakeBrain(ProofVerdict? proof = null, bool unavailable = false)
        {
            _proof = proof;
            _unavailable = unavailable;
        }

        public Task<GoalVerdict> ValidateGoalAsync(ValidateGoalRequest r, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProofVerdict> CheckProofAsync(CheckProofRequest r, CancellationToken ct = default)
            => _unavailable ? throw new BrainUnavailableException() : Task.FromResult(_proof!);

        public Task<Valuation> GetValuationAsync(GetValuationRequest r, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static ProofVerdict Supported() => new()
    { SupportsClaim = true, Confidence = 0.9, Reasoning = "the screenshot shows the grade", InsufficiencyReason = "" };

    private static ProofVerdict Insufficient() => new()
    { SupportsClaim = false, Confidence = 0.3, Reasoning = "too blurry", InsufficiencyReason = "cannot read the grade" };

    private readonly SqlServerFixture _fx;

    public VerificationWorkflowTests(SqlServerFixture fx) => _fx = fx;

    private SqlConnection Conn()
    {
        Skip.IfNot(_fx.Available, "SQL Server not reachable");
        return _fx.Open();
    }

    private static string Key() => Guid.NewGuid().ToString("n");

    private static (int CommitmentId, DateTimeOffset Deadline, int[] MilestoneIds) NewCommitment(SqlConnection c, int milestones)
    {
        var row = c.QuerySingle<(int Id, DateTime Deadline)>(
            "DECLARE @u INT = (SELECT TOP 1 user_id FROM users WHERE role='learner'); "
            + "DECLARE @ch INT = (SELECT TOP 1 charity_id FROM charities); "
            + "INSERT INTO commitments (user_id, goal_text, stake_cents, charity_id, drive_mode, state, deadline) "
            + "OUTPUT INSERTED.commitment_id, INSERTED.deadline "
            + "VALUES (@u, N'Score 90% in History', 10000, @ch, 'AUTO', 'draft', DATEADD(DAY, 30, SYSUTCDATETIME()));");

        var ids = new int[milestones];
        for (var i = 0; i < milestones; i++)
        {
            ids[i] = c.ExecuteScalar<int>(
                "INSERT INTO milestones (commitment_id, ordinal, description, target_metric, due_date, state) "
                + "OUTPUT INSERTED.milestone_id "
                + "VALUES (@cid, @ord, N'leg', N'>=90', DATEADD(DAY, @d, SYSUTCDATETIME()), 'pending')",
                new { cid = row.Id, ord = i + 1, d = (i + 1) * 5 });
        }

        return (row.Id, new DateTimeOffset(DateTime.SpecifyKind(row.Deadline, DateTimeKind.Utc), TimeSpan.Zero), ids);
    }

    private static int RefereeUserId(SqlConnection c) =>
        c.ExecuteScalar<int>("SELECT TOP 1 user_id FROM users WHERE role='referee'");

    private static int LearnerUserId(SqlConnection c) =>
        c.ExecuteScalar<int>("SELECT TOP 1 user_id FROM users WHERE role='learner'");

    private static IEvidenceStore EvidenceStore() =>
        new LocalEvidenceStore(Options.Create(new LedgerOptions
        {
            SqlConnectionString = "x",
            BrainGrpcAddress = "x",
            EvidenceDir = Path.Combine(Path.GetTempPath(), "boys-evidence-test"),
        }));

    // Build an activated commitment + a service wired to the given brain, sharing a before-deadline clock.
    private (VerificationService Svc, SqlCommitmentRepository Repo) Activated(
        SqlConnection c, IBrainClient brain, int commitmentId, DateTimeOffset deadline)
    {
        var clock = new FixedClock(deadline.AddDays(-1));
        var repo = new SqlCommitmentRepository(new TestConnectionFactory(), clock);
        repo.TransitionAsync(commitmentId, CommitmentCommand.Activate, false, Key()).GetAwaiter().GetResult();
        var svc = new VerificationService(
            new TestConnectionFactory(), repo, brain, EvidenceStore(), clock, NullLogger<VerificationService>.Instance);
        return (svc, repo);
    }

    [SkippableFact]
    public async Task Happy_path_ai_supports_referee_approves_clears_the_milestone()
    {
        using var c = Conn();
        var (cid, deadline, mids) = NewCommitment(c, 1);
        var (svc, _) = Activated(c, new FakeBrain(Supported()), cid, deadline);

        var submit = await svc.SubmitProofAsync(cid, mids[0], "Scored 92", Evidence, Mime, Key());
        submit.CommitmentState.Should().Be(CommitmentState.PendingVerification);
        submit.AiVerdict.Status.Should().Be(AiVerdictStatus.Supported);

        var decide = await svc.RefereeDecideAsync(mids[0], RefereeDecision.Approve, RefereeUserId(c), Key());
        decide.CommitmentState.Should().Be(CommitmentState.MilestoneCleared);
        decide.MilestoneState.Should().Be("cleared");
    }

    [SkippableFact]
    public async Task Ai_insufficient_keeps_it_pending_and_resubmission_reruns_the_ai()
    {
        using var c = Conn();
        var (cid, deadline, mids) = NewCommitment(c, 1);
        var (svc, _) = Activated(c, new FakeBrain(Insufficient()), cid, deadline);

        var first = await svc.SubmitProofAsync(cid, mids[0], "Scored 92", Evidence, Mime, Key());
        first.AiVerdict.Status.Should().Be(AiVerdictStatus.Insufficient);
        first.AiVerdict.InsufficiencyReason.Should().Be("cannot read the grade");  // reason surfaced
        first.CommitmentState.Should().Be(CommitmentState.PendingVerification);
        first.ResubmissionCount.Should().Be(1);

        // Resubmit with a clearer screenshot — the brain now supports it; the count advances.
        var svc2 = new VerificationService(
            new TestConnectionFactory(),
            new SqlCommitmentRepository(new TestConnectionFactory(), new FixedClock(deadline.AddDays(-1))),
            new FakeBrain(Supported()), EvidenceStore(),
            new FixedClock(deadline.AddDays(-1)), NullLogger<VerificationService>.Instance);
        var second = await svc2.SubmitProofAsync(cid, mids[0], "Scored 92", Evidence, Mime, Key());
        second.AiVerdict.Status.Should().Be(AiVerdictStatus.Supported);
        second.ResubmissionCount.Should().Be(2);
        second.CommitmentState.Should().Be(CommitmentState.PendingVerification);  // no double transition
    }

    [SkippableFact]
    public async Task Referee_overrules_ai_yes_to_reject_fails_the_commitment()
    {
        using var c = Conn();
        var (cid, deadline, mids) = NewCommitment(c, 1);
        var (svc, _) = Activated(c, new FakeBrain(Supported()), cid, deadline);
        await svc.SubmitProofAsync(cid, mids[0], "Scored 92", Evidence, Mime, Key());

        var decide = await svc.RefereeDecideAsync(mids[0], RefereeDecision.Reject, RefereeUserId(c), Key());

        decide.CommitmentState.Should().Be(CommitmentState.Failed);  // human authority is absolute
        decide.MilestoneState.Should().Be("failed");
    }

    [SkippableFact]
    public async Task Referee_overrules_ai_no_to_approve_clears_the_milestone()
    {
        using var c = Conn();
        var (cid, deadline, mids) = NewCommitment(c, 1);
        var (svc, _) = Activated(c, new FakeBrain(Insufficient()), cid, deadline);
        await svc.SubmitProofAsync(cid, mids[0], "Scored 92", Evidence, Mime, Key());

        var decide = await svc.RefereeDecideAsync(mids[0], RefereeDecision.Approve, RefereeUserId(c), Key());

        decide.CommitmentState.Should().Be(CommitmentState.MilestoneCleared);
    }

    [SkippableFact]
    public async Task Referee_decision_is_idempotent_on_double_click()
    {
        using var c = Conn();
        var (cid, deadline, mids) = NewCommitment(c, 1);
        var (svc, _) = Activated(c, new FakeBrain(Supported()), cid, deadline);
        await svc.SubmitProofAsync(cid, mids[0], "Scored 92", Evidence, Mime, Key());
        var key = Key();

        var first = await svc.RefereeDecideAsync(mids[0], RefereeDecision.Approve, RefereeUserId(c), key);
        var second = await svc.RefereeDecideAsync(mids[0], RefereeDecision.Approve, RefereeUserId(c), key);

        first.WasApplied.Should().BeTrue();
        second.WasApplied.Should().BeFalse();
        second.CommitmentState.Should().Be(CommitmentState.MilestoneCleared);
    }

    [SkippableFact]
    public async Task Non_referee_may_not_decide()
    {
        using var c = Conn();
        var (cid, deadline, mids) = NewCommitment(c, 1);
        var (svc, _) = Activated(c, new FakeBrain(Supported()), cid, deadline);
        await svc.SubmitProofAsync(cid, mids[0], "Scored 92", Evidence, Mime, Key());

        var byLearner = () => svc.RefereeDecideAsync(mids[0], RefereeDecision.Approve, LearnerUserId(c), Key());

        await byLearner.Should().ThrowAsync<ForbiddenException>();
    }

    [SkippableFact]
    public async Task Brain_down_accepts_the_submission_as_pending_ai_and_the_referee_can_still_decide()
    {
        using var c = Conn();
        var (cid, deadline, mids) = NewCommitment(c, 1);
        var (svc, _) = Activated(c, new FakeBrain(unavailable: true), cid, deadline);

        var submit = await svc.SubmitProofAsync(cid, mids[0], "Scored 92", Evidence, Mime, Key());
        submit.AiVerdict.Status.Should().Be(AiVerdictStatus.PendingAi);
        submit.AiVerdict.Degraded.Should().BeTrue();
        submit.CommitmentState.Should().Be(CommitmentState.PendingVerification);  // accepted despite brain down

        var decide = await svc.RefereeDecideAsync(mids[0], RefereeDecision.Approve, RefereeUserId(c), Key());
        decide.CommitmentState.Should().Be(CommitmentState.MilestoneCleared);  // manual decision still works
    }

    [SkippableFact]
    public async Task Oversized_and_unsupported_evidence_are_rejected_before_anything_is_stored()
    {
        using var c = Conn();
        var (cid, deadline, mids) = NewCommitment(c, 1);
        var (svc, _) = Activated(c, new FakeBrain(Supported()), cid, deadline);

        var oversized = () => svc.SubmitProofAsync(
            cid, mids[0], "Scored 92", new byte[VerificationService.MaxEvidenceBytes + 1], Mime, Key());
        var badMime = () => svc.SubmitProofAsync(cid, mids[0], "Scored 92", Evidence, "application/x-msdownload", Key());

        await oversized.Should().ThrowAsync<OversizedEvidenceException>();
        await badMime.Should().ThrowAsync<UnsupportedMimeException>();
    }

    [SkippableFact]
    public async Task Real_brain_round_trip_returns_a_non_degraded_verdict()
    {
        using var c = Conn();
        var (cid, deadline, mids) = NewCommitment(c, 1);
        var clock = new FixedClock(deadline.AddDays(-1));
        var repo = new SqlCommitmentRepository(new TestConnectionFactory(), clock);
        await repo.TransitionAsync(cid, CommitmentCommand.Activate, false, Key());

        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:50061");
        var brain = new BrainClient(
            new QuantService.QuantServiceClient(channel),
            new RefereeService.RefereeServiceClient(channel),
            clock,
            Options.Create(new LedgerOptions { SqlConnectionString = "x", BrainGrpcAddress = "http://127.0.0.1:50061", BrainTimeoutMs = 5000 }),
            NullLogger<BrainClient>.Instance);
        var svc = new VerificationService(
            new TestConnectionFactory(), repo, brain, EvidenceStore(), clock, NullLogger<VerificationService>.Instance);

        var result = await svc.SubmitProofAsync(cid, mids[0], "Scored 90% on the midterm", Evidence, Mime, Key());

        // If brain is down the service degrades gracefully; that's a skip, not a failure.
        Skip.If(result.AiVerdict.Degraded, "brain container not reachable on :50061");
        result.AiVerdict.Status.Should().BeOneOf(AiVerdictStatus.Supported, AiVerdictStatus.Insufficient);
    }
}
