namespace Boys.Ledger.Tests.Api;

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>B11 boot contract: the host comes up, liveness works without a database, unknown routes
/// return the standard envelope (not a blank 404), and a missing connection string kills startup.</summary>
public class BootAndEnvelopeTests
{
    [Fact]
    public async Task Liveness_is_ok_without_a_database()
    {
        using var app = new LedgerAppFactory();
        using var client = app.CreateClient();

        var resp = await client.GetAsync("/health/live");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Root_reports_service_identity()
    {
        using var app = new LedgerAppFactory();
        using var client = app.CreateClient();

        var body = await client.GetStringAsync("/");

        body.Should().Contain("boys-ledger");
    }

    [Fact]
    public async Task Unknown_route_returns_the_standard_error_envelope()
    {
        using var app = new LedgerAppFactory();
        using var client = app.CreateClient();

        var resp = await client.GetAsync("/does-not-exist");
        var json = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("not_found");
    }

    [Fact]
    public void Missing_connection_string_fails_startup_fast()
    {
        using var app = new LedgerAppFactory(sqlConnectionString: "");

        // ValidateOnStart throws at host start (when the client forces the server up).
        var start = () => app.CreateClient();

        start.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("SqlConnectionString");
    }
}
