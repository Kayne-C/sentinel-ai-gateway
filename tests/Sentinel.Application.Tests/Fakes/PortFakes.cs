using Microsoft.Extensions.AI;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Domain.Audit;
using Sentinel.Domain.Common;
using RoutingContext = Sentinel.Application.Abstractions.RoutingContext;

namespace Sentinel.Application.Tests.Fakes;

internal sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<string> Inputs { get; } = [];

    public Exception? Failure { get; set; }

    public int Dimensions { get; set; } = EmbeddingDefaults.Dimensions;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var inputs = values.ToList();
        Inputs.AddRange(inputs);
        if (Failure is not null)
        {
            throw Failure;
        }

        var vector = Enumerable.Repeat(1f / MathF.Sqrt(Dimensions), Dimensions).ToArray();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(inputs.Select(_ => new Embedding<float>(vector))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class FakeVectorSearch : IVectorSearch
{
    public List<VectorQuery> Queries { get; } = [];

    public List<RetrievedChunk> Results { get; } = [];

    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(VectorQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult<IReadOnlyList<RetrievedChunk>>([.. Results]);
    }
}

internal sealed class FakeSemanticCache : ISemanticCache
{
    public CacheCandidate? Candidate { get; set; }

    public List<SemanticCacheScope> Lookups { get; } = [];

    public List<(SemanticCacheScope Scope, CachedAnswer Answer)> Stored { get; } = [];

    public Task<CacheCandidate?> FindAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken)
    {
        Lookups.Add(scope);
        return Task.FromResult(Candidate);
    }

    public Task StoreAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CachedAnswer answer, CancellationToken cancellationToken)
    {
        Stored.Add((scope, answer));
        return Task.CompletedTask;
    }

    public Task InvalidateDocumentAsync(string tenantId, Guid documentId, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FakeTokenBudget : ITokenBudget
{
    public bool Grant { get; set; } = true;

    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(42);

    public List<BudgetRequest> Reservations { get; } = [];

    public List<(BudgetLease Lease, int ActualTokens)> Settlements { get; } = [];

    public Task<BudgetLease> ReserveAsync(BudgetRequest request, CancellationToken cancellationToken)
    {
        Reservations.Add(request);
        var lease = Grant
            ? new BudgetLease(true, request.TenantId, request.SubjectId, request.EstimatedTokens, 1_000_000, 100_000, null, null)
            : BudgetLease.Denied(request, "subject", 1_000_000, 0, RetryAfter);
        return Task.FromResult(lease);
    }

    public Task SettleAsync(BudgetLease lease, int actualTokens, CancellationToken cancellationToken)
    {
        Settlements.Add((lease, actualTokens));
        return Task.CompletedTask;
    }

    public Task<BudgetStatus> GetStatusAsync(string tenantId, string subjectId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class FakeAuditLog : IAuditLog
{
    public List<AuditEvent> Events { get; } = [];

    public Task<AuditEntry> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Events.Add(auditEvent);
        var sequence = Events.Count;
        return Task.FromResult(new AuditEntry(sequence, auditEvent, null, HashChain.Compute(null, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    public Task<AuditVerification> VerifyAsync(string tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PagedResponse<AuditEntry>> ListAsync(AuditQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeModelRouter : IModelRouter
{
    public ModelRoute Route { get; set; } = new("fast", "test-fast", 0.1, ["test"]);

    public List<RoutingContext> Contexts { get; } = [];

    ModelRoute IModelRouter.Route(RoutingContext context)
    {
        Contexts.Add(context);
        return Route;
    }
}

/// <summary>One token per whitespace-separated word: easy to reason about in assertions.</summary>
internal sealed class WordTokenCounter : ITokenCounter
{
    public int CountTokens(string text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>A model that records exactly what it was sent and answers from a script.</summary>
internal sealed class ScriptedChatClient : IChatClient
{
    public List<(List<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public Func<IReadOnlyList<ChatMessage>, string> Answer { get; set; } = _ => "Scripted answer.";

    public UsageDetails? Usage { get; set; } = new() { InputTokenCount = 120, OutputTokenCount = 30, TotalTokenCount = 150 };

    public string? ModelId { get; set; }

    public Exception? Failure { get; set; }

    public string AllText => string.Join("\n", Calls.SelectMany(c => c.Messages).Select(m => m.Text));

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var received = messages.ToList();
        Calls.Add((received, options));
        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer(received)))
        {
            Usage = Usage,
            ModelId = ModelId ?? options?.ModelId,
        });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class FakeChatClientProvider(IChatClient client) : IChatClientProvider
{
    public List<string> Tiers { get; } = [];

    public IChatClient GetClient(string tier)
    {
        Tiers.Add(tier);
        return client;
    }
}
