namespace Boys.Ledger.Api.Grpc;

using Boys.Contracts.Brain.V1;
using Boys.Ledger.Api.Configuration;
using Boys.Ledger.Domain.Abstractions;
using Boys.Ledger.Domain.Errors;
using global::Grpc.Core;
using Microsoft.Extensions.Options;

/// <summary>Wraps the generated quant + referee clients: applies a per-call deadline (off <see cref="IClock"/>)
/// and translates any transport failure into a single <see cref="BrainUnavailableException"/>. The
/// resilience handler (retry + circuit breaker + attempt timeout) is layered on the underlying HttpClient
/// in DI; this class is the final "gRPC error → domain error" boundary.</summary>
public sealed class BrainClient : IBrainClient
{
    private readonly QuantService.QuantServiceClient _quant;
    private readonly RefereeService.RefereeServiceClient _referee;
    private readonly IClock _clock;
    private readonly TimeSpan _timeout;
    private readonly ILogger<BrainClient> _logger;

    public BrainClient(
        QuantService.QuantServiceClient quant,
        RefereeService.RefereeServiceClient referee,
        IClock clock,
        IOptions<LedgerOptions> options,
        ILogger<BrainClient> logger)
    {
        _quant = quant;
        _referee = referee;
        _clock = clock;
        _timeout = TimeSpan.FromMilliseconds(options.Value.BrainTimeoutMs);
        _logger = logger;
    }

    public Task<GoalVerdict> ValidateGoalAsync(ValidateGoalRequest request, CancellationToken cancellationToken = default)
        => CallAsync(() => _referee.ValidateGoalAsync(request, Options(cancellationToken)));

    public Task<ProofVerdict> CheckProofAsync(CheckProofRequest request, CancellationToken cancellationToken = default)
        => CallAsync(() => _referee.CheckProofAsync(request, Options(cancellationToken)));

    public Task<Valuation> GetValuationAsync(GetValuationRequest request, CancellationToken cancellationToken = default)
        => CallAsync(() => _quant.GetValuationAsync(request, Options(cancellationToken)));

    private CallOptions Options(CancellationToken cancellationToken)
        => new(deadline: _clock.UtcNow.UtcDateTime.Add(_timeout), cancellationToken: cancellationToken);

    private async Task<T> CallAsync<T>(Func<AsyncUnaryCall<T>> call)
    {
        try
        {
            using var pending = call();
            return await pending.ResponseAsync;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "brain gRPC call failed with status {Status}", ex.StatusCode);
            throw new BrainUnavailableException(inner: ex);
        }
    }
}
