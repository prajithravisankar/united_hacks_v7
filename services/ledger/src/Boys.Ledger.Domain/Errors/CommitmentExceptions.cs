namespace Boys.Ledger.Domain.Errors;

/// <summary>A (state, command) pair not in the transition table — an illegal lifecycle shortcut, such as
/// cashing out without a cleared milestone.</summary>
public sealed class IllegalTransitionException : DomainException
{
    public IllegalTransitionException(string fromState, string command)
        : base("illegal_transition", $"command '{command}' is not allowed from state '{fromState}'")
    {
    }
}

/// <summary>Two transitions raced the same commitment; this one lost the optimistic-concurrency check
/// (the row changed underneath it). Safe to re-read and retry.</summary>
public sealed class ConcurrencyConflictException : DomainException
{
    public ConcurrencyConflictException(int commitmentId)
        : base("conflict", $"commitment {commitmentId} was modified concurrently; re-read and retry")
    {
    }
}

/// <summary>The referenced commitment does not exist.</summary>
public sealed class CommitmentNotFoundException : DomainException
{
    public CommitmentNotFoundException(int commitmentId)
        : base("not_found", $"commitment {commitmentId} not found")
    {
    }
}
