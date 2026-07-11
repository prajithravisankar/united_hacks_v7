namespace Boys.Ledger.Api.Http;

/// <summary>Stamps every request with a correlation id (honouring an inbound <c>X-Request-Id</c> or
/// minting one), echoes it on the response, and opens a logging scope so every log line for the
/// request carries it. The error envelope surfaces the same id, so a user-reported failure is one
/// grep away in the logs.</summary>
public sealed class RequestIdMiddleware
{
    public const string HeaderName = "X-Request-Id";
    public const string ItemKey = "RequestId";

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestIdMiddleware> _logger;

    public RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = context.Request.Headers.TryGetValue(HeaderName, out var inbound)
                        && !string.IsNullOrWhiteSpace(inbound)
            ? inbound.ToString()
            : Guid.NewGuid().ToString("n");

        context.Items[ItemKey] = requestId;
        context.Response.Headers[HeaderName] = requestId;

        using (_logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId }))
        {
            await _next(context);
        }
    }
}

public static class HttpContextRequestIdExtensions
{
    /// <summary>The correlation id assigned by <see cref="RequestIdMiddleware"/>, or null if unset.</summary>
    public static string? RequestId(this HttpContext context)
        => context.Items.TryGetValue(RequestIdMiddleware.ItemKey, out var value) ? value as string : null;
}
