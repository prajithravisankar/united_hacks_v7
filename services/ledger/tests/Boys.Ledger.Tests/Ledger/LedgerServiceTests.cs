namespace Boys.Ledger.Tests.Ledger;

using Boys.Ledger.Domain.Errors;
using Boys.Ledger.Domain.Ledger;
using FluentAssertions;
using Xunit;

/// <summary>B12 pure invariants: plans are balanced, the protected principal never rides, and malformed
/// requests are refused before anything is built. No database — this is the domain contract.</summary>
public class LedgerServiceTests
{
    private readonly LedgerService _ledger = new();

    [Fact]
    public void DepositAndEscrow_moves_action_pool_into_escrow_and_balances()
    {
        var plan = _ledger.DepositAndEscrow(commitmentId: 1, stakeCents: 10_000, "dep-1");

        plan.Postings.Should().BeEquivalentTo(new[]
        {
            new Posting(LedgerAccount.ActionPool, -10_000),
            new Posting(LedgerAccount.UserEscrow, +10_000),
        });
        plan.Postings.Sum(p => p.DeltaCents).Should().Be(0);
    }

    [Fact]
    public void ReleaseEscrow_moves_escrow_into_the_destination()
    {
        var plan = _ledger.ReleaseEscrow(commitmentId: 1, LedgerAccount.UserYield, amountCents: 10_000, "rel-1");

        plan.Postings.Should().BeEquivalentTo(new[]
        {
            new Posting(LedgerAccount.UserEscrow, -10_000),
            new Posting(LedgerAccount.UserYield, +10_000),
        });
    }

    [Fact]
    public void Unbalanced_group_is_rejected_and_no_plan_exists()
    {
        var build = () => _ledger.BuildTransfer(commitmentId: 1, new[]
        {
            new Posting(LedgerAccount.ActionPool, -10_000),
            new Posting(LedgerAccount.UserEscrow, +9_999),  // off by one cent
        }, "bad-1");

        build.Should().Throw<UnbalancedPostingsException>();
    }

    [Fact]
    public void Principal_Never_Enters_Action_Pool()
    {
        // The named invariant: any group that debits escrow while crediting the action pool is rejected.
        var build = () => _ledger.BuildTransfer(commitmentId: 1, new[]
        {
            new Posting(LedgerAccount.UserEscrow, -10_000),
            new Posting(LedgerAccount.ActionPool, +10_000),
        }, "ride-principal");

        build.Should().Throw<EscrowViolationException>();
    }

    [Fact]
    public void ReleaseEscrow_into_the_action_pool_is_rejected_by_the_same_rule()
    {
        var build = () => _ledger.ReleaseEscrow(commitmentId: 1, LedgerAccount.ActionPool, 10_000, "rel-action");

        build.Should().Throw<EscrowViolationException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_amounts_are_refused(long amount)
    {
        var deposit = () => _ledger.DepositAndEscrow(1, amount, "z");
        var release = () => _ledger.ReleaseEscrow(1, LedgerAccount.UserYield, amount, "z");

        deposit.Should().Throw<LedgerValidationException>();
        release.Should().Throw<LedgerValidationException>();
    }

    [Fact]
    public void Escrow_posting_without_a_commitment_is_refused()
    {
        // R-audit regression: a USER_ESCROW debit with no commitmentId would let the persistence guard
        // measure global escrow while committing into a scope no per-commitment guard can see — releasing
        // principal twice. Rejected at construction, so the exploit plan can't even be built.
        var build = () => _ledger.BuildTransfer(commitmentId: null, new[]
        {
            new Posting(LedgerAccount.UserEscrow, -10_000),
            new Posting(LedgerAccount.UserYield, +10_000),
        }, "orphan-escrow");

        build.Should().Throw<LedgerValidationException>();
    }

    [Fact]
    public void Non_escrow_global_transfers_are_still_allowed()
    {
        // Genesis-style pool moves that never touch escrow may be commitment-less.
        var build = () => _ledger.BuildTransfer(commitmentId: null, new[]
        {
            new Posting(LedgerAccount.WinnersBonusPool, -5_000),
            new Posting(LedgerAccount.ActionPool, +5_000),
        }, "genesis-1");

        build.Should().NotThrow();
    }

    [Fact]
    public void Empty_or_keyless_plans_are_refused()
    {
        var noKey = () => _ledger.BuildTransfer(1, new[] { new Posting(LedgerAccount.HouseCarry, 0) }, "  ");
        var noPostings = () => _ledger.BuildTransfer(1, Array.Empty<Posting>(), "k");

        noKey.Should().Throw<LedgerValidationException>();
        noPostings.Should().Throw<LedgerValidationException>();
    }

    [Fact]
    public void Random_balanced_transfers_conserve_total_money()
    {
        // Seeded (deterministic) property test: a long sequence of zero-sum transfers keeps the sum across
        // all six accounts at exactly zero, and every generated plan is valid by construction.
        var rng = new Random(20260711);
        var accounts = (LedgerAccount[])Enum.GetValues(typeof(LedgerAccount));
        var balances = accounts.ToDictionary(a => a, _ => 0L);

        for (var i = 0; i < 5_000; i++)
        {
            var from = accounts[rng.Next(accounts.Length)];
            var to = accounts[rng.Next(accounts.Length)];
            if (from == to || (from == LedgerAccount.UserEscrow && to == LedgerAccount.ActionPool))
            {
                continue;  // skip self-transfer and the one forbidden direction
            }

            var amount = rng.Next(1, 1_000_000);
            var plan = _ledger.BuildTransfer(commitmentId: 1, new[]  // scoped, so escrow postings are valid
            {
                new Posting(from, -amount),
                new Posting(to, +amount),
            }, $"prop-{i}");

            foreach (var posting in plan.Postings)
            {
                balances[posting.Account] += posting.DeltaCents;
            }
        }

        balances.Values.Sum().Should().Be(0);  // no money created or destroyed, ever
    }
}
