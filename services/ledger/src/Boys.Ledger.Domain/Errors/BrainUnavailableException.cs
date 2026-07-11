namespace Boys.Ledger.Domain.Errors;

/// <summary>brain (the quant/referee service) could not be reached, timed out, or the circuit is open.
/// Callers degrade gracefully (serve a "degraded" response) instead of failing the whole request —
/// the resilience hook the verification and settlement flows lean on. The transport cause is kept in
/// <see cref="Exception.InnerException"/> for server-side logging only (never serialized to a caller).</summary>
public sealed class BrainUnavailableException : DomainException
{
    public BrainUnavailableException(string message = "brain service is unavailable", Exception? inner = null)
        : base("brain_unavailable", message, inner)
    {
    }
}
