using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Application.Common;
using Sentinel.Application.Diagnostics;
using Sentinel.Application.Guardrails;
using Sentinel.Domain.Audit;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Guardrails.Pii;
using RoutingContext = Sentinel.Application.Abstractions.RoutingContext;

namespace Sentinel.Application.Features.Ask;

/// <summary>
/// Permission-aware RAG. The step order is the security model: guardrails run before anything is spent or sent;
/// retrieval is keyed only by the caller's validated identity; a cached answer is reused only when it was grounded on
/// exactly the documents (and versions) this caller may read right now; the budget is reserved before the provider is
/// called and settled afterwards with real usage. Every outcome, including unexpected failures, is audited, and the
/// audit append is not optional: if it fails the request fails rather than return an unaudited answer.
/// </summary>
internal sealed partial class AskQuestionCommandHandler(
    IPromptGuard promptGuard,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IVectorSearch vectorSearch,
    ISemanticCache semanticCache,
    ITokenBudget tokenBudget,
    ITokenCounter tokenCounter,
    IModelRouter modelRouter,
    IChatClientProvider chatClientProvider,
    IAuditLog auditLog,
    IOptions<RagOptions> ragOptions,
    IOptions<ModelCatalogOptions> modelCatalog,
    IOptions<AuditPolicyOptions> auditPolicy,
    TimeProvider timeProvider,
    ILogger<AskQuestionCommandHandler> logger) : ICommandHandler<AskQuestionCommand, AskResponse>
{
    internal const string CacheNamespace = "ask";
    internal const float Temperature = 0.2f;
    internal const string QuarantinedFinding = "context.quarantined";
    internal const string KnowledgeUnavailable = "Knowledge.Unavailable";

    private static readonly IReadOnlyDictionary<PiiType, int> NoPii = new Dictionary<PiiType, int>();

    public async Task<Result<AskResponse>> Handle(AskQuestionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Caller);
        var run = new AskRun(command.Caller, timeProvider.GetTimestamp());
        try
        {
            return await RunAsync(command, run, cancellationToken);
        }
        catch (Exception exception) when (!run.AuditAttempted)
        {
            // Unexpected failures are audited too; the original exception still propagates to the host.
            var cancelled = ModelCallAccounting.IsCallerCancellation(exception, cancellationToken);
            if (!cancelled)
            {
                LogUnexpectedFailure(logger, exception.GetType().Name);
            }

            await SettleAsync(run, actualTokens: 0);
            await TryAuditFailureAsync(run, cancelled ? ModelCallAccounting.RequestCancelled : ModelCallAccounting.InternalError);
            throw;
        }
    }

    private async Task<Result<AskResponse>> RunAsync(AskQuestionCommand command, AskRun run, CancellationToken cancellationToken)
    {
        var caller = command.Caller;
        var rag = ragOptions.Value;
        var vault = new PiiVault();

        // 1. Input guardrails. A blocked question never reaches embeddings, retrieval, the cache, the budget or a model.
        var input = await promptGuard.GuardInputAsync([new ChatMessage(ChatRole.User, command.Question ?? string.Empty)], vault, cancellationToken);
        var question = input.Messages.Count > 0 ? input.Messages[0].Text : string.Empty;
        run.InputPii = input.RedactedPii;
        run.Findings.AddRange(input.Injection.Rules);
        run.RedactedPrompt = question;
        if (input.Blocked)
        {
            LogQuestionBlocked(logger, input.Injection.Detector, string.Join(' ', input.Injection.Rules));
            await AuditAsync(run, AuditOutcome.Blocked, AskErrors.PromptInjection);
            return Error.PolicyViolation(AskErrors.PromptInjection, "The question was rejected by the prompt-injection guardrail.");
        }

        var callerSuppliedPii = input.RedactedPii.Values.Any(count => count > 0);

        // 2. Embed the redacted question; raw PII never reaches the embedding provider.
        ReadOnlyMemory<float> embedding;
        try
        {
            embedding = await embeddingGenerator.GenerateVectorAsync(question, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (!ModelCallAccounting.IsCallerCancellation(exception, cancellationToken))
        {
            LogDependencyFailed(logger, "embedding", exception.GetType().Name);
            return await FailAsync(run, Error.Unavailable(AskErrors.ModelUnavailable, "The embedding model is unavailable."));
        }

        if (embedding.Length != EmbeddingDefaults.Dimensions)
        {
            LogEmbeddingDimensionMismatch(logger, embedding.Length, EmbeddingDefaults.Dimensions);
            return await FailAsync(run, Error.Unavailable(AskErrors.ModelUnavailable, "The embedding model returned vectors of an unexpected size."));
        }

        // 3. Retrieve. Tenant and ACL principals come from the validated identity, never from the request body.
        var topK = Math.Clamp(command.TopK ?? rag.TopK, 1, AskQuestionCommandValidator.MaxTopK);
        IReadOnlyList<RetrievedChunk> retrieved;
        try
        {
            retrieved = await vectorSearch.SearchAsync(
                new VectorQuery(caller.TenantId, caller.Principals, embedding, topK, rag.MinSimilarity), cancellationToken);
        }
        catch (Exception exception) when (!ModelCallAccounting.IsCallerCancellation(exception, cancellationToken))
        {
            LogDependencyFailed(logger, "vector-search", exception.GetType().Name);
            return await FailAsync(run, Error.Unavailable(KnowledgeUnavailable, "The knowledge base is unavailable."));
        }

        // 4. Context guardrails (indirect injection, PII), then trim to the context budget in similarity order.
        var guarded = await promptGuard.GuardContextAsync(retrieved, vault, cancellationToken);
        run.ContextPii = guarded.RedactedPii;
        if (guarded.Quarantined.Count > 0)
        {
            run.Findings.Add($"{QuarantinedFinding}:{guarded.Quarantined.Count}");
        }

        var context = TrimContext(guarded.Chunks, rag.MaxContextTokens);
        var userPrompt = RagPrompt.BuildUserPrompt(context.Documents, question);
        run.RedactedPrompt = RagPrompt.SystemPrompt + "\n\n" + userPrompt;
        var sources = DistinctDocuments(context.Chunks);

        // 5. Route.
        var systemTokens = tokenCounter.CountTokens(RagPrompt.SystemPrompt);
        var route = modelRouter.Route(new RoutingContext(
            caller.TenantId, question, tokenCounter.CountTokens(question), systemTokens + context.Tokens, RequestedModel: null));
        var tier = ModelCallAccounting.ResolveTier(modelCatalog.Value, route);
        var model = ModelCallAccounting.ModelFor(route, tier);
        run.Route = route;
        run.Model = model;

        // 6. Semantic cache. Questions with caller PII are personal: never looked up, never stored.
        var cacheable = rag.SemanticCacheEnabled && !callerSuppliedPii && sources.Count > 0;
        if (cacheable)
        {
            var served = await TryServeFromCacheAsync(run, vault, embedding, route, tier, sources, cancellationToken);
            if (served is not null)
            {
                return served;
            }
        }

        // 7. Reserve the worst case: the whole prompt plus the tier's maximum output.
        var promptTokens = systemTokens + tokenCounter.CountTokens(userPrompt);
        var maxOutputTokens = Math.Max(1, tier.MaxOutputTokens);
        BudgetLease lease;
        try
        {
            lease = await tokenBudget.ReserveAsync(
                new BudgetRequest(caller.TenantId, caller.SubjectId, promptTokens + maxOutputTokens), cancellationToken);
        }
        catch (Exception exception) when (!ModelCallAccounting.IsCallerCancellation(exception, cancellationToken))
        {
            LogDependencyFailed(logger, "token-budget", exception.GetType().Name);
            return await FailAsync(run, Error.Unavailable(ModelCallAccounting.BudgetUnavailable, "The token budget service is unavailable."));
        }

        if (!lease.Granted)
        {
            ModelCallAccounting.RecordBudgetRejection(lease);
            await AuditAsync(run, AuditOutcome.Throttled, AskErrors.BudgetExceeded);
            return new RateLimitedError(AskErrors.BudgetExceeded, "The token budget for this period is exhausted.", lease.RetryAfter);
        }

        run.Lease = lease;

        // 8. Call the model. From here on the documents have left the gateway, so they count as sources.
        run.Sources = SourceLabels(sources);
        ChatResponse response;
        try
        {
            var client = chatClientProvider.GetClient(route.Tier);
            response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.System, RagPrompt.SystemPrompt), new ChatMessage(ChatRole.User, userPrompt)],
                new ChatOptions { MaxOutputTokens = maxOutputTokens, Temperature = Temperature, ModelId = model },
                cancellationToken);
        }
        catch (Exception exception) when (!ModelCallAccounting.IsCallerCancellation(exception, cancellationToken))
        {
            LogModelFailed(logger, route.Tier, exception.GetType().Name);
            await SettleAsync(run, actualTokens: 0);
            return await FailAsync(run, Error.Unavailable(AskErrors.ModelUnavailable, "The language model is unavailable."));
        }

        // 9. Bill real usage (settled first, so nothing below can lose the charge), post-process the answer, cache it
        // if it is shareable, audit. Missing or zero usage is replaced by our own count: a call always has input, and
        // unreported usage must not become free usage.
        var billedPrompt = ModelCallAccounting.ToTokenCount(response.Usage?.InputTokenCount) is int reportedPrompt and > 0
            ? reportedPrompt
            : promptTokens;
        var billedCompletion = ModelCallAccounting.ToTokenCount(response.Usage?.OutputTokenCount) is int reportedCompletion and > 0
            ? reportedCompletion
            : tokenCounter.CountTokens(response.Text);
        var cost = tier.EstimateCost(billedPrompt, billedCompletion);
        var answeredModel = string.IsNullOrWhiteSpace(response.ModelId) ? model : response.ModelId;
        run.Model = answeredModel;
        run.PromptTokens = billedPrompt;
        run.CompletionTokens = billedCompletion;
        run.Cost = cost;
        await SettleAsync(run, billedPrompt + billedCompletion);

        var answer = promptGuard.GuardOutput(response.Text, vault);

        // A question on which any injection rule fired (even below the block threshold) is not cached: a shared answer
        // must not be steered by one caller's suspicious wording. Truncated or filtered answers are not worth sharing.
        var complete = response.FinishReason != ChatFinishReason.Length && response.FinishReason != ChatFinishReason.ContentFilter;
        if (cacheable && complete && input.Injection.Rules.Count == 0 && !string.IsNullOrWhiteSpace(answer))
        {
            var cachedAnswer = new CachedAnswer(
                question, answer, sources, answeredModel, billedPrompt, billedCompletion, timeProvider.GetUtcNow().UtcDateTime);
            await StoreInCacheAsync(new SemanticCacheScope(caller.TenantId, CacheNamespace, route.Tier), embedding, cachedAnswer);
        }

        var entry = await AuditAsync(run, AuditOutcome.Allowed, errorCode: null);
        ModelCallAccounting.RecordUsage(route.Tier, billedPrompt, billedCompletion, cost);

        return new AskResponse(
            answer, Citations(sources), answeredModel, route.Tier, CacheHit: false,
            new UsageInfo(billedPrompt, billedCompletion, cost), run.PiiCounts(), entry.Sequence, run.LatencyMs);
    }

    private async Task<Result<AskResponse>?> TryServeFromCacheAsync(
        AskRun run, PiiVault vault, ReadOnlyMemory<float> embedding, ModelRoute route, ModelTierOptions tier,
        IReadOnlyList<SourceReference> sources, CancellationToken cancellationToken)
    {
        CacheCandidate? candidate;
        try
        {
            candidate = await semanticCache.FindAsync(new SemanticCacheScope(run.Caller.TenantId, CacheNamespace, route.Tier), embedding, cancellationToken);
        }
        catch (Exception exception) when (!ModelCallAccounting.IsCallerCancellation(exception, cancellationToken))
        {
            // The cache only saves money; it must never be the reason a question goes unanswered.
            LogCacheFailed(logger, "lookup", exception.GetType().Name);
            return null;
        }

        if (candidate is null)
        {
            RecordCacheLookup("miss");
            return null;
        }

        // Same documents at the same versions means the answer is grounded on content this caller may read right now;
        // anything else (a document the caller cannot see, a newer revision) means it is not this caller's answer.
        if (!SameGrounding(candidate.Answer.Sources, sources))
        {
            RecordCacheLookup("rejected");
            return null;
        }

        RecordCacheLookup("hit");
        var cached = candidate.Answer;
        SentinelTelemetry.CostAvoided.Add(
            (double)tier.EstimateCost(cached.PromptTokens, cached.CompletionTokens),
            new KeyValuePair<string, object?>("model.tier", route.Tier));

        var answer = promptGuard.GuardOutput(cached.Answer, vault);
        run.Model = cached.Model;
        run.Sources = SourceLabels(cached.Sources);
        var entry = await AuditAsync(run, AuditOutcome.CacheHit, errorCode: null);

        return new AskResponse(
            answer, Citations(sources), cached.Model, route.Tier, CacheHit: true,
            new UsageInfo(0, 0, 0m), run.PiiCounts(), entry.Sequence, run.LatencyMs);
    }

    private async Task StoreInCacheAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CachedAnswer answer)
    {
        try
        {
            // The answer is already paid for; storing it should not depend on the caller still listening.
            await semanticCache.StoreAsync(scope, embedding, answer, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogCacheFailed(logger, "store", exception.GetType().Name);
        }
    }

    /// <summary>Longest prefix of the ranked chunks whose formatted blocks fit in <paramref name="maxContextTokens"/>.</summary>
    private TrimmedContext TrimContext(IReadOnlyList<RetrievedChunk> chunks, int maxContextTokens)
    {
        var kept = new List<RetrievedChunk>(chunks.Count);
        var documents = new List<string>(chunks.Count);
        var used = 0;

        // OrderByDescending is stable, so equally similar chunks keep the order the vector store returned.
        foreach (var chunk in chunks.OrderByDescending(c => c.Similarity))
        {
            var document = RagPrompt.FormatDocument(chunk);
            var tokens = tokenCounter.CountTokens(document);
            if (used + tokens > maxContextTokens)
            {
                break;
            }

            kept.Add(chunk);
            documents.Add(document);
            used += tokens;
        }

        return new TrimmedContext(kept, documents, used);
    }

    private static List<SourceReference> DistinctDocuments(IEnumerable<RetrievedChunk> chunks)
    {
        var seen = new HashSet<(Guid, int)>();
        var sources = new List<SourceReference>();
        foreach (var chunk in chunks)
        {
            if (seen.Add((chunk.DocumentId, chunk.DocumentVersion)))
            {
                sources.Add(new SourceReference(chunk.DocumentId, chunk.ExternalId, chunk.DocumentVersion, chunk.DocumentTitle));
            }
        }

        return sources;
    }

    private static bool SameGrounding(IReadOnlyList<SourceReference> cachedSources, IReadOnlyList<SourceReference> contextSources)
    {
        var cached = cachedSources.Select(s => (s.DocumentId, s.Version)).ToHashSet();
        return cached.Count > 0 && cached.SetEquals(contextSources.Select(s => (s.DocumentId, s.Version)));
    }

    private static List<Citation> Citations(IEnumerable<SourceReference> sources) =>
        [.. sources.Select(s => new Citation(s.ExternalId, s.Title, s.Version))];

    private static string[] SourceLabels(IEnumerable<SourceReference> sources) =>
        [.. sources.Select(s => $"{s.ExternalId}:{s.Version}")];

    private static void RecordCacheLookup(string result) =>
        SentinelTelemetry.CacheLookups.Add(1, new KeyValuePair<string, object?>("result", result));

    private async Task SettleAsync(AskRun run, int actualTokens)
    {
        if (run.Lease is not { Granted: true } lease || run.Settled)
        {
            return;
        }

        run.Settled = true;
        try
        {
            // Bookkeeping must complete even if the caller has gone away, hence no request token.
            await tokenBudget.SettleAsync(lease, actualTokens, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // The reservation stays charged: over-billing one request is safer than losing the budget's integrity.
            LogSettlementFailed(logger, exception.GetType().Name);
        }
    }

    private async Task<Result<AskResponse>> FailAsync(AskRun run, Error error)
    {
        await AuditAsync(run, AuditOutcome.Failed, error.Code);
        return error;
    }

    private async Task TryAuditFailureAsync(AskRun run, string errorCode)
    {
        try
        {
            await AuditAsync(run, AuditOutcome.Failed, errorCode);
        }
        catch (Exception exception)
        {
            LogAuditFailed(logger, exception.GetType().Name);
        }
    }

    private Task<AuditEntry> AuditAsync(AskRun run, AuditOutcome outcome, string? errorCode)
    {
        run.AuditAttempted = true;
        run.LatencyMs = timeProvider.GetElapsedTime(run.StartedAt).TotalMilliseconds;

        var auditEvent = new AuditEvent
        {
            TenantId = run.Caller.TenantId,
            SubjectId = run.Caller.SubjectId,
            Operation = AuditOperation.Ask,
            Outcome = outcome,
            OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            Model = run.Model,
            ModelTier = run.Route?.Tier,
            PromptTokens = run.PromptTokens,
            CompletionTokens = run.CompletionTokens,
            EstimatedCostUsd = run.Cost,
            RedactedPii = run.PiiCounts(),
            GuardrailFindings = [.. run.Findings.Select(f => f.Replace(',', '_')).Distinct(StringComparer.Ordinal)],
            Sources = run.Sources,
            PromptDigest = run.RedactedPrompt is null ? null : ModelCallAccounting.PromptDigest(run.RedactedPrompt),
            RedactedPrompt = auditPolicy.Value.StoreRedactedPrompts ? run.RedactedPrompt : null,
            ErrorCode = errorCode,
            LatencyMs = run.LatencyMs,
            TraceId = ModelCallAccounting.CurrentTraceId(),
        };

        // Not cancellable: an outcome that happened must be recorded even if the caller disconnected.
        return auditLog.AppendAsync(auditEvent, CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Question blocked by the prompt-injection guardrail ({Detector}: {Rules})")]
    private static partial void LogQuestionBlocked(ILogger logger, string detector, string rules);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ask dependency {Dependency} failed ({ExceptionType})")]
    private static partial void LogDependencyFailed(ILogger logger, string dependency, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The embedding provider returned {Actual}-dimensional vectors; {Expected} are required")]
    private static partial void LogEmbeddingDimensionMismatch(ILogger logger, int actual, int expected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model call on tier {Tier} failed ({ExceptionType}); the reservation is refunded")]
    private static partial void LogModelFailed(ILogger logger, string tier, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Semantic cache {Operation} failed ({ExceptionType}); continuing without the cache")]
    private static partial void LogCacheFailed(ILogger logger, string operation, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Token budget settlement failed ({ExceptionType}); the reservation stays charged")]
    private static partial void LogSettlementFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ask pipeline failed unexpectedly ({ExceptionType})")]
    private static partial void LogUnexpectedFailure(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Audit append failed ({ExceptionType}) while recording a failed request")]
    private static partial void LogAuditFailed(ILogger logger, string exceptionType);

    private sealed record TrimmedContext(IReadOnlyList<RetrievedChunk> Chunks, IReadOnlyList<string> Documents, int Tokens);

    /// <summary>What the audit entry needs, accumulated as the pipeline progresses.</summary>
    private sealed class AskRun(CallerIdentity caller, long startedAt)
    {
        public CallerIdentity Caller { get; } = caller;

        public long StartedAt { get; } = startedAt;

        public IReadOnlyDictionary<PiiType, int> InputPii { get; set; } = NoPii;

        public IReadOnlyDictionary<PiiType, int> ContextPii { get; set; } = NoPii;

        public List<string> Findings { get; } = [];

        /// <summary>The redacted question until the full prompt is built, then the redacted system + user prompt.</summary>
        public string? RedactedPrompt { get; set; }

        public ModelRoute? Route { get; set; }

        public string? Model { get; set; }

        public IReadOnlyList<string> Sources { get; set; } = [];

        public int PromptTokens { get; set; }

        public int CompletionTokens { get; set; }

        public decimal Cost { get; set; }

        public BudgetLease? Lease { get; set; }

        public bool Settled { get; set; }

        public bool AuditAttempted { get; set; }

        public double LatencyMs { get; set; }

        public Dictionary<string, int> PiiCounts() => ModelCallAccounting.PiiTypeCounts(InputPii, ContextPii);
    }
}
