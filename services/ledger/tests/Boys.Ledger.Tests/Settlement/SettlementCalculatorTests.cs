namespace Boys.Ledger.Tests.Settlement;

using Boys.Ledger.Domain.Ledger;
using Boys.Ledger.Domain.Settlement;
using FluentAssertions;
using Xunit;

/// <summary>B15 money math — the densest tests in the repo. Every promise in idea.md's "three ways it can
/// end" is a named test, and the carry/floor/conservation invariants are property-tested over ≥1000 seeded
/// scenarios. Pure — no database.</summary>
public class SettlementCalculatorTests
{
    private static long Net(SettlementPlan plan, LedgerAccount account) =>
        plan.Postings.Where(p => p.Account == account).Sum(p => p.DeltaCents);

    // Every settlement plan must be a valid (balanced, escrow-inviolable) posting group.
    private static void AssertValid(SettlementPlan plan)
    {
        plan.Postings.Sum(p => p.DeltaCents).Should().Be(0, "settlement postings must balance");
        var construct = () => new PostingPlan("settle-key", 1, plan.Postings);
        construct.Should().NotThrow();  // balanced + never moves principal into the action pool
    }

    // ---- the canonical demo example (idea.md) ----

    [Fact]
    public void Canonical_cash_out_100_to_155_pays_146_75_and_carries_8_25()
    {
        var plan = SettlementCalculator.CashOut(principalCents: 10_000, navCents: 15_500);

        plan.Receipt.CarryCents.Should().Be(825);         // $8.25 — 15% of the $55 gain
        plan.Receipt.TakeHomeCents.Should().Be(14_675);   // $146.75 — principal + gain − carry
        Net(plan, LedgerAccount.UserEscrow).Should().Be(-10_000);   // escrow emptied
        Net(plan, LedgerAccount.UserYield).Should().Be(14_675);
        Net(plan, LedgerAccount.HouseCarry).Should().Be(825);
        Net(plan, LedgerAccount.ActionPool).Should().Be(-5_500);    // the fund pays the gain
        AssertValid(plan);
    }

    // ---- carry: gains only ----

    [Fact]
    public void Zero_gain_takes_zero_carry()
    {
        var plan = SettlementCalculator.CashOut(10_000, 10_000);
        plan.Receipt.CarryCents.Should().Be(0);
        plan.Receipt.TakeHomeCents.Should().Be(10_000);
        AssertValid(plan);
    }

    [Fact]
    public void Negative_yield_takes_zero_carry_and_floors_at_principal()
    {
        var plan = SettlementCalculator.CashOut(10_000, 9_000);  // fund down $10
        plan.Receipt.CarryCents.Should().Be(0);
        plan.Receipt.TakeHomeCents.Should().Be(10_000);         // exactly principal — never less
        Net(plan, LedgerAccount.HouseCarry).Should().Be(0);
        AssertValid(plan);
    }

    // ---- failure: 90/10, never profit from failure ----

    [Fact]
    public void Failure_splits_principal_90_10_and_forfeits_gain_to_the_winners_pool()
    {
        var plan = SettlementCalculator.Failure(principalCents: 10_000, navCents: 15_000);  // gain $50

        plan.Receipt.CharityCents.Should().Be(1_000);            // exactly 10%
        plan.Receipt.TakeHomeCents.Should().Be(9_000);           // 90% back
        Net(plan, LedgerAccount.CharityPayable).Should().Be(1_000);
        Net(plan, LedgerAccount.UserYield).Should().Be(9_000);
        Net(plan, LedgerAccount.WinnersBonusPool).Should().Be(5_000);  // 100% of yield forfeited
        Net(plan, LedgerAccount.HouseCarry).Should().Be(0);            // 0 to the house
        AssertValid(plan);
    }

    [Fact]
    public void Failure_odd_cent_stake_loses_no_cents()
    {
        var plan = SettlementCalculator.Failure(principalCents: 3_333, navCents: 3_333);  // $33.33
        plan.Receipt.CharityCents.Should().Be(333);   // banker's-rounded 10% of 3333
        plan.Receipt.TakeHomeCents.Should().Be(3_000);
        (plan.Receipt.CharityCents + plan.Receipt.TakeHomeCents).Should().Be(3_333);  // no lost cents
        AssertValid(plan);
    }

    [Fact]
    public void Failure_with_negative_yield_takes_no_forfeit_only_the_principal_split()
    {
        var plan = SettlementCalculator.Failure(10_000, 8_000);  // fund down
        Net(plan, LedgerAccount.WinnersBonusPool).Should().Be(0);  // nothing to forfeit
        Net(plan, LedgerAccount.ActionPool).Should().Be(0);
        plan.Receipt.CharityCents.Should().Be(1_000);
        plan.Receipt.TakeHomeCents.Should().Be(9_000);
        AssertValid(plan);
    }

    // ---- ride: compound the base, keep the original floor ----

    [Fact]
    public void Ride_rebases_to_nav_and_keeps_the_original_floor_across_three_legs()
    {
        var floor = 10_000L;

        var leg1 = SettlementCalculator.Ride(principalCents: floor, navCents: 15_000);
        leg1.NewBaseCents.Should().Be(15_000);
        leg1.FloorCents.Should().Be(floor);

        var leg2 = SettlementCalculator.Ride(principalCents: floor, navCents: 22_000);  // grew from the 15k base
        leg2.NewBaseCents.Should().Be(22_000);
        leg2.FloorCents.Should().Be(floor);

        var leg3 = SettlementCalculator.Ride(principalCents: floor, navCents: 31_000);
        leg3.NewBaseCents.Should().Be(31_000);
        leg3.FloorCents.Should().Be(floor);  // still the ORIGINAL principal after N rides
    }

    // ---- success: bonus from the pool, never over-drawn ----

    [Fact]
    public void Success_pays_cash_out_plus_a_bonus_from_the_winners_pool()
    {
        var plan = SettlementCalculator.Success(principalCents: 10_000, navCents: 15_500, winnersBonusPoolCents: 100_000);

        plan.Receipt.BonusCents.Should().Be(1_000);              // 10% of principal, pool is plentiful
        plan.Receipt.TakeHomeCents.Should().Be(14_675 + 1_000);  // cash-out take-home + bonus
        Net(plan, LedgerAccount.WinnersBonusPool).Should().Be(-1_000);
        AssertValid(plan);
    }

    [Fact]
    public void Success_bonus_is_capped_at_the_available_pool()
    {
        var plan = SettlementCalculator.Success(10_000, 15_500, winnersBonusPoolCents: 250);  // pool nearly empty
        plan.Receipt.BonusCents.Should().Be(250);   // capped — never over-drawn
        AssertValid(plan);

        var empty = SettlementCalculator.Success(10_000, 15_500, winnersBonusPoolCents: 0);
        empty.Receipt.BonusCents.Should().Be(0);
        AssertValid(empty);
    }

    // ---- property tests over seeded scenarios ----

    [Fact]
    public void Carry_is_always_on_gains_only_and_the_user_never_receives_less_than_principal()
    {
        var rng = new Random(20260715);
        for (var i = 0; i < 2_000; i++)
        {
            var principal = rng.Next(2_000, 50_000);     // product stake range
            var nav = rng.Next(0, 120_000);              // fund can be up or down (even to zero)
            var plan = SettlementCalculator.CashOut(principal, nav);

            var gain = nav - principal;
            plan.Receipt.CarryCents.Should().Be(gain > 0 ? MoneyMath.RoundHalfEven(gain * 0.15m) : 0);
            plan.Receipt.CarryCents.Should().BeLessThanOrEqualTo(Math.Max(0, gain));  // never exceeds the gain
            plan.Receipt.TakeHomeCents.Should().BeGreaterThanOrEqualTo(principal);    // floor holds, always
            AssertValid(plan);
        }
    }

    [Fact]
    public void Every_full_scenario_conserves_money_to_zero()
    {
        // deposit (action -> escrow) then settle: the sum of all account deltas is exactly zero, and escrow
        // returns to zero. No money is created or destroyed on any path.
        var rng = new Random(20260716);
        for (var i = 0; i < 3_000; i++)
        {
            var principal = rng.Next(2_000, 50_000);
            var nav = rng.Next(0, 120_000);
            var pool = rng.Next(0, 500_000);

            SettlementPlan settle = (i % 3) switch
            {
                0 => SettlementCalculator.CashOut(principal, nav),
                1 => SettlementCalculator.Success(principal, nav, pool),
                _ => SettlementCalculator.Failure(principal, nav),
            };

            // Deposit: ACTION_POOL -principal, USER_ESCROW +principal (the B12 primitive).
            var deposit = new List<Posting>
            {
                new(LedgerAccount.ActionPool, -principal),
                new(LedgerAccount.UserEscrow, +principal),
            };

            var all = deposit.Concat(settle.Postings).ToList();
            all.Sum(p => p.DeltaCents).Should().Be(0);  // global conservation
            all.Where(p => p.Account == LedgerAccount.UserEscrow).Sum(p => p.DeltaCents)
                .Should().Be(0);                        // escrow escrowed then fully released
            AssertValid(settle);
        }
    }
}
