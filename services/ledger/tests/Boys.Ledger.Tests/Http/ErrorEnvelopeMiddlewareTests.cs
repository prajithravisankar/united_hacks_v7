namespace Boys.Ledger.Tests.Http;

using System.Text.Json;
using Boys.Ledger.Api.Http;
using Boys.Ledger.Domain.Errors;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>The exception boundary in isolation: expected domain errors map to their status + a clean
/// envelope carrying the request id; anything unexpected is a generic 500 that leaks neither the
/// internal message nor a stack trace.</summary>
public class ErrorEnvelopeMiddlewareTests
{
    private static async Task<(int Status, string Body)> RunAsync(RequestDelegate next)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        ctx.Items[RequestIdMiddleware.ItemKey] = "req-abc";
        using var bodyStream = new MemoryStream();
        ctx.Response.Body = bodyStream;

        var middleware = new ErrorEnvelopeMiddleware(next, NullLogger<ErrorEnvelopeMiddleware>.Instance);
        await middleware.InvokeAsync(ctx);

        bodyStream.Position = 0;
        var text = await new StreamReader(bodyStream).ReadToEndAsync();
        return (ctx.Response.StatusCode, text);
    }

    [Fact]
    public async Task Domain_exception_maps_to_status_code_and_carries_request_id()
    {
        var (status, body) = await RunAsync(_ => throw new BrainUnavailableException());

        status.Should().Be(503);
        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("brain_unavailable");
        error.GetProperty("requestId").GetString().Should().Be("req-abc");
        body.Should().NotContain("StackTrace");
    }

    [Fact]
    public async Task Unexpected_exception_is_a_generic_500_that_leaks_nothing()
    {
        var (status, body) = await RunAsync(_ => throw new InvalidOperationException("secret internal detail"));

        status.Should().Be(500);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("internal_error");
        body.Should().NotContain("secret internal detail");  // internal message never surfaces
        body.Should().NotContain("StackTrace");
    }
}
