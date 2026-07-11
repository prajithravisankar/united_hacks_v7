namespace Boys.Ledger.Domain.Errors;

/// <summary>Base for every expected domain-rule violation. Carries a stable machine <see cref="Code"/>
/// the HTTP edge maps into the standard error envelope — a raw stack trace never reaches a caller.</summary>
public abstract class DomainException : Exception
{
    protected DomainException(string code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    /// <summary>Stable, snake_case error code (e.g. "brain_unavailable"). Part of the API contract.</summary>
    public string Code { get; }
}
