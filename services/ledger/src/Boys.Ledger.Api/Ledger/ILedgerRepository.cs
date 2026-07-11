namespace Boys.Ledger.Api.Ledger;

using Boys.Ledger.Domain.Ledger;

/// <summary>The result of posting a plan. <see cref="WasAlreadyApplied"/> is true when the idempotency
/// key had already been posted — the call was a no-op returning the original transaction.</summary>
public sealed record PostedTransaction(Guid TxnId, string IdempotencyKey, bool WasAlreadyApplied);

/// <summary>Persists posting plans and answers balance queries. The single DB gate for money: a plan is
/// written as one atomic transaction (header + lines), idempotent by key, and rejected if it would drive
/// USER_ESCROW negative.</summary>
public interface ILedgerRepository
{
    Task<PostedTransaction> PostAsync(PostingPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Authoritative balance of an account = SUM of its postings.</summary>
    Task<long> GetAccountBalanceAsync(LedgerAccount account, CancellationToken cancellationToken = default);

    /// <summary>Per-account balances restricted to one commitment's transactions.</summary>
    Task<IReadOnlyDictionary<LedgerAccount, long>> GetCommitmentBalancesAsync(
        int commitmentId, CancellationToken cancellationToken = default);
}
