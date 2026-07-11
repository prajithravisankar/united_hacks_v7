namespace Boys.Ledger.Api.Http;

using Boys.Ledger.Domain.Errors;

/// <summary>The single exception boundary. Turns any exception into the standard <see cref="ErrorEnvelope"/>:
/// expected <see cref="DomainException"/>s become their mapped 4xx/503 with the safe message; anything
/// unexpected is logged in full server-side and returned as a generic 500 — the caller never sees a
/// stack trace or a raw framework error.</summary>
public sealed class ErrorEnvelopeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorEnvelopeMiddleware> _logger;

    public ErrorEnvelopeMiddleware(RequestDelegate next, ILogger<ErrorEnvelopeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (DomainException ex)
        {
            _logger.LogWarning(ex, "Domain error {Code}", ex.Code);
            await WriteAsync(context, ErrorStatus.ForCode(ex.Code), ex.Code, ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            // Malformed request (unparseable JSON body, etc.) — a client error, not a 500.
            _logger.LogWarning(ex, "Malformed request");
            await WriteAsync(context, ex.StatusCode, "bad_request", "the request was malformed");
        }
        catch (Exception ex)
        {
            // Unexpected: log everything, tell the caller nothing but a correlation id.
            _logger.LogError(ex, "Unhandled exception");
            await WriteAsync(context, StatusCodes.Status500InternalServerError,
                "internal_error", "an unexpected error occurred");
        }
    }

    private static async Task WriteAsync(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            // Can't rewrite a response already on the wire; the exception is logged, so bail cleanly.
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        var envelope = new ErrorEnvelope(new ErrorBody(code, message, context.RequestId()));
        await context.Response.WriteAsJsonAsync(envelope);
    }
}
