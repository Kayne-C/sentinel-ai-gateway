using Sentinel.Gateway.Http;
using System.Text.Json.Nodes;

namespace Sentinel.Gateway.Proxy;

/// <summary>
/// Errors in the shape OpenAI SDKs understand (<c>{"error": {"message", "type", "param", "code"}}</c>), so a client that
/// only knows the OpenAI API shows a useful message instead of failing to parse a problem-details document.
/// </summary>
internal static class OpenAiErrors
{
    public static async Task WriteAsync(
        HttpContext context, int status, string type, string code, string message, string? param = null, TimeSpan? retryAfter = null)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        if (retryAfter is { } delay)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var body = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = type,
                ["param"] = param,
                ["code"] = code,
            },
        };

        await context.Response.WriteAsync(GatewayJson.Serialize(body), context.RequestAborted);
    }

    public static Task InvalidRequestAsync(HttpContext context, string code, string message, string? param = null) =>
        WriteAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error", code, message, param);
}
