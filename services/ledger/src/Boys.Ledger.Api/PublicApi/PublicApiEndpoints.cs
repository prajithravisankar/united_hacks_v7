namespace Boys.Ledger.Api.PublicApi;

using Boys.Contracts.Brain.V1;
using Boys.Ledger.Api.Commitments;
using Boys.Ledger.Api.Configuration;
using Boys.Ledger.Api.Grpc;
using Boys.Ledger.Api.Http;
using Boys.Ledger.Api.Infrastructure;
using Boys.Ledger.Api.Settlement;
using Boys.Ledger.Api.Verification;
using Boys.Ledger.Domain.Commitments;
using Boys.Ledger.Domain.Errors;
using Boys.Ledger.Domain.Settlement;
using Dapper;

/// <summary>The public REST edge the frontend consumes. Every handler is a thin adapter over a domain
/// service — no business logic lives here. Identity is a seeded demo user selected by an <c>X-User-Id</c>
/// header (no auth is a locked v0 decision); referee actions require that user to actually have the referee
/// role. Errors flow through the standard envelope; the valuation proxy degrades brain outages to a 200.</summary>
public static class PublicApiEndpoints
{
    private const string UserHeader = "X-User-Id";

    public static void MapPublicApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---- goals ----
        api.MapPost("/goals", async (CreateGoalRequest request, HttpContext ctx, GoalService goals, IDbConnectionFactory factory) =>
        {
            var userId = await ResolveUserAsync(ctx, factory);
            var result = await goals.CreateAsync(userId, request);
            if (!result.Accepted)
            {
                // AI gate wants a revision — 422 with the suggested rewrite in the envelope.
                return Results.Json(new
                {
                    error = new { code = "goal_rejected", message = result.Reasoning, requestId = ctx.RequestId() },
                    suggestedRewrite = result.SuggestedRewrite,
                }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            return Results.Json(new { commitmentId = result.CommitmentId, aiVerdict = result.Verdict, degraded = result.Degraded },
                statusCode: StatusCodes.Status201Created);
        });

        api.MapPost("/goals/{id:int}/activate", async (int id, GoalService goals) =>
            Results.Json(new { commitmentId = id, state = (await goals.ActivateAsync(id)).ToDb() }));

        api.MapGet("/goals/{id:int}", async (int id, ICommitmentRepository commitments, IDbConnectionFactory factory) =>
        {
            var view = await commitments.GetAsync(id);  // throws NotFound (404) if absent; applies the deadline gate
            var events = await commitments.GetEventsAsync(id);
            await using var conn = factory.Create();
            await conn.OpenAsync();
            var milestones = await conn.QueryAsync<MilestoneView>(
                "SELECT milestone_id AS MilestoneId, ordinal AS Ordinal, description AS Description, "
                + "target_metric AS TargetMetric, due_date AS DueDate, state AS State "
                + "FROM milestones WHERE commitment_id = @id ORDER BY ordinal", new { id });
            return Results.Json(new
            {
                commitmentId = id,
                state = view.State.ToDb(),
                deadline = view.Deadline,
                milestones,
                timeline = events.Select(e => new { e.FromState, e.ToState, e.Command, e.OccurredAt }),
            });
        });

        // ---- proof + referee decision ----
        api.MapPost("/goals/{id:int}/proof", async (int id, ProofRequest request, VerificationService verification) =>
        {
            var evidence = DecodeBase64(request.EvidenceBase64);
            var result = await verification.SubmitProofAsync(id, request.MilestoneId, request.Claim, evidence, request.Mime, request.IdempotencyKey);
            return Results.Json(new
            {
                commitmentState = result.CommitmentState.ToDb(),
                milestoneState = result.MilestoneState,
                ai = new { status = result.AiVerdict.Status.ToString(), degraded = result.AiVerdict.Degraded, result.AiVerdict.Reasoning },
                result.ResubmissionCount,
            });
        });

        api.MapPost("/milestones/{id:int}/decision", async (int id, DecisionRequest request, HttpContext ctx, VerificationService verification) =>
        {
            var userId = RequestUserId(ctx) ?? throw new ForbiddenException("referee identity required (X-User-Id header)");
            var decision = ParseDecision(request.Decision);
            var result = await verification.RefereeDecideAsync(id, decision, userId, request.IdempotencyKey);  // 403 if not a referee
            return Results.Json(new { commitmentState = result.CommitmentState.ToDb(), result.MilestoneState, result.WasApplied });
        });

        // ---- cash-out / ride / settle ----
        api.MapPost("/goals/{id:int}/cashout", async (int id, ICommitmentRepository commitments) =>
            Results.Json(new { state = (await commitments.TransitionAsync(id, CommitmentCommand.CashOut, false, $"sys:cashout:{id}", systemKey: true)).ToState.ToDb() }));

        // Ride can happen once per leg, so the idempotency key is per-leg — a fixed sys:ride:{id} would make
        // the 2nd+ ride a silent no-op and break multi-leg riding, while a double-click on the SAME leg still
        // collapses to one transition. The leg is counted from the authoritative, transactional event log
        // (clear_milestone events are written atomically with the transition — the milestones.state projection
        // is updated separately and can lag).
        api.MapPost("/goals/{id:int}/ride", async (int id, ICommitmentRepository commitments, IDbConnectionFactory factory) =>
        {
            await using var conn = factory.Create();
            await conn.OpenAsync();
            var leg = await conn.ExecuteScalarAsync<int>(ClearedLegsSql, new { id });
            var result = await commitments.TransitionAsync(id, CommitmentCommand.Ride, false, $"sys:ride:{id}:leg{leg}", systemKey: true);
            return Results.Json(new { state = result.ToState.ToDb() });
        });

        // Clear the FINAL leg → succeeded (the winning terminal, which pays the winners-pool bonus). Guard
        // server-side that every milestone really is cleared — the state machine's isFinalLeg is client-asserted,
        // so without this a client could "succeed" (and draw the bonus) after clearing just one of N milestones.
        api.MapPost("/goals/{id:int}/succeed", async (int id, ICommitmentRepository commitments, IDbConnectionFactory factory) =>
        {
            await using var conn = factory.Create();
            await conn.OpenAsync();
            var cleared = await conn.ExecuteScalarAsync<int>(ClearedLegsSql, new { id });
            var total = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM milestones WHERE commitment_id = @id", new { id });
            if (cleared < total)
            {
                throw new LedgerValidationException("cannot succeed before the final milestone is cleared — ride to the next leg");
            }
            var result = await commitments.TransitionAsync(id, CommitmentCommand.Complete, isFinalLeg: true, $"sys:succeed:{id}", systemKey: true);
            return Results.Json(new { state = result.ToState.ToDb() });
        });

        api.MapPost("/goals/{id:int}/settle", async (int id, SettlementService settlement) =>
            Results.Json(ReceiptJson(await settlement.SettleAsync(id))));

        api.MapGet("/goals/{id:int}/receipt", async (int id, SettlementService settlement, HttpContext ctx) =>
        {
            var receipt = await settlement.GetReceiptAsync(id);
            return receipt is null ? NotFound(ctx, "no settlement for this commitment") : Results.Json(ReceiptJson(receipt));
        });

        // ---- valuation proxy: brain outage degrades to 200 + degraded:true, never a 500 ----
        api.MapGet("/goals/{id:int}/valuation", async (int id, IBrainClient brain, IDbConnectionFactory factory, Microsoft.Extensions.Options.IOptions<LedgerOptions> options, HttpContext ctx) =>
        {
            var commitment = await ReadCommitmentAsync(factory, id);
            if (commitment is null)
            {
                return NotFound(ctx, $"commitment {id} not found");
            }

            try
            {
                var v = await brain.GetValuationAsync(new GetValuationRequest
                {
                    CommitmentId = id.ToString(),
                    PrincipalCents = commitment.Value.Stake,
                    StartDate = options.Value.FundStartDate,
                    AsOf = options.Value.FundAsOfDate,
                    DriveMode = commitment.Value.DriveMode == "USER" ? DriveMode.User : DriveMode.Auto,
                });
                return Results.Json(new
                {
                    degraded = false,
                    navCents = v.Nav.Cents,
                    principalCents = v.Principal.Cents,
                    gainCents = v.Gain.Cents,
                    carryPreviewCents = v.CarryPreview.Cents,
                    takeHomeCents = v.UserTakeHome.Cents,
                });
            }
            catch (BrainUnavailableException)
            {
                return Results.Json(new { degraded = true, navCents = (long?)null, principalCents = commitment.Value.Stake });
            }
        });

        // ---- community pool + charities ----
        api.MapGet("/pool", async (IDbConnectionFactory factory) =>
        {
            await using var conn = factory.Create();
            await conn.OpenAsync();
            var stats = await conn.QuerySingleAsync<CommunityPoolStats>(
                "SELECT committed_people AS CommittedPeople, pool_cents AS PoolCents FROM community_pool_stats WHERE id = 1");
            return Results.Json(stats);
        });

        api.MapGet("/charities", async (IDbConnectionFactory factory) =>
        {
            await using var conn = factory.Create();
            await conn.OpenAsync();
            var charities = await conn.QueryAsync<Charity>(
                "SELECT charity_id AS CharityId, name AS Name FROM charities ORDER BY charity_id");
            return Results.Json(charities);
        });
    }

    // Count of milestones a commitment has cleared, from the authoritative event log (atomic with each
    // clear transition — unlike the milestones.state projection, which is written on a separate connection).
    private const string ClearedLegsSql =
        "SELECT COUNT(*) FROM commitment_events WHERE commitment_id = @id AND command = 'clear_milestone'";

    private static int? RequestUserId(HttpContext ctx) =>
        ctx.Request.Headers.TryGetValue(UserHeader, out var value) && int.TryParse(value, out var id) ? id : null;

    private static async Task<int> ResolveUserAsync(HttpContext ctx, IDbConnectionFactory factory)
    {
        if (RequestUserId(ctx) is int id)
        {
            return id;
        }

        await using var conn = factory.Create();
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>("SELECT TOP 1 user_id FROM users WHERE role = 'learner' ORDER BY user_id");
    }

    private static async Task<(long Stake, string DriveMode)?> ReadCommitmentAsync(IDbConnectionFactory factory, int commitmentId)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<(long, string)?>(
            "SELECT stake_cents, drive_mode FROM commitments WHERE commitment_id = @id", new { id = commitmentId });
    }

    private static byte[] DecodeBase64(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw new LedgerValidationException("evidenceBase64 is not valid base64");
        }
    }

    private static RefereeDecision ParseDecision(string decision) => decision.ToLowerInvariant() switch
    {
        "approve" => RefereeDecision.Approve,
        "reject" => RefereeDecision.Reject,
        _ => throw new LedgerValidationException("decision must be 'approve' or 'reject'"),
    };

    private static IResult NotFound(HttpContext ctx, string message) => Results.Json(
        new ErrorEnvelope(new ErrorBody("not_found", message, ctx.RequestId())), statusCode: StatusCodes.Status404NotFound);

    private static object ReceiptJson(SettlementReceipt r) => new
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
}
