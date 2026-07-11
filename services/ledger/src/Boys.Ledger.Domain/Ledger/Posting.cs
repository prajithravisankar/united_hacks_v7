namespace Boys.Ledger.Domain.Ledger;

/// <summary>One line of a transaction: a signed cent delta against one account. Positive credits the
/// account, negative debits it. A transaction is a group of these whose deltas sum to zero.</summary>
public readonly record struct Posting(LedgerAccount Account, long DeltaCents);
