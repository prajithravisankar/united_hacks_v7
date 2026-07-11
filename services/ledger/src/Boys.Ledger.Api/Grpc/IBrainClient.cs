namespace Boys.Ledger.Api.Grpc;

using Boys.Contracts.Brain.V1;

/// <summary>The ledger's one door to brain. Every method either returns brain's answer or throws
/// <see cref="Boys.Ledger.Domain.Errors.BrainUnavailableException"/> — callers never see a raw
/// <c>RpcException</c>, so degraded-mode handling lives in exactly one place per flow.</summary>
public interface IBrainClient
{
    /// <summary>SMART-goal gate (referee AI first pass).</summary>
    Task<GoalVerdict> ValidateGoalAsync(ValidateGoalRequest request, CancellationToken cancellationToken = default);

    /// <summary>Milestone proof check (referee AI first pass).</summary>
    Task<ProofVerdict> CheckProofAsync(CheckProofRequest request, CancellationToken cancellationToken = default);

    /// <summary>Carry/floor-aware valuation of a commitment's action pool.</summary>
    Task<Valuation> GetValuationAsync(GetValuationRequest request, CancellationToken cancellationToken = default);
}
