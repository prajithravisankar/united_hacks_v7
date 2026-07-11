namespace Boys.Ledger.IntegrationTests.PublicApi;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Boys.Contracts.Brain.V1;
using Boys.Contracts.Common.V1;
using Boys.Ledger.Api.Grpc;
using Boys.Ledger.Domain.Errors;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>B16 public REST API against real SQL Server, with brain faked in DI: goal creation through the
/// AI gate, validation, activation/escrow, the full golden path to a cent-exact receipt, referee auth, the
/// community pool, the standard envelope on bad input, and degraded-brain valuation.</summary>
public sealed class PublicApiTests : IClassFixture<SqlServerFixture>
{
    private sealed class FakeBrainClient : IBrainClient
    {
        public GoalVerdict Goal { get; set; } = new()
        {
            Verdict = Verdict.Accept,
            Verifiability = Verifiability.Strong,
            RequiredProofType = "grade_screenshot",
            SuggestedRewrite = string.Empty,
            Reasoning = "clear and measurable",
        };

        public ProofVerdict Proof { get; set; } = new()
        { SupportsClaim = true, Confidence = 0.9, Reasoning = "supported", InsufficiencyReason = string.Empty };

        public Valuation Value { get; set; } = new()
        {
            Nav = new Money { Cents = 15_500, Currency = "USD" },
            Principal = new Money { Cents = 10_000, Currency = "USD" },
            Gain = new Money { Cents = 5_500, Currency = "USD" },
            CarryPreview = new Money { Cents = 825, Currency = "USD" },
            UserTakeHome = new Money { Cents = 14_675, Currency = "USD" },
        };

        public bool ValuationUnavailable { get; set; }

        public Task<GoalVerdict> ValidateGoalAsync(ValidateGoalRequest r, CancellationToken ct = default) => Task.FromResult(Goal);

        public Task<ProofVerdict> CheckProofAsync(CheckProofRequest r, CancellationToken ct = default) => Task.FromResult(Proof);

        public Task<Valuation> GetValuationAsync(GetValuationRequest r, CancellationToken ct = default)
            => ValuationUnavailable ? throw new BrainUnavailableException() : Task.FromResult(Value);
    }

    private sealed class PublicApiFactory : WebApplicationFactory<Program>
    {
        public FakeBrainClient Brain { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ledger:SqlConnectionString"] = Migrations.DbConfig.BoysConnectionString(),
                ["Ledger:BrainGrpcAddress"] = "http://127.0.0.1:50061",
                ["Ledger:EvidenceDir"] = Path.Combine(Path.GetTempPath(), "boys-ev-api-test"),
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IBrainClient>();
                services.AddSingleton<IBrainClient>(Brain);
            });
        }
    }

    private readonly SqlServerFixture _fx;

    public PublicApiTests(SqlServerFixture fx) => _fx = fx;

    private void RequireDb() => Skip.IfNot(_fx.Available, "SQL Server not reachable");

    private static int CharityId(SqlConnection c) => c.ExecuteScalar<int>("SELECT TOP 1 charity_id FROM charities");
    private static int RefereeId(SqlConnection c) => c.ExecuteScalar<int>("SELECT TOP 1 user_id FROM users WHERE role='referee'");
    private static int LearnerId(SqlConnection c) => c.ExecuteScalar<int>("SELECT TOP 1 user_id FROM users WHERE role='learner'");
    private static int MilestoneId(SqlConnection c, int cid) =>
        c.ExecuteScalar<int>("SELECT TOP 1 milestone_id FROM milestones WHERE commitment_id=@cid ORDER BY ordinal", new { cid });

    private static object GoalPayload(int charityId, long stake = 10_000, int milestones = 1) => new
    {
        goalText = "Score 90% in History",
        stakeCents = stake,
        charityId,
        driveMode = "AUTO",
        deadline = DateTimeOffset.UtcNow.AddDays(30),
        milestones = Enumerable.Range(1, milestones).Select(i => new
        {
            description = $"leg {i}",
            targetMetric = ">=90",
            dueDate = DateTimeOffset.UtcNow.AddDays(10 * i),
        }).ToArray(),
    };

    private static async Task<int> CreateAsync(HttpClient client, object payload)
    {
        var resp = await client.PostAsJsonAsync("/api/goals", payload);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("commitmentId").GetInt32();
    }

    [SkippableFact]
    public async Task Create_valid_goal_returns_201_with_the_ai_verdict()
    {
        RequireDb();
        using var c = _fx.Open();
        await using var app = new PublicApiFactory();
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/goals", GoalPayload(CharityId(c)));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("aiVerdict").GetString().Should().Be("Accept");
    }

    [SkippableFact]
    public async Task Ai_rejected_goal_returns_422_with_the_suggested_rewrite()
    {
        RequireDb();
        using var c = _fx.Open();
        await using var app = new PublicApiFactory();
        app.Brain.Goal = new GoalVerdict
        {
            Verdict = Verdict.Reject,
            SuggestedRewrite = "Make it measurable: 'Score >= 90% on the final'",
            Reasoning = "goal is not specific",
        };
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/goals", GoalPayload(CharityId(c)));

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("goal_rejected");
        doc.RootElement.GetProperty("suggestedRewrite").GetString().Should().Contain("measurable");
    }

    [SkippableTheory]
    [InlineData(1_999)]   // below $20
    [InlineData(50_001)]  // above $500
    public async Task Stake_outside_the_range_is_422_naming_the_rule(long stake)
    {
        RequireDb();
        using var c = _fx.Open();
        await using var app = new PublicApiFactory();
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/goals", GoalPayload(CharityId(c), stake: stake));

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("stake must be between");
    }

    [SkippableFact]
    public async Task Too_many_milestones_is_422()
    {
        RequireDb();
        using var c = _fx.Open();
        await using var app = new PublicApiFactory();
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/goals", GoalPayload(CharityId(c), milestones: 6));

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("between 1 and 5 milestones");
    }

    [SkippableFact]
    public async Task Activate_posts_escrow_and_is_idempotent()
    {
        RequireDb();
        using var c = _fx.Open();
        await using var app = new PublicApiFactory();
        using var client = app.CreateClient();
        var cid = await CreateAsync(client, GoalPayload(CharityId(c)));

        var first = await client.PostAsync($"/api/goals/{cid}/activate", null);
        var second = await client.PostAsync($"/api/goals/{cid}/activate", null);  // idempotent

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        // escrow funded exactly once
        var escrow = c.ExecuteScalar<long>(
            "SELECT ISNULL(SUM(p.delta_cents),0) FROM ledger_postings p JOIN ledger_transactions t ON p.txn_id=t.txn_id "
            + "WHERE t.commitment_id=@cid AND p.account='USER_ESCROW'", new { cid });
        escrow.Should().Be(10_000);
    }

    [SkippableFact]
    public async Task Full_golden_path_from_create_to_a_cent_exact_receipt()
    {
        RequireDb();
        using var c = _fx.Open();
        await using var app = new PublicApiFactory();
        using var client = app.CreateClient();

        var cid = await CreateAsync(client, GoalPayload(CharityId(c)));
        var mid = MilestoneId(c, cid);
        var refereeId = RefereeId(c);

        (await client.PostAsync($"/api/goals/{cid}/activate", null)).EnsureSuccessStatusCode();

        var proof = await client.PostAsJsonAsync($"/api/goals/{cid}/proof", new
        {
            milestoneId = mid,
            claim = "Scored 92",
            evidenceBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("a screenshot")),
            mime = "image/png",
            idempotencyKey = Guid.NewGuid().ToString("n"),
        });
        proof.EnsureSuccessStatusCode();

        var decisionReq = new HttpRequestMessage(HttpMethod.Post, $"/api/milestones/{mid}/decision")
        {
            Content = JsonContent.Create(new { decision = "approve", idempotencyKey = Guid.NewGuid().ToString("n") }),
        };
        decisionReq.Headers.Add("X-User-Id", refereeId.ToString());
        (await client.SendAsync(decisionReq)).EnsureSuccessStatusCode();

        var valuation = await client.GetFromJsonAsync<JsonElement>($"/api/goals/{cid}/valuation");
        valuation.GetProperty("degraded").GetBoolean().Should().BeFalse();
        valuation.GetProperty("navCents").GetInt64().Should().Be(15_500);

        (await client.PostAsync($"/api/goals/{cid}/cashout", null)).EnsureSuccessStatusCode();

        var settle = await client.PostAsync($"/api/goals/{cid}/settle", null);
        settle.EnsureSuccessStatusCode();
        using var receipt = JsonDocument.Parse(await settle.Content.ReadAsStringAsync());
        receipt.RootElement.GetProperty("takeHomeCents").GetInt64().Should().Be(14_675);  // to the cent
        receipt.RootElement.GetProperty("carryCents").GetInt64().Should().Be(825);
    }

    [SkippableFact]
    public async Task Referee_action_by_a_non_referee_is_403()
    {
        RequireDb();
        using var c = _fx.Open();
        await using var app = new PublicApiFactory();
        using var client = app.CreateClient();
        var cid = await CreateAsync(client, GoalPayload(CharityId(c)));
        var mid = MilestoneId(c, cid);
        (await client.PostAsync($"/api/goals/{cid}/activate", null)).EnsureSuccessStatusCode();
        await client.PostAsJsonAsync($"/api/goals/{cid}/proof", new
        {
            milestoneId = mid,
            claim = "Scored 92",
            evidenceBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("x")),
            mime = "image/png",
            idempotencyKey = Guid.NewGuid().ToString("n"),
        });

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/milestones/{mid}/decision")
        {
            Content = JsonContent.Create(new { decision = "approve", idempotencyKey = Guid.NewGuid().ToString("n") }),
        };
        req.Headers.Add("X-User-Id", LearnerId(c).ToString());  // a learner spoofing a referee

        (await client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task Pool_and_charities_return_the_seeded_world()
    {
        RequireDb();
        await using var app = new PublicApiFactory();
        using var client = app.CreateClient();

        var pool = await client.GetFromJsonAsync<JsonElement>("/api/pool");
        pool.GetProperty("committedPeople").GetInt32().Should().Be(1204);
        pool.GetProperty("poolCents").GetInt64().Should().Be(4_730_000);

        var charities = await client.GetFromJsonAsync<JsonElement>("/api/charities");
        charities.GetArrayLength().Should().Be(4);
    }

    [SkippableFact]
    public async Task Unknown_id_and_bad_body_return_the_envelope_never_a_raw_exception()
    {
        RequireDb();
        await using var app = new PublicApiFactory();
        using var client = app.CreateClient();

        var unknown = await client.GetAsync("/api/goals/999999999");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var doc = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("not_found");

        // malformed JSON body -> 400/422 envelope, not a raw 500 stack trace
        var bad = await client.PostAsync("/api/goals", new StringContent("{not json", Encoding.UTF8, "application/json"));
        ((int)bad.StatusCode).Should().BeInRange(400, 422);
        (await bad.Content.ReadAsStringAsync()).Should().NotContain("StackTrace");
    }

    [SkippableFact]
    public async Task Valuation_degrades_to_200_when_brain_is_down()
    {
        RequireDb();
        using var c = _fx.Open();
        await using var app = new PublicApiFactory();
        app.Brain.ValuationUnavailable = true;
        using var client = app.CreateClient();
        var cid = await CreateAsync(client, GoalPayload(CharityId(c)));

        var resp = await client.GetAsync($"/api/goals/{cid}/valuation");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);  // never a 500
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("degraded").GetBoolean().Should().BeTrue();
    }

    private async Task<int> DriveToCashedOutAsync(HttpClient client, SqlConnection c)
    {
        var cid = await CreateAsync(client, GoalPayload(CharityId(c)));
        var mid = MilestoneId(c, cid);
        (await client.PostAsync($"/api/goals/{cid}/activate", null)).EnsureSuccessStatusCode();
        await client.PostAsJsonAsync($"/api/goals/{cid}/proof", new
        {
            milestoneId = mid,
            claim = "Scored 92",
            evidenceBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("x")),
            mime = "image/png",
            idempotencyKey = Guid.NewGuid().ToString("n"),
        });
        var dec = new HttpRequestMessage(HttpMethod.Post, $"/api/milestones/{mid}/decision")
        { Content = JsonContent.Create(new { decision = "approve", idempotencyKey = Guid.NewGuid().ToString("n") }) };
        dec.Headers.Add("X-User-Id", RefereeId(c).ToString());
        (await client.SendAsync(dec)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/goals/{cid}/cashout", null)).EnsureSuccessStatusCode();
        return cid;
    }

    [SkippableFact]
    public async Task A_caller_cannot_forge_a_system_key_to_poison_another_settlement()
    {
        RequireDb();
        using var c = _fx.Open();
        await using var app = new PublicApiFactory();
        using var client = app.CreateClient();

        var victim = await DriveToCashedOutAsync(client, c);

        var attacker = await CreateAsync(client, GoalPayload(CharityId(c)));
        var amid = MilestoneId(c, attacker);
        (await client.PostAsync($"/api/goals/{attacker}/activate", null)).EnsureSuccessStatusCode();

        // (a) a reserved sys: key is rejected outright.
        var reserved = await client.PostAsJsonAsync($"/api/goals/{attacker}/proof", new
        {
            milestoneId = amid,
            claim = "x",
            evidenceBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("x")),
            mime = "image/png",
            idempotencyKey = $"sys:settle:{victim}",
        });
        reserved.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // (b) a non-sys forge is allowed but no longer collides with the system's sys:settle key.
        await client.PostAsJsonAsync($"/api/goals/{attacker}/proof", new
        {
            milestoneId = amid,
            claim = "x",
            evidenceBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("x")),
            mime = "image/png",
            idempotencyKey = $"settle:{victim}",
        });

        // the victim still settles cleanly and reaches 'settled'
        (await client.PostAsync($"/api/goals/{victim}/settle", null)).EnsureSuccessStatusCode();
        var state = await client.GetFromJsonAsync<JsonElement>($"/api/goals/{victim}");
        state.GetProperty("state").GetString().Should().Be("settled");
    }

    [SkippableFact]
    public async Task OpenApi_document_is_served_and_covers_the_public_paths()
    {
        RequireDb();
        await using var app = new PublicApiFactory();
        using var client = app.CreateClient();

        var spec = await client.GetStringAsync("/openapi/v1.json");

        spec.Should().Contain("/api/goals").And.Contain("/api/pool").And.Contain("/api/charities");
    }

    [SkippableFact]
    public async Task Committed_openapi_matches_the_live_document_no_drift()
    {
        RequireDb();
        await using var app = new PublicApiFactory();
        using var client = app.CreateClient();

        using var live = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var committedPath = Path.Combine(Migrations.DbConfig.RepoRoot(), "services", "ledger", "openapi.json");
        using var committed = JsonDocument.Parse(await File.ReadAllTextAsync(committedPath));

        var livePaths = live.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).OrderBy(x => x);
        var committedPaths = committed.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).OrderBy(x => x);

        livePaths.Should().Equal(committedPaths, "openapi.json is stale — regenerate it from the running app");
    }
}
