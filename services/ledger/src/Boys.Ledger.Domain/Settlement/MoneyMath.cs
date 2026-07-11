namespace Boys.Ledger.Domain.Settlement;

/// <summary>Money rounding for the ledger. Banker's rounding (round-half-to-even) on exact decimals — the
/// same policy brain uses (Python Decimal ROUND_HALF_EVEN) — so the two services never disagree by a cent.</summary>
public static class MoneyMath
{
    public const decimal CarryRate = 0.15m;          // 15% of gains, never principal
    public const decimal CharityRate = 0.10m;        // 10% of principal to charity on failure
    public const decimal SuccessBonusRate = 0.10m;   // success bonus target = 10% of principal, capped at the pool

    /// <summary>Round an exact decimal cent amount to whole cents, half-to-even.</summary>
    public static long RoundHalfEven(decimal cents) => (long)Math.Round(cents, MidpointRounding.ToEven);

    /// <summary>Carry on gains only: 15% of the gain when positive, else zero. Never on principal.</summary>
    public static long CarryOnGain(long gainCents) =>
        gainCents > 0 ? RoundHalfEven(gainCents * CarryRate) : 0;

    /// <summary>The charity slice on failure: 10% of principal, banker's-rounded. The user takes the exact
    /// remainder (principal − charity), so odd-cent stakes lose no cents.</summary>
    public static long CharityCut(long principalCents) => RoundHalfEven(principalCents * CharityRate);
}
