namespace Boys.Ledger.Domain.Ledger;

using Boys.Ledger.Domain.Errors;

/// <summary>A validated group of postings ready to persist as one transaction. Construction enforces the
/// two structural money invariants, so an invalid plan cannot exist: (1) deltas sum to zero — no money
/// created or destroyed; (2) the protected principal never moves into the action pool. The remaining
/// invariant (USER_ESCROW never negative) needs the live balance, so it is enforced at persistence.</summary>
public sealed class PostingPlan
{
    public string IdempotencyKey { get; }
    public int? CommitmentId { get; }
    public IReadOnlyList<Posting> Postings { get; }

    public PostingPlan(string idempotencyKey, int? commitmentId, IReadOnlyList<Posting> postings)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new LedgerValidationException("idempotency key is required");
        }

        if (postings is null || postings.Count == 0)
        {
            throw new LedgerValidationException("a transaction needs at least one posting");
        }

        Validate(postings);

        // Escrow is the protected principal of a specific commitment — a USER_ESCROW posting with no
        // commitment is nonsensical, and it would also let the persistence guard measure the wrong scope
        // (global vs per-commitment) and over-release principal. Forbid it at the source.
        if (commitmentId is null && postings.Any(p => p.Account == LedgerAccount.UserEscrow))
        {
            throw new LedgerValidationException("a USER_ESCROW posting must belong to a commitment");
        }

        IdempotencyKey = idempotencyKey;
        CommitmentId = commitmentId;
        Postings = postings;
    }

    private static void Validate(IReadOnlyList<Posting> postings)
    {
        long residual = 0;
        var escrowDebited = false;   // principal leaving escrow
        var actionCredited = false;  // money entering the action pool

        foreach (var posting in postings)
        {
            residual += posting.DeltaCents;

            if (posting.Account == LedgerAccount.UserEscrow && posting.DeltaCents < 0)
            {
                escrowDebited = true;
            }

            if (posting.Account == LedgerAccount.ActionPool && posting.DeltaCents > 0)
            {
                actionCredited = true;
            }
        }

        if (residual != 0)
        {
            throw new UnbalancedPostingsException(residual);
        }

        // "Principal never rides": a single group must never both debit escrow and credit the action pool.
        if (escrowDebited && actionCredited)
        {
            throw new EscrowViolationException();
        }
    }
}
