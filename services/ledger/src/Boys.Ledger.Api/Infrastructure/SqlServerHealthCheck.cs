namespace Boys.Ledger.Api.Infrastructure;

using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>Readiness probe: opens a real connection and runs <c>SELECT 1</c>. Reachable → Healthy;
/// anything else → Unhealthy (never throws). This is what makes <c>/health/ready</c> track SQL Server
/// truthfully — compose gates dependents on it, and the demo can prove "DB down → not ready".</summary>
public sealed class SqlServerHealthCheck : IHealthCheck
{
    private readonly IDbConnectionFactory _factory;

    public SqlServerHealthCheck(IDbConnectionFactory factory) => _factory = factory;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("SQL Server reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQL Server unreachable", ex);
        }
    }
}
