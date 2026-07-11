using Boys.Contracts.Brain.V1;
using Boys.Ledger.Api.Configuration;
using Boys.Ledger.Api.Grpc;
using Boys.Ledger.Api.Http;
using Boys.Ledger.Api.Infrastructure;
using Boys.Ledger.Domain.Abstractions;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// ---- typed config, validated at startup (fail fast, never at first request) ----
builder.Services
    .AddOptions<LedgerOptions>()
    .Bind(builder.Configuration.GetSection(LedgerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ---- infrastructure ----
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();

// ---- brain gRPC clients (resilience: retry + circuit breaker + attempt timeout on the HttpClient) ----
var brainAddress = builder.Configuration[$"{LedgerOptions.SectionName}:BrainGrpcAddress"]
                   ?? "http://127.0.0.1:50061";
builder.Services.AddGrpcClient<QuantService.QuantServiceClient>(o => o.Address = new Uri(brainAddress))
    .AddStandardResilienceHandler();
builder.Services.AddGrpcClient<RefereeService.RefereeServiceClient>(o => o.Address = new Uri(brainAddress))
    .AddStandardResilienceHandler();
builder.Services.AddScoped<IBrainClient, BrainClient>();

// ---- health: liveness (process up) vs readiness (SQL Server reachable) ----
builder.Services.AddHealthChecks()
    .AddCheck("live", () => HealthCheckResult.Healthy("process up"), tags: ["live"])
    .AddCheck<SqlServerHealthCheck>("sqlserver", tags: ["ready"]);

var app = builder.Build();

// Request id first so the error envelope (and every log line) can carry it.
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<ErrorEnvelopeMiddleware>();

app.MapGet("/", () => Results.Json(new { service = "boys-ledger", status = "ok" }));

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });

// Any unmatched route returns the standard envelope, not a blank 404.
app.MapFallback(async (HttpContext ctx) =>
{
    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    await ctx.Response.WriteAsJsonAsync(
        new ErrorEnvelope(new ErrorBody("not_found", "resource not found", ctx.RequestId())));
});

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the API in-memory for tests.</summary>
public partial class Program;
