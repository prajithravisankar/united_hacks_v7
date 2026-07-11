using Boys.Contracts.Brain.V1;
using Boys.Ledger.Api.Commitments;
using Boys.Ledger.Api.Configuration;
using Boys.Ledger.Api.Grpc;
using Boys.Ledger.Api.Http;
using Boys.Ledger.Api.Infrastructure;
using Boys.Ledger.Api.Ledger;
using Boys.Ledger.Api.PublicApi;
using Boys.Ledger.Api.Settlement;
using Boys.Ledger.Api.Verification;
using Boys.Ledger.Domain.Abstractions;
using Boys.Ledger.Domain.Commitments;
using Boys.Ledger.Domain.Errors;
using Boys.Ledger.Domain.Ledger;
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

// ---- ledger: pure plan-builder (singleton) + Dapper persistence (scoped) ----
builder.Services.AddSingleton<LedgerService>();
builder.Services.AddScoped<ILedgerRepository, SqlLedgerRepository>();
builder.Services.AddScoped<ICommitmentRepository, SqlCommitmentRepository>();

// ---- verification workflow: evidence store (singleton) + orchestrating service (scoped) ----
builder.Services.AddSingleton<IEvidenceStore, LocalEvidenceStore>();
builder.Services.AddScoped<VerificationService>();

// ---- settlement engine (scoped) ----
builder.Services.AddScoped<SettlementService>();

// ---- public REST API (goal creation/activation) + OpenAPI ----
builder.Services.AddScoped<GoalService>();
builder.Services.AddOpenApi();

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

// The public REST edge (thin adapters over the domain services) + its OpenAPI document.
app.MapOpenApi();
app.MapPublicApi();

// ---- internal balance queries (no auth; diagnostic view of the money) ----
app.MapGet("/internal/accounts/{account}/balance", async (string account, ILedgerRepository repo) =>
{
    if (!LedgerAccounts.TryFromDb(account.ToUpperInvariant(), out var parsed))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorBody("not_found", $"unknown account '{account}'", null)),
            statusCode: StatusCodes.Status404NotFound);
    }

    var balance = await repo.GetAccountBalanceAsync(parsed);
    return Results.Json(new { account = parsed.ToDb(), balanceCents = balance });
});

app.MapGet("/internal/commitments/{commitmentId:int}/balances", async (int commitmentId, ILedgerRepository repo) =>
{
    var balances = await repo.GetCommitmentBalancesAsync(commitmentId);
    return Results.Json(balances.ToDictionary(kv => kv.Key.ToDb(), kv => kv.Value));
});

// ---- commitment state + audit trail (diagnostic; the deadline gate is applied on read) ----
app.MapGet("/internal/commitments/{commitmentId:int}/state", async (int commitmentId, ICommitmentRepository repo) =>
{
    var view = await repo.GetAsync(commitmentId);
    return Results.Json(new { commitmentId = view.CommitmentId, state = view.State.ToDb(), deadline = view.Deadline });
});

app.MapGet("/internal/commitments/{commitmentId:int}/events", async (int commitmentId, ICommitmentRepository repo) =>
{
    var events = await repo.GetEventsAsync(commitmentId);
    return Results.Json(events.Select(e => new
    {
        e.EventId,
        e.FromState,
        e.ToState,
        e.Command,
        e.OccurredAt,
    }));
});

// ---- verification workflow (internal; B16 formalizes these as the public API) ----
app.MapPost("/internal/commitments/{commitmentId:int}/milestones/{milestoneId:int}/proof",
    async (int commitmentId, int milestoneId, SubmitProofRequest request, VerificationService svc) =>
    {
        byte[] evidence;
        try
        {
            evidence = Convert.FromBase64String(request.EvidenceBase64);
        }
        catch (FormatException)
        {
            throw new LedgerValidationException("evidenceBase64 is not valid base64");
        }

        var result = await svc.SubmitProofAsync(
            commitmentId, milestoneId, request.Claim, evidence, request.Mime, request.IdempotencyKey);
        return Results.Json(new
        {
            commitmentState = result.CommitmentState.ToDb(),
            milestoneState = result.MilestoneState,
            ai = new
            {
                status = result.AiVerdict.Status.ToString(),
                degraded = result.AiVerdict.Degraded,
                confidence = result.AiVerdict.Confidence,
                reasoning = result.AiVerdict.Reasoning,
            },
            resubmissionCount = result.ResubmissionCount,
        });
    });

app.MapPost("/internal/milestones/{milestoneId:int}/decision",
    async (int milestoneId, RefereeDecisionRequest request, VerificationService svc) =>
    {
        var decision = request.Decision.ToLowerInvariant() switch
        {
            "approve" => RefereeDecision.Approve,
            "reject" => RefereeDecision.Reject,
            _ => throw new LedgerValidationException("decision must be 'approve' or 'reject'"),
        };

        var result = await svc.RefereeDecideAsync(milestoneId, decision, request.RefereeUserId, request.IdempotencyKey);
        return Results.Json(new
        {
            commitmentState = result.CommitmentState.ToDb(),
            milestoneState = result.MilestoneState,
            wasApplied = result.WasApplied,
        });
    });

// ---- settlement (internal; B16 formalizes) ----
static object ReceiptJson(Boys.Ledger.Domain.Settlement.SettlementReceipt r) => new
{
    type = r.Type.ToString(),
    principalCents = r.PrincipalCents,
    navCents = r.NavCents,
    gainCents = r.GainCents,
    carryCents = r.CarryCents,
    charityCents = r.CharityCents,
    bonusCents = r.BonusCents,
    takeHomeCents = r.TakeHomeCents,
};

app.MapPost("/internal/commitments/{commitmentId:int}/settle",
    async (int commitmentId, SettlementService svc) => Results.Json(ReceiptJson(await svc.SettleAsync(commitmentId))));

app.MapGet("/internal/commitments/{commitmentId:int}/receipt", async (int commitmentId, SettlementService svc) =>
{
    var receipt = await svc.GetReceiptAsync(commitmentId);
    return receipt is null
        ? Results.Json(new ErrorEnvelope(new ErrorBody("not_found", "no settlement for this commitment", null)),
            statusCode: StatusCodes.Status404NotFound)
        : Results.Json(ReceiptJson(receipt));
});

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
