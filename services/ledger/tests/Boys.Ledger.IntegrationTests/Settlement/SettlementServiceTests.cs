namespace Boys.Ledger.IntegrationTests.Settlement;

using Boys.Contracts.Brain.V1;
using Boys.Contracts.Common.V1;
using Boys.Ledger.Api.Commitments;
using Boys.Ledger.Api.Configuration;
using Boys.Ledger.Api.Grpc;
using Boys.Ledger.Api.Infrastructure;
using Boys.Ledger.Api.Ledger;
using Boys.Ledger.Api.Settlement;
using Boys.Ledger.Domain.Abstractions;
using Boys.Ledger.Domain.Commitments;
using Boys.Ledger.Domain.Errors;
using Boys.Ledger.Domain.Ledger;
using Boys.Ledger.Domain.Settlement;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static Boys.Ledger.Domain.Commitments.CommitmentCommand;
using static Boys.Ledger.Domain.Commitments.CommitmentState;

/// <summary>B15 settlement against real SQL Server: the canonical cash-out to the cent, failure, success with
/// a pool-funded bonus, exactly-once under retry and concurrency, and the receipt.</summary>
public sealed class SettlementServiceTests : IClassFixture<SqlServerFixture>
{
    private const long Stake = 10_000;

    private sealed class TestConnectionFactory : IDbConnectionFactory
    {
        public SqlConnection Create() => new(Migrations.DbConfig.BoysConnectionString());
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) => UtcNow = now;

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class FakeBrain : IBrainClient
    {
        private readonly long _navCents;

        public FakeBrain(long navCents) => _navCents = navCents;

        public Task<GoalVerdict> ValidateGoalAsync(ValidateGoalRequest r, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProofVerdict> CheckProofAsync(CheckProofRequest r, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Valuation> GetValuationAsync(GetValuationRequest r, CancellationToken ct = default)
            => Task.FromResult(new Valuation { Nav = new Money { Cents = _navCents, Currency = "USD" } });
    }

    private readonly SqlServerFixture _fx;

    public SettlementServiceTests(SqlServerFixture fx) => _fx = fx;

    private SqlConnection Conn()
    {
        Skip.IfNot(_fx.Available, "SQL Server not reachable");
        return _fx.Open();
    }

    private static string Key() => Guid.NewGuid().ToString("n");

    private static (int Cid, int MilestoneId, DateTimeOffset Deadline) NewCommitment(SqlConnection c)
    {
        var row = c.QuerySingle<(int Id, DateTime Deadline)>(
            "DECLARE @u INT = (SELECT TOP 1 user_id FROM users WHERE role='learner'); "
            + "DECLARE @ch INT = (SELECT TOP 1 charity_id FROM charities); "
            + "INSERT INTO commitments (user_id, goal_text, stake_cents, charity_id, drive_mode, state, deadline) "
            + "OUTPUT INSERTED.commitment_id, INSERTED.deadline "
            + "VALUES (@u, N'Score 90%', @stake, @ch, 'AUTO', 'draft', DATEADD(DAY, 30, SYSUTCDATETIME()));",
            new { stake = Stake });
        var mid = c.ExecuteScalar<int>(
            "INSERT INTO milestones (commitment_id, ordinal, description, target_metric, due_date, state) "
            + "OUTPUT INSERTED.milestone_id VALUES (@cid, 1, N'leg', N'>=90', DATEADD(DAY, 10, SYSUTCDATETIME()), 'pending')",
            new { cid = row.Id });
        return (row.Id, mid, new DateTimeOffset(DateTime.SpecifyKind(row.Deadline, DateTimeKind.Utc), TimeSpan.Zero));
    }

    // Drive a fresh commitment (with a funded escrow) to a target terminal-pre-settle state.
    private (int Cid, SqlCommitmentRepository CRepo, SqlLedgerRepository LRepo, LedgerService LSvc) DriveTo(
        SqlConnection c, CommitmentState target)
    {
        var (cid, _, deadline) = NewCommitment(c);
        var clock = new FixedClock(deadline.AddDays(-1));
        var crepo = new SqlCommitmentRepository(new TestConnectionFactory(), clock);
        var lrepo = new SqlLedgerRepository(new TestConnectionFactory());
        var lsvc = new LedgerService();

        crepo.TransitionAsync(cid, Activate, false, Key()).GetAwaiter().GetResult();
        lrepo.PostAsync(lsvc.DepositAndEscrow(cid, Stake, Key())).GetAwaiter().GetResult();  // fund escrow

        if (target == Failed)
        {
            crepo.TransitionAsync(cid, SubmitProof, false, Key()).GetAwaiter().GetResult();
            crepo.TransitionAsync(cid, RejectMilestone, false, Key()).GetAwaiter().GetResult();
            return (cid, crepo, lrepo, lsvc);
        }

        crepo.TransitionAsync(cid, SubmitProof, false, Key()).GetAwaiter().GetResult();
        crepo.TransitionAsync(cid, ClearMilestone, false, Key()).GetAwaiter().GetResult();
        crepo.TransitionAsync(cid, target == Succeeded ? Complete : CashOut, isFinalLeg: target == Succeeded, Key())
            .GetAwaiter().GetResult();
        return (cid, crepo, lrepo, lsvc);
    }

    private static SettlementService Settlement(
        SqlCommitmentRepository crepo, SqlLedgerRepository lrepo, LedgerService lsvc, long nav) => new(
        new TestConnectionFactory(), crepo, lrepo, lsvc, new FakeBrain(nav),
        Options.Create(new LedgerOptions
        {
            SqlConnectionString = "x",
            BrainGrpcAddress = "x",
            FundStartDate = "2021-08-13",
            FundAsOfDate = "2024-05-19",
        }),
        NullLogger<SettlementService>.Instance);

    [SkippableFact]
    public async Task Canonical_cash_out_pays_146_75_and_balances_to_the_cent()
    {
        using var c = Conn();
        var (cid, crepo, lrepo, lsvc) = DriveTo(c, CashedOut);

        var receipt = await Settlement(crepo, lrepo, lsvc, nav: 15_500).SettleAsync(cid);

        receipt.TakeHomeCents.Should().Be(14_675);
        receipt.CarryCents.Should().Be(825);
        (await crepo.GetAsync(cid)).State.Should().Be(Settled);

        var balances = await lrepo.GetCommitmentBalancesAsync(cid);
        balances[LedgerAccount.UserEscrow].Should().Be(0);       // deposited then released
        balances[LedgerAccount.UserYield].Should().Be(14_675);
        balances[LedgerAccount.HouseCarry].Should().Be(825);
        balances[LedgerAccount.ActionPool].Should().Be(-15_500); // -10000 deposit, -5500 gain
        balances.Values.Sum().Should().Be(0);                    // conservation for this commitment
    }

    [SkippableFact]
    public async Task Settling_twice_settles_once()
    {
        using var c = Conn();
        var (cid, crepo, lrepo, lsvc) = DriveTo(c, CashedOut);
        var settle = Settlement(crepo, lrepo, lsvc, nav: 15_500);

        var first = await settle.SettleAsync(cid);
        var second = await settle.SettleAsync(cid);  // already settled -> returns the same receipt

        second.Should().BeEquivalentTo(first);
        (await lrepo.GetCommitmentBalancesAsync(cid))[LedgerAccount.UserYield].Should().Be(14_675);  // not doubled
        (await SettlementCountAsync(c, cid)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Concurrent_settles_apply_the_money_exactly_once()
    {
        using var c = Conn();
        var (cid, crepo, lrepo, lsvc) = DriveTo(c, CashedOut);
        var settle = Settlement(crepo, lrepo, lsvc, nav: 15_500);

        // Some may lose the state race and throw; the money must still move exactly once.
        await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try
            {
                await settle.SettleAsync(cid);
            }
            catch (DomainException)
            {
                // ConcurrencyConflict/IllegalTransition on the losers is fine — the winner settled.
            }
        }));

        (await lrepo.GetCommitmentBalancesAsync(cid))[LedgerAccount.UserYield].Should().Be(14_675);
        (await SettlementCountAsync(c, cid)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Failure_settlement_splits_90_10_and_forfeits_gain()
    {
        using var c = Conn();
        var (cid, crepo, lrepo, lsvc) = DriveTo(c, Failed);

        var receipt = await Settlement(crepo, lrepo, lsvc, nav: 15_000).SettleAsync(cid);  // gain $50

        receipt.CharityCents.Should().Be(1_000);
        receipt.TakeHomeCents.Should().Be(9_000);
        var balances = await lrepo.GetCommitmentBalancesAsync(cid);
        balances[LedgerAccount.UserYield].Should().Be(9_000);
        balances[LedgerAccount.CharityPayable].Should().Be(1_000);
        balances[LedgerAccount.WinnersBonusPool].Should().Be(5_000);  // yield forfeited
        balances.Values.Sum().Should().Be(0);
    }

    [SkippableFact]
    public async Task Success_settlement_pays_a_bonus_from_the_pool()
    {
        using var c = Conn();
        var (cid, crepo, lrepo, lsvc) = DriveTo(c, Succeeded);
        // Seed the (global) winners pool so a bonus is available.
        await lrepo.PostAsync(lsvc.BuildTransfer(null, new[]
        {
            new Posting(LedgerAccount.WinnersBonusPool, +100_000),
            new Posting(LedgerAccount.ActionPool, -100_000),
        }, Key()));

        var receipt = await Settlement(crepo, lrepo, lsvc, nav: 15_500).SettleAsync(cid);

        receipt.BonusCents.Should().Be(1_000);                 // 10% of principal, pool plentiful
        receipt.TakeHomeCents.Should().Be(14_675 + 1_000);
        (await lrepo.GetCommitmentBalancesAsync(cid))[LedgerAccount.UserYield].Should().Be(15_675);
    }

    [SkippableFact]
    public async Task Concurrent_success_settlements_never_over_draw_the_winners_pool()
    {
        // R-audit regression: the bonus draw is capped against the shared pool under a global lock, so N
        // Successes settling at once can never drive WINNERS_BONUS_POOL negative.
        using var c = Conn();
        var lrepo = new SqlLedgerRepository(new TestConnectionFactory());
        var lsvc = new LedgerService();

        // Set the GLOBAL pool to a known small amount that covers only ~2 of the 5 bonuses.
        var poolBefore = await lrepo.GetAccountBalanceAsync(LedgerAccount.WinnersBonusPool);
        await lrepo.PostAsync(lsvc.BuildTransfer(null, new[]
        {
            new Posting(LedgerAccount.WinnersBonusPool, 2_500 - poolBefore),  // drive global pool to exactly 2500
            new Posting(LedgerAccount.ActionPool, poolBefore - 2_500),
        }, Key()));

        // Five commitments, each Succeeded, each wanting a 1000-cent bonus (only 2 can be paid in full).
        var settlers = Enumerable.Range(0, 5).Select(_ =>
        {
            var (cid, crepo, lr, ls) = DriveTo(c, Succeeded);
            return Settlement(crepo, lr, ls, nav: 15_500).SettleAsync(cid);
        }).ToArray();
        await Task.WhenAll(settlers);

        (await lrepo.GetAccountBalanceAsync(LedgerAccount.WinnersBonusPool)).Should().BeGreaterThanOrEqualTo(0);
    }

    [SkippableFact]
    public async Task A_commitment_that_is_not_terminal_cannot_be_settled()
    {
        using var c = Conn();
        var (cid, _, deadline) = NewCommitment(c);
        var clock = new FixedClock(deadline.AddDays(-1));
        var crepo = new SqlCommitmentRepository(new TestConnectionFactory(), clock);
        await crepo.TransitionAsync(cid, Activate, false, Key());  // active, not settle-able

        var settle = Settlement(crepo, new SqlLedgerRepository(new TestConnectionFactory()), new LedgerService(), 15_500);
        var act = () => settle.SettleAsync(cid);

        await act.Should().ThrowAsync<IllegalTransitionException>();
    }

    private static async Task<int> SettlementCountAsync(SqlConnection c, int commitmentId)
        => await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM settlements WHERE commitment_id = @id", new { id = commitmentId });
}
