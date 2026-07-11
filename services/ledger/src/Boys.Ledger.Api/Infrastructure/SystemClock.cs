namespace Boys.Ledger.Api.Infrastructure;

using Boys.Ledger.Domain.Abstractions;

/// <summary>The production clock. The only place the domain touches the wall-clock — tests inject a
/// fixed <see cref="IClock"/> so deadline gates and settlement are reproducible to the second.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
