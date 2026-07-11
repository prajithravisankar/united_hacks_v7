namespace Boys.Ledger.Domain.Ledger;

using Boys.Ledger.Domain.Errors;

/// <summary>Builds validated posting plans for the money flows. Pure — no I/O, no clock. Every flow in the
/// product (deposit, cash-out, ride, success, failure) is composed from these primitives and persisted
/// through the one repository gate, so invariants are enforced in exactly one place.</summary>
public sealed class LedgerService
{
    /// <summary>Deposit-on-activation: the platform escrows the user's stake, funded from the action-pool
    /// float. Direction is action → escrow (never escrow → action), so the "principal never rides" rule
    /// is satisfied by construction.</summary>
    public PostingPlan DepositAndEscrow(int commitmentId, long stakeCents, string idempotencyKey)
    {
        RequirePositive(stakeCents, nameof(stakeCents));
        return new PostingPlan(idempotencyKey, commitmentId, new[]
        {
            new Posting(LedgerAccount.ActionPool, -stakeCents),
            new Posting(LedgerAccount.UserEscrow, +stakeCents),
        });
    }

    /// <summary>Release escrowed principal to a destination account (e.g. USER_YIELD on cash-out,
    /// CHARITY_PAYABLE on failure). Releasing into ACTION_POOL is rejected by the escrow rule.</summary>
    public PostingPlan ReleaseEscrow(int commitmentId, LedgerAccount to, long amountCents, string idempotencyKey)
    {
        RequirePositive(amountCents, nameof(amountCents));
        return new PostingPlan(idempotencyKey, commitmentId, new[]
        {
            new Posting(LedgerAccount.UserEscrow, -amountCents),
            new Posting(to, +amountCents),
        });
    }

    /// <summary>General validated builder — the seam B15 composes settlement recipes through. Validation
    /// (balanced, escrow-inviolable) happens in the <see cref="PostingPlan"/> constructor.</summary>
    public PostingPlan BuildTransfer(int? commitmentId, IReadOnlyList<Posting> postings, string idempotencyKey)
        => new(idempotencyKey, commitmentId, postings);

    private static void RequirePositive(long amountCents, string name)
    {
        if (amountCents <= 0)
        {
            throw new LedgerValidationException($"{name} must be positive (was {amountCents})");
        }
    }
}
