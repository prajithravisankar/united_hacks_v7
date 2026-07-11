namespace Boys.Ledger.Domain.Abstractions;

/// <summary>The single source of "now" for the domain. Injected everywhere time is read so
/// deadline gates and settlement stay deterministic under test — no wall-clock in domain logic.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
