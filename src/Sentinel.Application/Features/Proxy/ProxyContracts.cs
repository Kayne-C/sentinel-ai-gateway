using Microsoft.Extensions.AI;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Guardrails.Pii;

namespace Sentinel.Application.Features.Proxy;

/// <summary>An OpenAI-compatible chat completion request, already parsed by the transport.</summary>
public sealed record ProxyChatRequest(
    CallerIdentity Caller,
    IReadOnlyList<ChatMessage> Messages,
    string? RequestedModel,
    int? MaxOutputTokens,
    bool Stream);

/// <summary>
/// Everything the transport needs to forward a vetted request upstream and to post-process the answer.
/// One instance per request; holds the request's <see cref="PiiVault"/> in memory only.
/// </summary>
public sealed class PreparedProxyCall
{
    public required ProxyChatRequest Request { get; init; }

    /// <summary>Messages with PII replaced by placeholders: this is what the provider receives.</summary>
    public required IReadOnlyList<ChatMessage> SanitizedMessages { get; init; }

    public required ModelRoute Route { get; init; }

    public required int MaxOutputTokens { get; init; }

    public required BudgetLease Lease { get; init; }

    public required PiiVault Vault { get; init; }

    public required IReadOnlyDictionary<PiiType, int> RedactedPii { get; init; }

    public required long StartedAtTimestamp { get; init; }
}

public sealed record ProxyCompletion(int PromptTokens, int CompletionTokens, bool Succeeded, string? ErrorCode);

public interface IChatProxyService
{
    /// <summary>Guardrails, budget reservation and routing. Failure = do not call the provider.</summary>
    Task<Result<PreparedProxyCall>> PrepareAsync(ProxyChatRequest request, CancellationToken cancellationToken);

    /// <summary>Post-processes one complete answer text for the caller (see <c>IPromptGuard.GuardOutput</c>).</summary>
    string GuardOutput(PreparedProxyCall call, string modelText);

    /// <summary>Settles the budget with real usage and writes the audit entry. Must be called exactly once.</summary>
    Task CompleteAsync(PreparedProxyCall call, ProxyCompletion completion, CancellationToken cancellationToken);
}
