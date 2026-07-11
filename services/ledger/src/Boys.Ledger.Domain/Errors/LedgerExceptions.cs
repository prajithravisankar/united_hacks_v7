namespace Boys.Ledger.Domain.Errors;

/// <summary>A posting group whose signed deltas do not sum to zero — money would be created or destroyed.</summary>
public sealed class UnbalancedPostingsException : DomainException
{
    public UnbalancedPostingsException(long residualCents)
        : base("unbalanced_postings", $"postings must sum to zero; off by {residualCents} cents")
    {
    }
}

/// <summary>The "principal never rides" invariant, as an exception: a group that moves USER_ESCROW into
/// ACTION_POOL is rejected — the protected floor can never be put at play.</summary>
public sealed class EscrowViolationException : DomainException
{
    public EscrowViolationException()
        : base("escrow_violation", "the protected principal (USER_ESCROW) may never move into ACTION_POOL")
    {
    }
}

/// <summary>A transaction that would drive USER_ESCROW below zero — you cannot release more principal
/// than is escrowed. Guarded at persistence against the live balance.</summary>
public sealed class NegativeBalanceException : DomainException
{
    public NegativeBalanceException(long attemptedBalanceCents)
        : base("negative_balance", $"USER_ESCROW would go negative ({attemptedBalanceCents} cents); rejected")
    {
    }
}

/// <summary>A malformed request the domain refuses before building a plan (non-positive amounts, etc.).</summary>
public sealed class LedgerValidationException : DomainException
{
    public LedgerValidationException(string message)
        : base("validation", message)
    {
    }
}

/// <summary>Could not acquire the per-commitment post lock in time — a transient conflict, safe to retry.</summary>
public sealed class IdempotencyConflictException : DomainException
{
    public IdempotencyConflictException(string resource)
        : base("idempotency_conflict", $"could not serialize the post for {resource}; retry")
    {
    }
}

/// <summary>A posting would draw the winners bonus pool below zero — the pool cannot cover the draw. The
/// caller (settlement) recomputes a smaller bonus from the live balance and retries.</summary>
public sealed class InsufficientPoolException : DomainException
{
    public InsufficientPoolException(long balanceCents, long drawCents)
        : base("insufficient_pool", $"winners pool has {balanceCents} cents; cannot draw {drawCents}")
    {
    }
}
