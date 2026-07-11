namespace Boys.Ledger.IntegrationTests.Ledger;

using Boys.Ledger.Api.Infrastructure;
using Boys.Ledger.Api.Ledger;
using Boys.Ledger.Domain.Errors;
using Boys.Ledger.Domain.Ledger;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

/// <summary>B12 persistence invariants against real SQL Server: atomic posting, idempotency (including
/// under concurrency), the negative-escrow guard, and balance = sum of postings.</summary>
public sealed class LedgerRepositoryTests : IClassFixture<SqlServerFixture>
{
    private sealed class TestConnectionFactory : IDbConnectionFactory
    {
        public SqlConnection Create() => new(Migrations.DbConfig.BoysConnectionString());
    }

    private readonly SqlServerFixture _fx;
    private readonly LedgerService _ledger = new();
    private readonly ILedgerRepository _repo = new SqlLedgerRepository(new TestConnectionFactory());

    public LedgerRepositoryTests(SqlServerFixture fx) => _fx = fx;

    private SqlConnection Conn()
    {
        Skip.IfNot(_fx.Available, "SQL Server not reachable");
        return _fx.Open();
    }

    private static int NewCommitment(SqlConnection c) => c.ExecuteScalar<int>(
        "DECLARE @u INT = (SELECT TOP 1 user_id FROM users WHERE role='learner'); "
        + "DECLARE @ch INT = (SELECT TOP 1 charity_id FROM charities); "
        + "INSERT INTO commitments (user_id, goal_text, stake_cents, charity_id, drive_mode, state, deadline) "
        + "OUTPUT INSERTED.commitment_id "
        + "VALUES (@u, N'Score 90% in History', 10000, @ch, 'AUTO', 'draft', DATEADD(DAY, 30, SYSUTCDATETIME()));");

    private static string Key() => Guid.NewGuid().ToString("n");

    [SkippableFact]
    public async Task Deposit_escrows_the_stake_and_records_balances()
    {
        using var c = Conn();
        var commitmentId = NewCommitment(c);

        await _repo.PostAsync(_ledger.DepositAndEscrow(commitmentId, 10_000, Key()));

        var balances = await _repo.GetCommitmentBalancesAsync(commitmentId);
        balances[LedgerAccount.UserEscrow].Should().Be(10_000);
        balances[LedgerAccount.ActionPool].Should().Be(-10_000);  // action pool fronts the float
    }

    [SkippableFact]
    public async Task Same_idempotency_key_posted_twice_is_a_noop()
    {
        using var c = Conn();
        var commitmentId = NewCommitment(c);
        var plan = _ledger.DepositAndEscrow(commitmentId, 10_000, Key());

        var first = await _repo.PostAsync(plan);
        var second = await _repo.PostAsync(plan);

        first.WasAlreadyApplied.Should().BeFalse();
        second.WasAlreadyApplied.Should().BeTrue();
        second.TxnId.Should().Be(first.TxnId);  // same original transaction returned
        (await _repo.GetCommitmentBalancesAsync(commitmentId))[LedgerAccount.UserEscrow]
            .Should().Be(10_000);  // applied once, not twice
    }

    [SkippableFact]
    public async Task Concurrent_posts_of_the_same_key_apply_exactly_once()
    {
        using var c = Conn();
        var commitmentId = NewCommitment(c);
        var plan = _ledger.DepositAndEscrow(commitmentId, 10_000, Key());

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => _repo.PostAsync(plan)));

        results.Count(r => !r.WasAlreadyApplied).Should().Be(1);  // exactly one writer won
        results.Select(r => r.TxnId).Distinct().Should().HaveCount(1);  // all report the same txn
        (await _repo.GetCommitmentBalancesAsync(commitmentId))[LedgerAccount.UserEscrow]
            .Should().Be(10_000);
    }

    [SkippableFact]
    public async Task Releasing_more_than_escrowed_is_rejected_and_nothing_moves()
    {
        using var c = Conn();
        var commitmentId = NewCommitment(c);
        await _repo.PostAsync(_ledger.DepositAndEscrow(commitmentId, 10_000, Key()));

        var overRelease = () => _repo.PostAsync(
            _ledger.ReleaseEscrow(commitmentId, LedgerAccount.UserYield, 20_000, Key()));

        await overRelease.Should().ThrowAsync<NegativeBalanceException>();
        (await _repo.GetCommitmentBalancesAsync(commitmentId))[LedgerAccount.UserEscrow]
            .Should().Be(10_000);  // unchanged — the failed post wrote nothing
    }

    [SkippableFact]
    public async Task Balance_equals_the_sum_of_postings_across_a_sequence()
    {
        using var c = Conn();
        var commitmentId = NewCommitment(c);

        await _repo.PostAsync(_ledger.DepositAndEscrow(commitmentId, 10_000, Key()));
        await _repo.PostAsync(_ledger.ReleaseEscrow(commitmentId, LedgerAccount.UserYield, 3_000, Key()));
        await _repo.PostAsync(_ledger.DepositAndEscrow(commitmentId, 5_000, Key()));

        var balances = await _repo.GetCommitmentBalancesAsync(commitmentId);
        balances[LedgerAccount.UserEscrow].Should().Be(12_000);   // 10000 - 3000 + 5000
        balances[LedgerAccount.UserYield].Should().Be(3_000);
        balances[LedgerAccount.ActionPool].Should().Be(-15_000);  // -10000 - 5000
        balances.Values.Sum().Should().Be(0);                     // conservation across this commitment
    }
}
