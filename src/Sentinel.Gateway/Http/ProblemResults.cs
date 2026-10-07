using Sentinel.Domain.Common;

namespace Sentinel.Gateway.Http;

/// <summary>Maps application results to RFC 9457 problem details with a machine-readable <c>code</c>.</summary>
public static class ProblemResults
{
    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult>? success = null) =>
        result.IsSuccess ? (success is null ? Results.Ok(result.Value) : success(result.Value)) : Problem(result.Error);

    public static IResult ToHttp(this Result result, Func<IResult>? success = null) =>
        result.IsSuccess ? (success is null ? Results.NoContent() : success()) : Problem(result.Error);

    public static int StatusFor(Error error) => error.Type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.PolicyViolation => StatusCodes.Status422UnprocessableEntity,
        ErrorType.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    public static IResult Problem(Error error)
    {
        var status = StatusFor(error);
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
        if (error is ValidationError validation)
        {
            extensions["errors"] = validation.Errors;
        }

        var problem = Results.Problem(
            statusCode: status,
            title: TitleFor(status),
            detail: error.Description,
            extensions: extensions);

        return error is RateLimitedError { RetryAfter: { } retryAfter }
            ? new HeaderResult(problem, "Retry-After", Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture))
            : problem;
    }

    private static string TitleFor(int status) => status switch
    {
        400 => "Invalid request",
        401 => "Authentication required",
        403 => "Forbidden",
        404 => "Not found",
        409 => "Conflict",
        422 => "Request refused",
        429 => "Too many requests",
        503 => "Service unavailable",
        _ => "Request failed",
    };

    private sealed class HeaderResult(IResult inner, string name, string value) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers[name] = value;
            return inner.ExecuteAsync(httpContext);
        }
    }
}
