using Microsoft.Extensions.AI;
using Sentinel.Application.Abstractions;
using Sentinel.Guardrails.Injection;
using Sentinel.Guardrails.Pii;

namespace Sentinel.Application.Guardrails;

/// <summary>Input is blocked when <see cref="Injection"/> is an attack; messages are already redacted either way.</summary>
public sealed record GuardedInput(
    IReadOnlyList<ChatMessage> Messages,
    InjectionVerdict Injection,
    IReadOnlyDictionary<PiiType, int> RedactedPii)
{
    public bool Blocked => Injection.IsAttack;
}

/// <summary>Retrieved context after guardrails: safe chunks (redacted) and the chunks dropped as injections.</summary>
public sealed record GuardedContext(
    IReadOnlyList<RetrievedChunk> Chunks,
    IReadOnlyList<RetrievedChunk> Quarantined,
    IReadOnlyDictionary<PiiType, int> RedactedPii);

/// <summary>
/// The single place where guardrails are applied, shared by the RAG endpoint, the OpenAI-compatible proxy and the
/// Semantic Kernel filters, so every path gets identical protection.
/// </summary>
public interface IPromptGuard
{
    /// <summary>Redacts PII in every message (origin: caller) and scans user/tool content for prompt injection.</summary>
    Task<GuardedInput> GuardInputAsync(IReadOnlyList<ChatMessage> messages, PiiVault vault, CancellationToken cancellationToken);

    /// <summary>Drops chunks carrying indirect prompt injection and redacts PII in the rest (origin: context).</summary>
    Task<GuardedContext> GuardContextAsync(IReadOnlyList<RetrievedChunk> chunks, PiiVault vault, CancellationToken cancellationToken);

    /// <summary>
    /// Final answer for the caller: restores caller-supplied placeholders (if enabled), keeps context placeholders
    /// masked and redacts any other PII the model produced.
    /// </summary>
    string GuardOutput(string modelText, PiiVault vault);

    /// <summary>
    /// The same output rules for a streamed answer. Text is released as soon as it is safe; a tail that could still
    /// turn into a placeholder or a PII entity is held back until more text (or <see cref="IOutputStream.Flush"/>)
    /// arrives. For every way of splitting an answer into deltas, the concatenated output equals
    /// <see cref="GuardOutput"/> applied to the whole answer.
    /// </summary>
    IOutputStream CreateOutputStream(PiiVault vault);
}

/// <summary>One streamed answer. Not thread-safe; feed it deltas in order and call <see cref="Flush"/> once at the end.</summary>
public interface IOutputStream
{
    string Push(string delta);

    string Flush();
}
