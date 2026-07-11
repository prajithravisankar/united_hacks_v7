namespace Boys.Ledger.Tests.Settlement;

using Boys.Ledger.Domain.Commitments;
using Boys.Ledger.Domain.Errors;
using Boys.Ledger.Domain.Ledger;
using Boys.Ledger.Domain.Settlement;
using FluentAssertions;
using Xunit;
using static Boys.Ledger.Domain.Commitments.CommitmentCommand;
using static Boys.Ledger.Domain.Commitments.CommitmentState;

/// <summary>R4 adversarial money hunt — every "can you break the money?" attack, named, expecting rejection.
/// The pure-domain attacks live here (fast, deterministic); the persistence-level attacks (settle twice,
/// proof after deadline, referee replay, concurrent over-draw) are named tests in the integration suites.</summary>
public class MoneyHuntTests
{
    private readonly LedgerService _ledger = new();

    [Fact]
    public void Attack_cash_out_without_a_cleared_milestone_is_rejected()
    {
        // You can only cash out from milestone_cleared (non-final). From anywhere else it throws.
        foreach (var from in new[] { Draft, Active, PendingVerification, Riding })
        {
            var attack = () => CommitmentMachine.Next(from, CashOut, isFinalLeg: false);
            attack.Should().Throw<IllegalTransitionException>($"cannot cash out from {from}");
        }
    }

    [Fact]
    public void Attack_ride_past_the_final_leg_is_rejected()
    {
        var attack = () => CommitmentMachine.Next(MilestoneCleared, Ride, isFinalLeg: true);
        attack.Should().Throw<IllegalTransitionException>();
    }

    [Fact]
    public void Attack_settle_before_reaching_a_terminal_state_is_rejected()
    {
        foreach (var from in new[] { Draft, Active, PendingVerification, MilestoneCleared, Riding })
        {
            var attack = () => CommitmentMachine.Next(from, Settle, isFinalLeg: false);
            attack.Should().Throw<IllegalTransitionException>();
        }
    }

    [Fact]
    public void Attack_post_an_unbalanced_group_is_rejected()
    {
        var attack = () => _ledger.BuildTransfer(1, new[]
        {
            new Posting(LedgerAccount.ActionPool, -10_000),
            new Posting(LedgerAccount.UserYield, +9_999),  // one cent short
        }, "attack");
        attack.Should().Throw<UnbalancedPostingsException>();
    }

    [Fact]
    public void Attack_drive_escrow_into_the_action_pool_is_rejected()
    {
        var attack = () => _ledger.BuildTransfer(1, new[]
        {
            new Posting(LedgerAccount.UserEscrow, -10_000),
            new Posting(LedgerAccount.ActionPool, +10_000),  // principal must never ride
        }, "attack");
        attack.Should().Throw<EscrowViolationException>();
    }

    [Fact]
    public void Attack_negative_or_zero_stake_is_rejected()
    {
        var zero = () => _ledger.DepositAndEscrow(1, 0, "attack");
        var negative = () => _ledger.DepositAndEscrow(1, -10_000, "attack");
        zero.Should().Throw<LedgerValidationException>();
        negative.Should().Throw<LedgerValidationException>();
    }

    [Fact]
    public void Attack_negative_nav_settlement_still_floors_at_principal()
    {
        // A fund wipeout (NAV below principal, even negative) must never pay the user less than principal,
        // must take zero carry, and must still balance.
        var plan = SettlementCalculator.CashOut(principalCents: 10_000, navCents: -5_000);

        plan.Receipt.TakeHomeCents.Should().Be(10_000);   // floor holds even at a negative NAV
        plan.Receipt.CarryCents.Should().Be(0);
        plan.Postings.Sum(p => p.DeltaCents).Should().Be(0);
        var construct = () => new PostingPlan("k", 1, plan.Postings);
        construct.Should().NotThrow();
    }

    [Fact]
    public void Attack_failure_never_pays_the_house_and_charity_is_exactly_ten_percent()
    {
        var plan = SettlementCalculator.Failure(principalCents: 10_000, navCents: 20_000);  // big "gain" but failed

        plan.Postings.Where(p => p.Account == LedgerAccount.HouseCarry).Sum(p => p.DeltaCents).Should().Be(0);
        plan.Receipt.CharityCents.Should().Be(1_000);   // exactly 10%
        plan.Receipt.TakeHomeCents.Should().Be(9_000);  // 90% back
    }
}
