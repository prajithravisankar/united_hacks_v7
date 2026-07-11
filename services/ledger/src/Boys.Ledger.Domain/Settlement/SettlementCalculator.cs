namespace Boys.Ledger.Domain.Settlement;

using Boys.Ledger.Domain.Ledger;

/// <summary>How a commitment ends, money-wise.</summary>
public enum SettlementType
{
    CashOut,   // graceful exit at a verified milestone
    Success,   // final goal hit — cash-out plus a bonus from the winners pool
    Failure,   // missed a gate — 90% back to the user, 10% to charity, yield forfeited
}

/// <summary>A human-readable breakdown of a settlement — the "receipt" the demo shows and the review audits.
/// All amounts are cents.</summary>
public sealed record SettlementReceipt(
    SettlementType Type,
    long PrincipalCents,
    long NavCents,
    long GainCents,       // nav − principal (may be negative)
    long CarryCents,      // 15% of a positive gain, else 0
    long CharityCents,    // 10% of principal on failure, else 0
    long BonusCents,      // success bonus drawn from the winners pool, else 0
    long TakeHomeCents);  // total paid to the user (floored at principal on non-failure paths)

/// <summary>The postings to apply (balanced) plus the receipt to record.</summary>
public sealed record SettlementPlan(IReadOnlyList<Posting> Postings, SettlementReceipt Receipt);

/// <summary>The result of riding: play the next leg from the new base, but the protected floor stays the
/// ORIGINAL principal.</summary>
public sealed record RideResult(long NewBaseCents, long FloorCents);

/// <summary>The money endgame, exact to the cent. Pure — takes the principal and the current NAV and returns
/// a balanced posting plan + receipt. This is the ledger's money authority: it computes carry, floor, and the
/// splits itself (brain supplies only the NAV), so every promise in idea.md ("never lose your deposit",
/// "never profit from failure", "carry on gains only") is enforced here and tested to death.</summary>
public static class SettlementCalculator
{
    /// <summary>Cash out at a verified milestone: principal + gain − 15% carry, floored at principal.</summary>
    public static SettlementPlan CashOut(long principalCents, long navCents)
    {
        var gain = navCents - principalCents;
        var carry = MoneyMath.CarryOnGain(gain);
        var takeHome = Math.Max(principalCents, principalCents + gain - carry);  // floor

        var postings = new List<Posting>();
        AddCashOutPostings(postings, principalCents, gain, carry, takeHome);

        return new SettlementPlan(postings, new SettlementReceipt(
            SettlementType.CashOut, principalCents, navCents, gain, carry, CharityCents: 0, BonusCents: 0, takeHome));
    }

    /// <summary>Succeed: cash-out plus a bonus drawn from the winners pool (10% of principal, capped at the
    /// available pool so it is never over-drawn).</summary>
    public static SettlementPlan Success(long principalCents, long navCents, long winnersBonusPoolCents)
    {
        var gain = navCents - principalCents;
        var carry = MoneyMath.CarryOnGain(gain);
        var takeHome = Math.Max(principalCents, principalCents + gain - carry);

        var target = MoneyMath.RoundHalfEven(principalCents * MoneyMath.SuccessBonusRate);
        var bonus = Math.Min(target, Math.Max(0, winnersBonusPoolCents));  // never over-draw the pool

        var postings = new List<Posting>();
        AddCashOutPostings(postings, principalCents, gain, carry, takeHome);
        Add(postings, LedgerAccount.WinnersBonusPool, -bonus);
        Add(postings, LedgerAccount.UserYield, +bonus);

        return new SettlementPlan(postings, new SettlementReceipt(
            SettlementType.Success, principalCents, navCents, gain, carry, CharityCents: 0, bonus, takeHome + bonus));
    }

    /// <summary>Fail: 90% of principal back to the user, exactly 10% to charity (banker's-rounded, the user
    /// takes the remainder so no cent is lost), and any positive yield forfeited entirely to the winners pool
    /// (0 to the house — we never profit from failure).</summary>
    public static SettlementPlan Failure(long principalCents, long navCents)
    {
        var gain = navCents - principalCents;
        var charity = MoneyMath.CharityCut(principalCents);
        var userShare = principalCents - charity;  // exact remainder — no lost cents

        var postings = new List<Posting>
        {
            new(LedgerAccount.UserEscrow, -principalCents),
            new(LedgerAccount.UserYield, +userShare),
            new(LedgerAccount.CharityPayable, +charity),
        };

        if (gain > 0)
        {
            // Positive yield is forfeited to the community/winners pool — funds the people who finish.
            Add(postings, LedgerAccount.ActionPool, -gain);
            Add(postings, LedgerAccount.WinnersBonusPool, +gain);
        }

        return new SettlementPlan(postings, new SettlementReceipt(
            SettlementType.Failure, principalCents, navCents, gain, CarryCents: 0, charity, BonusCents: 0, userShare));
    }

    /// <summary>Ride: the base you play from next becomes the current NAV (earnings compound), but the
    /// protected floor stays the ORIGINAL principal — you only ever risk your winnings.</summary>
    public static RideResult Ride(long principalCents, long navCents) => new(navCents, principalCents);

    /// <summary>Release the principal from escrow and realize the gain (funded from the action pool) net of
    /// carry, flooring the user at principal. Zero-delta lines are omitted so the group is minimal.</summary>
    private static void AddCashOutPostings(List<Posting> postings, long principalCents, long gain, long carry, long takeHome)
    {
        // Balancing plug on the action pool: for a positive gain this is −gain (the house pays the winnings);
        // for a floored loss it is 0 (the principal in escrow already covers the floor).
        var actionDelta = principalCents - takeHome - carry;

        Add(postings, LedgerAccount.UserEscrow, -principalCents);
        Add(postings, LedgerAccount.UserYield, +takeHome);
        Add(postings, LedgerAccount.HouseCarry, +carry);
        Add(postings, LedgerAccount.ActionPool, actionDelta);
    }

    private static void Add(List<Posting> postings, LedgerAccount account, long delta)
    {
        if (delta != 0)
        {
            postings.Add(new Posting(account, delta));
        }
    }
}
