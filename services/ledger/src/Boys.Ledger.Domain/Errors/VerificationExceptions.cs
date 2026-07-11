namespace Boys.Ledger.Domain.Errors;

/// <summary>Submitted evidence exceeds the maximum inline size.</summary>
public sealed class OversizedEvidenceException : DomainException
{
    public OversizedEvidenceException(int actualBytes, int maxBytes)
        : base("oversized_evidence", $"evidence is {actualBytes} bytes; the limit is {maxBytes}")
    {
    }
}

/// <summary>Submitted evidence has a MIME type the referee cannot verify.</summary>
public sealed class UnsupportedMimeException : DomainException
{
    public UnsupportedMimeException(string mime)
        : base("unsupported_mime", $"evidence type '{mime}' is not supported")
    {
    }
}

/// <summary>The acting user is not permitted to perform this action (e.g. a non-referee deciding a proof).</summary>
public sealed class ForbiddenException : DomainException
{
    public ForbiddenException(string message)
        : base("forbidden", message)
    {
    }
}

/// <summary>The referenced milestone does not exist.</summary>
public sealed class MilestoneNotFoundException : DomainException
{
    public MilestoneNotFoundException(int milestoneId)
        : base("not_found", $"milestone {milestoneId} not found")
    {
    }
}
