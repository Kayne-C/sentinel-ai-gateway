namespace Sentinel.Domain.Audit;

public enum AuditOperation
{
    /// <summary>RAG question over the knowledge base.</summary>
    Ask,

    /// <summary>OpenAI-compatible chat completion through the proxy.</summary>
    ChatCompletion,

    DocumentUpserted,
    DocumentDeleted,
}

public enum AuditOutcome
{
    Allowed,
    CacheHit,

    /// <summary>Refused by a guardrail (prompt injection, policy).</summary>
    Blocked,

    /// <summary>Refused by a quota or token budget.</summary>
    Throttled,

    /// <summary>The request was accepted but a dependency failed.</summary>
    Failed,
}

/// <summary>
/// What happened, recorded for every model interaction and every knowledge-base change. Never contains raw PII:
/// prompts are stored only after redaction (and only when the tenant's policy allows storing prompts at all);
/// otherwise just their SHA-256 digest.
/// </summary>
public sealed record AuditEvent
{
    public required string TenantId { get; init; }

    public required string SubjectId { get; init; }

    public required AuditOperation Operation { get; init; }

    public required AuditOutcome Outcome { get; init; }

    public required DateTime OccurredAtUtc { get; init; }

    public string? Model { get; init; }

    public string? ModelTier { get; init; }

    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }

    /// <summary>Estimated provider cost in USD (from the configured price table).</summary>
    public decimal EstimatedCostUsd { get; init; }

    /// <summary>PII entity type → count of redacted occurrences (input and retrieved context).</summary>
    public IReadOnlyDictionary<string, int> RedactedPii { get; init; } = new Dictionary<string, int>();

    /// <summary>Guardrail rules that fired (e.g. <c>injection.override</c>), comma-free identifiers.</summary>
    public IReadOnlyList<string> GuardrailFindings { get; init; } = [];

    /// <summary>Documents (id:version) whose content reached the model or was served from cache.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>SHA-256 of the redacted prompt; lets investigators match a prompt without storing it.</summary>
    public string? PromptDigest { get; init; }

    /// <summary>Redacted prompt text, present only if the tenant opted in.</summary>
    public string? RedactedPrompt { get; init; }

    public string? ErrorCode { get; init; }

    public double LatencyMs { get; init; }

    public string? TraceId { get; init; }

    /// <summary>Document external id for knowledge-base operations.</summary>
    public string? Subject { get; init; }
}
