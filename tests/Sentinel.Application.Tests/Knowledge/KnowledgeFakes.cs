using Microsoft.Extensions.AI;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Application.Knowledge;
using Sentinel.Domain.Audit;
using Sentinel.Domain.Knowledge;
using Sentinel.Guardrails.Injection;

namespace Sentinel.Application.Tests.Knowledge;

/// <summary>One token per whitespace-separated word: additive, so token bounds can be asserted exactly.</summary>
internal sealed class WordTokenCounter : ITokenCounter
{
    public int Calls { get; private set; }

    public int CountTokens(string text)
    {
        Calls++;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}

/// <summary>Flags any text containing the marker phrase, like a rule-based detector would.</summary>
internal sealed class MarkerInjectionDetector : IPromptInjectionDetector
{
    public const string Marker = "ignore previous instructions";
    public const string Rule = "injection.override";

    public List<(string Text, ContentOrigin Origin)> Inspected { get; } = [];

    public ValueTask<InjectionVerdict> InspectAsync(string text, ContentOrigin origin, CancellationToken cancellationToken = default)
    {
        Inspected.Add((text, origin));
        return ValueTask.FromResult(text.Contains(Marker, StringComparison.OrdinalIgnoreCase)
            ? new InjectionVerdict(true, 0.95, [Rule], "marker")
            : InjectionVerdict.Clean("marker"));
    }
}

/// <summary>Deterministic vectors derived from the input text; counts calls and inputs.</summary>
internal sealed class CountingEmbeddingGenerator(int dimensions = EmbeddingDefaults.Dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Exception? Failure { get; set; }

    public int TotalInputs => Calls.Sum(c => c.Count);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var inputs = values.ToList();
        Calls.Add(inputs);
        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(inputs.Select(i => new Embedding<float>(VectorFor(i, dimensions)))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("fake", null, "fake-embedding", dimensions) : null;

    public void Dispose()
    {
    }

    public static float[] VectorFor(string text, int dimensions)
    {
        var seed = 17;
        foreach (var c in text)
        {
            seed = unchecked(seed * 31 + c);
        }

        var random = new Random(seed);
        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2 - 1);
        }

        vector[0] += 0.5f;
        return vector;
    }
}

/// <summary>Shared, ordered record of side effects so tests can assert "invalidate after save".</summary>
internal sealed class Journal
{
    public List<string> Entries { get; } = [];
}

internal sealed class RecordingSemanticCache(Journal journal) : ISemanticCache
{
    public List<(string TenantId, Guid DocumentId)> Invalidations { get; } = [];

    /// <summary>Invalidation calls (1-based) that throw.</summary>
    public HashSet<int> FailingCalls { get; } = [];

    public Task<CacheCandidate?> FindAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken) =>
        Task.FromResult<CacheCandidate?>(null);

    public Task StoreAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CachedAnswer answer, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task InvalidateDocumentAsync(string tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        Invalidations.Add((tenantId, documentId));
        journal.Entries.Add("invalidate");
        if (FailingCalls.Contains(Invalidations.Count))
        {
            throw new InvalidOperationException("cache unavailable");
        }

        return Task.CompletedTask;
    }
}

internal sealed class RecordingAuditLog(Journal journal) : IAuditLog
{
    public List<AuditEvent> Events { get; } = [];

    public Task<AuditEntry> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Events.Add(auditEvent);
        journal.Entries.Add("audit");
        return Task.FromResult(new AuditEntry(Events.Count, auditEvent, null, "hash"));
    }

    public Task<AuditVerification> VerifyAsync(string tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<PagedResponse<AuditEntry>> ListAsync(AuditQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>
/// Behaves like the EF repository where it matters: returns fresh copies (the handler mutates what it loads),
/// keeps chunks when <c>chunks</c> is null and refuses to overwrite a newer version.
/// </summary>
internal sealed class InMemoryKnowledgeRepository(Journal journal) : IKnowledgeRepository, IDocumentChunkCounter
{
    private readonly Dictionary<(string TenantId, string ExternalId), (KnowledgeDocument Document, IReadOnlyList<EmbeddedChunk> Chunks)> _store = [];

    public List<(KnowledgeDocument Document, IReadOnlyList<EmbeddedChunk>? Chunks)> Saves { get; } = [];

    public List<string> FindTenants { get; } = [];

    public (string TenantId, IReadOnlyCollection<string>? Principals)? LastList { get; private set; }

    public IReadOnlyList<DocumentSummary> ListResult { get; set; } = [];

    public bool ThrowConcurrency { get; set; }

    public IReadOnlyList<EmbeddedChunk> StoredChunks(string tenantId, string externalId) => _store[(tenantId, externalId)].Chunks;

    public KnowledgeDocument? Stored(string tenantId, string externalId) =>
        _store.TryGetValue((tenantId, externalId), out var entry) ? Copy(entry.Document) : null;

    public Task<KnowledgeDocument?> FindAsync(string tenantId, string externalId, CancellationToken cancellationToken)
    {
        FindTenants.Add(tenantId);
        return Task.FromResult(Stored(tenantId, externalId));
    }

    public Task SaveAsync(KnowledgeDocument document, IReadOnlyList<EmbeddedChunk>? chunks, CancellationToken cancellationToken)
    {
        journal.Entries.Add("save");
        if (ThrowConcurrency)
        {
            throw new KnowledgeConcurrencyException("lost the race");
        }

        var key = (document.TenantId, document.ExternalId);
        if (_store.TryGetValue(key, out var existing) && existing.Document.Version >= document.Version)
        {
            throw new KnowledgeConcurrencyException("stale version");
        }

        Saves.Add((Copy(document), chunks));
        _store[key] = (Copy(document), chunks ?? (_store.TryGetValue(key, out var previous) ? previous.Chunks : []));
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string tenantId, string externalId, CancellationToken cancellationToken)
    {
        journal.Entries.Add("delete");
        return Task.FromResult(_store.Remove((tenantId, externalId)));
    }

    public Task<PagedResponse<DocumentSummary>> ListAsync(
        string tenantId, IReadOnlyCollection<string>? principals, int page, int pageSize, CancellationToken cancellationToken)
    {
        LastList = (tenantId, principals);
        return Task.FromResult(new PagedResponse<DocumentSummary>(ListResult, page, pageSize, ListResult.Count));
    }

    public Task<DocumentChunkCounts> CountChunksAsync(string tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        var chunks = _store.Values.Where(v => v.Document.TenantId == tenantId && v.Document.Id == documentId).SelectMany(v => v.Chunks).ToList();
        return Task.FromResult(new DocumentChunkCounts(chunks.Count, chunks.Count(c => c.Chunk.Quarantined)));
    }

    private static KnowledgeDocument Copy(KnowledgeDocument d) => KnowledgeDocument.Restore(
        d.Id, d.TenantId, d.ExternalId, d.Title, d.SourceUri, d.Classification, d.Version, d.ContentHash, d.Principals, d.CreatedAtUtc, d.UpdatedAtUtc);
}
