namespace Boys.Ledger.Api.Http;

using System.Text.Json.Serialization;

/// <summary>The one JSON shape every error returns as: <c>{ "error": { code, message, requestId } }</c>.
/// No stack traces, no framework HTML — the frontend and the demo can rely on this everywhere.</summary>
public sealed record ErrorEnvelope(
    [property: JsonPropertyName("error")] ErrorBody Error);

public sealed record ErrorBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("requestId")] string? RequestId);

/// <summary>Maps a stable domain error code to its HTTP status. Anything unknown is a 400 —
/// a validation-style client error — never a 500 that would leak an unhandled path.</summary>
public static class ErrorStatus
{
    public static int ForCode(string code) => code switch
    {
        "brain_unavailable" => StatusCodes.Status503ServiceUnavailable,
        "not_found" => StatusCodes.Status404NotFound,
        "forbidden" => StatusCodes.Status403Forbidden,
        "conflict" or "idempotency_conflict" => StatusCodes.Status409Conflict,
        "unbalanced_postings" or "escrow_violation" or "negative_balance"
            or "illegal_transition" or "validation" or "oversized_evidence"
            or "unsupported_mime" => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status400BadRequest,
    };
}
