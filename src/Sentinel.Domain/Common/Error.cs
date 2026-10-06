namespace Sentinel.Domain.Common;

public enum ErrorType
{
    BusinessRule,
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    Forbidden,

    /// <summary>A guardrail refused the request (prompt injection, policy). Mapped to 422.</summary>
    PolicyViolation,

    /// <summary>A quota or token budget is exhausted. Mapped to 429.</summary>
    RateLimited,

    /// <summary>A dependency (model provider, vector store) is unavailable. Mapped to 503.</summary>
    Unavailable,
}

public record Error(string Code, string Description, ErrorType Type = ErrorType.BusinessRule)
{
    public static readonly Error None = new(string.Empty, string.Empty);

    public static Error BusinessRule(string code, string description) => new(code, description);

    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);

    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);

    public static Error Unauthorized(string code, string description) => new(code, description, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string description) => new(code, description, ErrorType.Forbidden);

    public static Error PolicyViolation(string code, string description) => new(code, description, ErrorType.PolicyViolation);

    public static Error Unavailable(string code, string description) => new(code, description, ErrorType.Unavailable);
}

/// <summary>Budget/quota refusal; <see cref="RetryAfter"/> becomes the HTTP Retry-After header.</summary>
public sealed record RateLimitedError(string Code, string Description, TimeSpan? RetryAfter)
    : Error(Code, Description, ErrorType.RateLimited);

public sealed record ValidationError(IReadOnlyDictionary<string, string[]> Errors)
    : Error("Validation.Failed", "One or more validation errors occurred.", ErrorType.Validation);
