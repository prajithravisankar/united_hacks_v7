namespace Boys.Ledger.IntegrationTests;

using System.Net;
using Boys.Ledger.Migrations;
using FluentAssertions;
using Xunit;

/// <summary>B11 acceptance: readiness tracks SQL Server truthfully. Reachable → 200; a dead server →
/// 503 (never a thrown 500); liveness is independent of the database.</summary>
public sealed class HealthReadyTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fx;

    public HealthReadyTests(SqlServerFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Ready_is_200_when_sql_server_is_reachable()
    {
        Skip.IfNot(_fx.Available, "SQL Server not reachable");

        await using var app = new LedgerAppFactory(DbConfig.BoysConnectionString());
        using var client = app.CreateClient();

        var resp = await client.GetAsync("/health/ready");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_is_503_when_sql_server_is_unreachable()
    {
        // Port 1 has nothing listening; the readiness check must report 503, not throw a 500.
        const string deadConnection =
            "Server=127.0.0.1,1;Database=boys;User Id=sa;Password=x;TrustServerCertificate=True;Encrypt=False;Connect Timeout=2";
        await using var app = new LedgerAppFactory(deadConnection);
        using var client = app.CreateClient();

        var resp = await client.GetAsync("/health/ready");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Live_is_200_even_with_an_unreachable_database()
    {
        const string deadConnection =
            "Server=127.0.0.1,1;Database=boys;User Id=sa;Password=x;TrustServerCertificate=True;Encrypt=False;Connect Timeout=2";
        await using var app = new LedgerAppFactory(deadConnection);
        using var client = app.CreateClient();

        var resp = await client.GetAsync("/health/live");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
