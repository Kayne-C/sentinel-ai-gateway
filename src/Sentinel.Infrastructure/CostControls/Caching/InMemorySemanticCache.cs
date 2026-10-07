using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;

namespace Sentinel.Infrastructure.CostControls.Caching;

/// <summary>
/// Process-local semantic cache: one bounded list per scope, scanned linearly (cosine over unit vectors).
/// </summary>
/// <remarks>
/// Same contract as <see cref="RedisSemanticCache"/>. The cache only partitions: it stores whatever the application
/// gives it and returns the most similar answer of exactly the same scope (ordinal, case-sensitive match of tenant,
/// namespace and tier). Deciding what is eligible for caching (an answer built from the caller's own PII is not) and
/// re-validating a hit's sources against the caller's current permissions are the application's job.
/// A linear scan is fine at <see cref="SemanticCacheOptions.MaxEntriesPerScope"/> scale (5000 × 384 floats ≈ 2 ms);
/// multi-instance deployments use Redis anyway.
/// </remarks>
internal sealed class InMemorySemanticCache(IOptionsMonitor<SemanticCacheOptions> options, TimeProvider timeProvider) : ISemanticCache
{
    private readonly ConcurrentDictionary<SemanticCacheScope, Partition> _partitions = new();

    public Task<CacheCandidate?> FindAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken)
    {
        VectorMath.EnsureDimensions(embedding, nameof(embedding));
        cancellationToken.ThrowIfCancellationRequested();

        if (!CacheScopeRules.IsCacheable(scope)
            || !VectorMath.TryNormalize(embedding.Span, out var query)
            || !_partitions.TryGetValue(scope, out var partition))
        {
            return Task.FromResult<CacheCandidate?>(null);
        }

        var settings = options.CurrentValue;
        var best = partition.FindMostSimilar(query, timeProvider.GetUtcNow(), settings.Ttl);
        return Task.FromResult(best is not null && best.Similarity >= settings.SimilarityThreshold ? best : null);
    }

    public Task StoreAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CachedAnswer answer, CancellationToken cancellationToken)
    {
        VectorMath.EnsureDimensions(embedding, nameof(embedding));
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(answer.Sources, nameof(answer));
        cancellationToken.ThrowIfCancellationRequested();

        if (!CacheScopeRules.IsCacheable(scope) || !VectorMath.TryNormalize(embedding.Span, out var vector))
        {
            return Task.CompletedTask;
        }

        // Copy the sources: the stored answer must not change if the caller later mutates its list.
        var stored = answer with { Sources = [.. answer.Sources] };
        var documents = stored.Sources.Select(s => s.DocumentId).Distinct().ToArray();
        var settings = options.CurrentValue;
        var now = timeProvider.GetUtcNow();

        _partitions.GetOrAdd(scope, static _ => new Partition())
            .Add(new Entry(vector, stored, documents, now), now, settings.Ttl, Math.Max(1, settings.MaxEntriesPerScope));
        return Task.CompletedTask;
    }

    public Task InvalidateDocumentAsync(string tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var (scope, partition) in _partitions)
        {
            if (string.Equals(scope.TenantId, tenantId, StringComparison.Ordinal))
            {
                partition.RemoveCiting(documentId);
            }
        }

        return Task.CompletedTask;
    }

    private sealed record Entry(float[] Vector, CachedAnswer Answer, Guid[] Documents, DateTimeOffset StoredAt);

    /// <summary>Entries of one scope, oldest first; every access is under the partition's lock.</summary>
    private sealed class Partition
    {
        private readonly Lock _gate = new();
        private readonly List<Entry> _entries = [];

        public CacheCandidate? FindMostSimilar(float[] query, DateTimeOffset now, TimeSpan ttl)
        {
            lock (_gate)
            {
                RemoveExpired(now, ttl);

                Entry? best = null;
                var bestSimilarity = double.NegativeInfinity;

                // Newest first, strictly greater wins: on a tie the most recent answer is served.
                for (var i = _entries.Count - 1; i >= 0; i--)
                {
                    var similarity = VectorMath.CosineOfUnitVectors(query, _entries[i].Vector);
                    if (similarity > bestSimilarity)
                    {
                        best = _entries[i];
                        bestSimilarity = similarity;
                    }
                }

                return best is null ? null : new CacheCandidate(best.Answer, bestSimilarity);
            }
        }

        public void Add(Entry entry, DateTimeOffset now, TimeSpan ttl, int maxEntries)
        {
            lock (_gate)
            {
                RemoveExpired(now, ttl);

                var excess = _entries.Count + 1 - maxEntries;
                if (excess > 0)
                {
                    _entries.RemoveRange(0, excess);
                }

                _entries.Add(entry);
            }
        }

        public void RemoveCiting(Guid documentId)
        {
            lock (_gate)
            {
                _entries.RemoveAll(e => Array.IndexOf(e.Documents, documentId) >= 0);
            }
        }

        private void RemoveExpired(DateTimeOffset now, TimeSpan ttl) => _entries.RemoveAll(e => now - e.StoredAt >= ttl);
    }
}
