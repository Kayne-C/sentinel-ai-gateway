using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace Sentinel.Gateway.Http;

/// <summary>Turns unhandled exceptions into problem details without leaking internals (no messages, no stack traces).</summary>
internal sealed partial class GatewayExceptionHandler(ILogger<GatewayExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case GatewayAuthException auth:
                await ProblemResults.Problem(auth.Error).ExecuteAsync(httpContext);
                return true;
            case BadHttpRequestException bad:
                httpContext.Response.StatusCode = bad.StatusCode;
                await Results.Problem(statusCode: bad.StatusCode, title: "Invalid request", extensions: Code("Request.Malformed"))
                    .ExecuteAsync(httpContext);
                return true;
            case OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested:
                return true; // The client went away; nothing to report.
        }

        LogUnhandled(logger, exception, Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier);
        await Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unexpected error", extensions: Code("Server.Error"))
            .ExecuteAsync(httpContext);
        return true;
    }

    private static Dictionary<string, object?> Code(string code) => new() { ["code"] = code };

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception (trace {TraceId})")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string traceId);
}
