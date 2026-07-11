namespace Boys.Ledger.IntegrationTests.Commitments;

using Boys.Ledger.Api.Commitments;
using Boys.Ledger.Api.Infrastructure;
using Boys.Ledger.Domain.Abstractions;
using Boys.Ledger.Domain.Commitments;
using Boys.Ledger.Domain.Errors;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;
using static Boys.Ledger.Domain.Commitments.CommitmentCommand;
using static Boys.Ledger.Domain.Commitments.CommitmentState;

/// <summary>B13 persistence: the lifecycle end-to-end, idempotent re-application, optimistic-concurrency
/// (one writer wins), the lazy deadline gate to the tick, and event-trail replay — against real SQL Server.</summary>
public sealed class CommitmentRepositoryTests : IClassFixture<SqlServerFixture>
{
    private sealed class TestConnectionFactory : IDbConnectionFactory
    {
        public SqlConnection Create() => new(Migrations.DbConfig.BoysConnectionString());
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) => UtcNow = now;

        public DateTimeOffset UtcNow { get; }
    }

    private readonly SqlServerFixture _fx;

    public CommitmentRepositoryTests(SqlServerFixture fx) => _fx = fx;

    private SqlConnection Conn()
    {
        Skip.IfNot(_fx.Available, "SQL Server not reachable");
        return _fx.Open();
    }

    private static (int Id, DateTimeOffset Deadline) NewCommitment(SqlConnection c)
    {
        var row = c.QuerySingle<(int Id, DateTime Deadline)>(
            "DECLARE @u INT = (SELECT TOP 1 user_id FROM users WHERE role='learner'); "
            + "DECLARE @ch INT = (SELECT TOP 1 charity_id FROM charities); "
            + "INSERT INTO commitments (user_id, goal_text, stake_cents, charity_id, drive_mode, state, deadline) "
            + "OUTPUT INSERTED.commitment_id, INSERTED.deadline "
            + "VALUES (@u, N'Score 90% in History', 10000, @ch, 'AUTO', 'draft', DATEADD(DAY, 30, SYSUTCDATETIME()));");
        return (row.Id, new DateTimeOffset(DateTime.SpecifyKind(row.Deadline, DateTimeKind.Utc), TimeSpan.Zero));
    }

    // A repository whose clock sits before the deadline (so the deadline never trips).
    private SqlCommitmentRepository RepoBefore(DateTimeOffset deadline) =>
        new(new TestConnectionFactory(), new FixedClock(deadline.AddDays(-1)));

    private static string Key() => Guid.NewGuid().ToString("n");

    [SkippableFact]
    public async Task Full_happy_path_lifecycle_reaches_settled()
    {
        using var c = Conn();
        var (id, deadline) = NewCommitment(c);
        var repo = RepoBefore(deadline);

        (await repo.TransitionAsync(id, Activate, false, Key())).ToState.Should().Be(Active);
        (await repo.TransitionAsync(id, SubmitProof, false, Key())).ToState.Should().Be(PendingVerification);
        (await repo.TransitionAsync(id, ClearMilestone, false, Key())).ToState.Should().Be(MilestoneCleared);
        (await repo.TransitionAsync(id, CashOut, false, Key())).ToState.Should().Be(CashedOut);
        (await repo.TransitionAsync(id, Settle, false, Key())).ToState.Should().Be(Settled);

        (await repo.GetAsync(id)).State.Should().Be(Settled);
    }

    [SkippableFact]
    public async Task Success_path_clears_the_final_leg_into_succeeded()
    {
        using var c = Conn();
        var (id, deadline) = NewCommitment(c);
        var repo = RepoBefore(deadline);

        await repo.TransitionAsync(id, Activate, true, Key());
        await repo.TransitionAsync(id, SubmitProof, true, Key());
        await repo.TransitionAsync(id, ClearMilestone, true, Key());
        (await repo.TransitionAsync(id, Complete, isFinalLeg: true, Key())).ToState.Should().Be(Succeeded);
    }

    [SkippableFact]
    public async Task Illegal_shortcut_is_rejected()
    {
        using var c = Conn();
        var (id, deadline) = NewCommitment(c);
        var repo = RepoBefore(deadline);

        var cashOutFromDraft = () => repo.TransitionAsync(id, CashOut, false, Key());

        await cashOutFromDraft.Should().ThrowAsync<IllegalTransitionException>();
        (await repo.GetAsync(id)).State.Should().Be(Draft);  // unchanged
    }

    [SkippableFact]
    public async Task Same_key_reapplied_is_a_noop()
    {
        using var c = Conn();
        var (id, deadline) = NewCommitment(c);
        var repo = RepoBefore(deadline);
        var key = Key();

        var first = await repo.TransitionAsync(id, Activate, false, key);
        var second = await repo.TransitionAsync(id, Activate, false, key);

        first.WasApplied.Should().BeTrue();
        second.WasApplied.Should().BeFalse();
        second.ToState.Should().Be(Active);
        (await repo.GetEventsAsync(id)).Count(e => e.Command == "activate").Should().Be(1);
    }

    [SkippableFact]
    public async Task Concurrent_transitions_let_exactly_one_win()
    {
        using var c = Conn();
        var (id, deadline) = NewCommitment(c);
        var repo = RepoBefore(deadline);

        // Two writers race the same draft->active with different keys; rowversion lets one win.
        var outcomes = await Task.WhenAll(new[] { Key(), Key() }.Select(async k =>
        {
            try
            {
                return (await repo.TransitionAsync(id, Activate, false, k)).WasApplied;
            }
            catch (DomainException)
            {
                return false;  // ConcurrencyConflict or IllegalTransition — both mean "the other one won"
            }
        }));

        outcomes.Count(applied => applied).Should().Be(1);
        (await repo.GetAsync(id)).State.Should().Be(Active);
        (await repo.GetEventsAsync(id)).Count(e => e.Command == "activate").Should().Be(1);  // applied once
    }

    [SkippableFact]
    public async Task Deadline_trips_a_live_leg_to_failed_lazily_on_read()
    {
        using var c = Conn();
        var (id, deadline) = NewCommitment(c);
        await RepoBefore(deadline).TransitionAsync(id, Activate, false, Key());  // -> active

        // A repo whose clock is one second past the deadline sweeps the read.
        var afterDeadline = new SqlCommitmentRepository(new TestConnectionFactory(), new FixedClock(deadline.AddSeconds(1)));

        (await afterDeadline.GetAsync(id)).State.Should().Be(Failed);
        (await afterDeadline.GetEventsAsync(id)).Should().Contain(e => e.Command == "deadline_gate");

        // A command arriving after the deadline hits a failed commitment -> rejected.
        var lateSubmit = () => afterDeadline.TransitionAsync(id, SubmitProof, false, Key());
        await lateSubmit.Should().ThrowAsync<IllegalTransitionException>();
    }

    [SkippableFact]
    public async Task Command_after_the_deadline_trips_and_is_rejected_in_one_step()
    {
        // R-audit regression: the deadline gate lives inside the command transaction, so a command arriving
        // after the deadline (with no prior read to sweep it) still fails the leg and is rejected atomically.
        using var c = Conn();
        var (id, deadline) = NewCommitment(c);
        await RepoBefore(deadline).TransitionAsync(id, Activate, false, Key());  // -> active, pre-deadline

        var afterDeadline = new SqlCommitmentRepository(new TestConnectionFactory(), new FixedClock(deadline.AddSeconds(1)));
        var lateCommand = () => afterDeadline.TransitionAsync(id, SubmitProof, false, Key());

        await lateCommand.Should().ThrowAsync<IllegalTransitionException>();
        (await afterDeadline.GetAsync(id)).State.Should().Be(Failed);  // the leg failed on the deadline
        (await afterDeadline.GetEventsAsync(id)).Should().Contain(e => e.Command == "deadline_gate");
    }

    [SkippableFact]
    public async Task Caller_may_not_use_a_reserved_system_key()
    {
        // R-audit regression: system events (deadline_gate) use a reserved 'sys:' prefix; a caller can't
        // collide with them and swallow/forge an event.
        using var c = Conn();
        var (id, deadline) = NewCommitment(c);
        var repo = RepoBefore(deadline);

        var reserved = () => repo.TransitionAsync(id, Activate, false, $"sys:deadline-gate:{id}");

        await reserved.Should().ThrowAsync<LedgerValidationException>();
    }

    [SkippableFact]
    public async Task Deadline_boundary_is_strict_to_the_tick()
    {
        using var c = Conn();
        var (id, deadline) = NewCommitment(c);
        await RepoBefore(deadline).TransitionAsync(id, Activate, false, Key());

        // Exactly at the deadline instant: not yet a miss.
        var atDeadline = new SqlCommitmentRepository(new TestConnectionFactory(), new FixedClock(deadline));
        (await atDeadline.GetAsync(id)).State.Should().Be(Active);

        // One tick past: failed.
        var pastDeadline = new SqlCommitmentRepository(new TestConnectionFactory(), new FixedClock(deadline.AddTicks(1)));
        (await pastDeadline.GetAsync(id)).State.Should().Be(Failed);
    }

    [SkippableFact]
    public async Task Event_trail_replays_to_the_final_state()
    {
        using var c = Conn();
        var (id, deadline) = NewCommitment(c);
        var repo = RepoBefore(deadline);

        await repo.TransitionAsync(id, Activate, false, Key());
        await repo.TransitionAsync(id, SubmitProof, false, Key());
        await repo.TransitionAsync(id, ClearMilestone, false, Key());
        await repo.TransitionAsync(id, Ride, false, Key());

        var events = await repo.GetEventsAsync(id);

        // Replay the recorded commands through the pure machine from the initial state.
        var replayed = Draft;
        foreach (var e in events)
        {
            CommitmentStates.FromDb(e.FromState).Should().Be(replayed);  // trail is contiguous
            replayed = CommitmentStates.FromDb(e.ToState);
        }

        replayed.Should().Be(Riding);
        replayed.Should().Be((await repo.GetAsync(id)).State);  // trail reconstructs the live state
    }
}
