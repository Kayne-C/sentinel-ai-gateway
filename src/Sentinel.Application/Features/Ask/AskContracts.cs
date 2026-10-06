using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Domain.Identity;

namespace Sentinel.Application.Features.Ask;

/// <summary>Permission-aware RAG over the tenant's knowledge base.</summary>
public sealed record AskQuestionCommand(CallerIdentity Caller, string Question, int? TopK = null) : ICommand<AskResponse>;

public sealed record AskResponse(
    string Answer,
    IReadOnlyList<Citation> Citations,
    string Model,
    string ModelTier,
    bool CacheHit,
    UsageInfo Usage,
    IReadOnlyDictionary<string, int> RedactedPii,
    long AuditSequence,
    double LatencyMs);

public sealed record Citation(string ExternalId, string Title, int Version);

public sealed record UsageInfo(int PromptTokens, int CompletionTokens, decimal EstimatedCostUsd);

public static class AskErrors
{
    public const string PromptInjection = "Guardrails.PromptInjection";
    public const string BudgetExceeded = "Budget.Exceeded";
    public const string ModelUnavailable = "Model.Unavailable";
}
