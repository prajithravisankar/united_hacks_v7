namespace Boys.Ledger.Domain.Ledger;

/// <summary>The six ledger accounts — the closed system every cent moves within. Balances are signed
/// integer cents; the sum across all six is conserved by every (balanced) transaction.</summary>
public enum LedgerAccount
{
    /// <summary>The user's protected principal — the floor. Never rides the fund, never goes negative.</summary>
    UserEscrow,

    /// <summary>The platform float / pooled capital that backs commitments and pays realized gains.</summary>
    ActionPool,

    /// <summary>The user's realized winnings, payable out to them.</summary>
    UserYield,

    /// <summary>Forfeited stakes from quitters — funds winners' bonuses ("winners funded by quitters").</summary>
    WinnersBonusPool,

    /// <summary>Money owed to the user's chosen charity (the 10% on failure).</summary>
    CharityPayable,

    /// <summary>The platform's carry — 15% of gains only, never principal, never charity money.</summary>
    HouseCarry,
}

/// <summary>Maps the domain enum to/from the exact DB strings in <c>ledger_accounts</c>.</summary>
public static class LedgerAccounts
{
    private static readonly IReadOnlyDictionary<LedgerAccount, string> ToDbName = new Dictionary<LedgerAccount, string>
    {
        [LedgerAccount.UserEscrow] = "USER_ESCROW",
        [LedgerAccount.ActionPool] = "ACTION_POOL",
        [LedgerAccount.UserYield] = "USER_YIELD",
        [LedgerAccount.WinnersBonusPool] = "WINNERS_BONUS_POOL",
        [LedgerAccount.CharityPayable] = "CHARITY_PAYABLE",
        [LedgerAccount.HouseCarry] = "HOUSE_CARRY",
    };

    private static readonly IReadOnlyDictionary<string, LedgerAccount> FromDbName =
        ToDbName.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string ToDb(this LedgerAccount account) => ToDbName[account];

    public static LedgerAccount FromDb(string name) =>
        FromDbName.TryGetValue(name, out var account)
            ? account
            : throw new ArgumentOutOfRangeException(nameof(name), name, "unknown ledger account");

    public static bool TryFromDb(string name, out LedgerAccount account) =>
        FromDbName.TryGetValue(name, out account);

    public static IReadOnlyCollection<LedgerAccount> All => (LedgerAccount[])Enum.GetValues(typeof(LedgerAccount));
}
