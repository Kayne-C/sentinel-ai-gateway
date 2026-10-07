using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NRedisStack;
using NRedisStack.Search;
using NRedisStack.Search.Literals.Enums;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using StackExchange.Redis;

namespace Sentinel.Infrastructure.CostControls.Caching;

/// <summary>
/// Semantic cache on the Redis 8 Query Engine: one HASH per answer, an HNSW cosine index over the embeddings and TAG
/// fields that partition the KNN search by tenant, namespace and model tier.
/// </summary>
/// <remarks>
/// <para><b>Contract.</b> The cache stores whatever the application gives it and partitions by scope; it does not judge
/// content. The application decides eligibility (an answer built from the caller's own PII must not be cached, because
/// other users of the tenant would receive it) and re-validates the sources of every hit against the caller's current
/// permissions and the documents' current versions before serving it. The namespace is opaque to the cache: an
/// application that wants answers partitioned by permission set as well can include a fingerprint of the caller's
/// principals in it.</para>
/// <para><b>Isolation.</b> The scope is a pre-filter of the KNN query (<c>(@tenant:{..} @ns:{..} @tier:{..})=>[KNN 1 ..]</c>),
/// so another partition's vectors are never even ranked. Tag values are escaped and stored exactly as described in
/// <see cref="RedisTag"/>, and every result is re-checked against the scope byte-for-byte before it is returned.</para>
/// <para><b>Availability.</b> The cache is an optimisation: when Redis is unreachable, lookups are misses and stores are
/// skipped (logged), so the gateway keeps answering. Invalidation, in contrast, reports failures to the caller.</para>
/// <para><b>Index.</b> Created on startup if missing (<c>FT.CREATE</c>; "already exists" from a concurrently starting
/// instance counts as success) and re-created lazily if it disappears. An existing index of the same name is reused
/// as-is: change <see cref="SemanticCacheOptions.IndexName"/> when the schema changes.</para>
/// </remarks>
internal sealed partial class RedisSemanticCache : ISemanticCache, IDisposable
{
    internal const string TenantField = "tenant";
    internal const string NamespaceField = "ns";
    internal const string TierField = "tier";
    internal const string DocumentsField = "docs";
    internal const string CreatedField = "created";
    internal const string EmbeddingField = "embedding";
    internal const string PayloadField = "payload";

    private const string DistanceField = "__distance";
    private const string VectorParameter = "vector";
    private const int InvalidationPageSize = 500;
    private const int MaxInvalidationRounds = 10_000;
    private const int MaxEvictionBatch = 1_000;
    private const int InitialScanMaxPolls = 200;
    private static readonly TimeSpan InitialScanPollInterval = TimeSpan.FromMilliseconds(25);

    private static readonly long MaxUnixMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private readonly IDatabase _database;
    private readonly SearchCommandsAsync _search;
    private readonly IOptionsMonitor<SemanticCacheOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RedisSemanticCache> _logger;
    private readonly string _indexName;
    private readonly string _keyPrefix;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexReady;

    public RedisSemanticCache(
        IConnectionMultiplexer redis,
        IOptionsMonitor<SemanticCacheOptions> options,
        TimeProvider timeProvider,
        ILogger<RedisSemanticCache> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        _database = redis.GetDatabase();
        _search = new SearchCommandsAsync(_database);
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;

        // Index and key layout are fixed for the lifetime of the process; thresholds and TTL are read per call.
        var settings = options.CurrentValue;
        _indexName = settings.IndexName;
        _keyPrefix = settings.KeyPrefix;
    }

    /// <summary>
    /// Lists the cache keys that cite one document: a plain Redis set updated in the same MULTI/EXEC as the entry.
    /// Invalidation is a security control (an answer built from a document whose ACL just changed must go away), so it
    /// must not depend on the query index, which is maintained asynchronously and can briefly drop an entry while it
    /// is being re-indexed. The set is plain keyspace state: read-your-writes. The key starts with a marker
    /// that is not <see cref="SemanticCacheOptions.KeyPrefix"/>, so the index never sees it. The tenant id is
    /// followed by a fixed-width document id, which keeps (tenant, document) pairs unambiguous.
    /// </summary>
    private RedisKey DocumentSetKey(string tenantId, Guid documentId) => $"~docs~{_keyPrefix}{tenantId}:{documentId:N}";

    public async Task<CacheCandidate?> FindAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken)
    {
        VectorMath.EnsureDimensions(embedding, nameof(embedding));
        if (!CacheScopeRules.IsCacheable(scope))
        {
            LogScopeNotCacheable(_logger);
            return null;
        }

        if (!VectorMath.TryNormalize(embedding.Span, out _))
        {
            return null;
        }

        var settings = _options.CurrentValue;
        try
        {
            await EnsureIndexAsync(cancellationToken);

            // Expired entries are excluded inside the pre-filter as well, so they cannot shadow a valid neighbour.
            var notBefore = (_timeProvider.GetUtcNow() - settings.Ttl).ToUnixTimeMilliseconds();
            var filter = $"{ScopeFilter(scope)} @{CreatedField}:[({notBefore.ToString(CultureInfo.InvariantCulture)} +inf]";
            // ADHOC_BF: exact distance computation over the entries that pass the filter. The default policy may walk the
            // HNSW graph first and filter afterwards, which can miss an existing entry of this scope (recall, not
            // leakage: the filter always applies). A scope holds at most MaxEntriesPerScope entries, so scanning it is cheap.
            var query = new Query($"({filter})=>[KNN 1 @{EmbeddingField} ${VectorParameter} HYBRID_POLICY ADHOC_BF AS {DistanceField}]")
                .AddParam(VectorParameter, VectorMath.ToLittleEndianBytes(embedding.Span))
                .SetSortBy(DistanceField, ascending: true)
                .ReturnFields(TenantField, NamespaceField, TierField, CreatedField, PayloadField, DistanceField)
                .Limit(0, 1)
                .Dialect(2);

            cancellationToken.ThrowIfCancellationRequested();
            var result = await _search.SearchAsync(_indexName, query);
            return result.Documents.Count == 0 ? null : ToCandidate(result.Documents[0], scope, settings);
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            NoteFailure(exception);
            LogLookupFailed(_logger, exception);
            return null;
        }
    }

    public async Task StoreAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CachedAnswer answer, CancellationToken cancellationToken)
    {
        VectorMath.EnsureDimensions(embedding, nameof(embedding));
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(answer.Sources, nameof(answer));
        if (!CacheScopeRules.IsCacheable(scope))
        {
            LogScopeNotCacheable(_logger);
            return;
        }

        if (!VectorMath.TryNormalize(embedding.Span, out _))
        {
            return;
        }

        var settings = _options.CurrentValue;
        try
        {
            await EnsureIndexAsync(cancellationToken);

            var now = _timeProvider.GetUtcNow();
            RedisKey key = _keyPrefix + Guid.CreateVersion7(now).ToString("N");
            var documents = string.Join(',', answer.Sources.Select(s => s.DocumentId).Distinct().Select(id => id.ToString("D")));
            HashEntry[] fields =
            [
                new(TenantField, scope.TenantId),
                new(NamespaceField, scope.Namespace),
                new(TierField, scope.ModelTier),
                new(DocumentsField, documents),
                new(CreatedField, now.ToUnixTimeMilliseconds()),
                new(EmbeddingField, VectorMath.ToLittleEndianBytes(embedding.Span)),
                new(PayloadField, JsonSerializer.Serialize(answer, CacheJsonContext.Default.CachedAnswer)),
            ];

            // One MULTI/EXEC: an entry never exists without its TTL, even if the connection drops in between.
            cancellationToken.ThrowIfCancellationRequested();
            var transaction = _database.CreateTransaction();
            var pending = new List<Task>
            {
                transaction.HashSetAsync(key, fields),
                transaction.KeyExpireAsync(key, settings.Ttl),
            };
            foreach (var documentId in answer.Sources.Select(source => source.DocumentId).Distinct())
            {
                var setKey = DocumentSetKey(scope.TenantId, documentId);
                pending.Add(transaction.SetAddAsync(setKey, key.ToString()));
                pending.Add(transaction.KeyExpireAsync(setKey, settings.Ttl));
            }

            await transaction.ExecuteAsync();
            await Task.WhenAll(pending);

            await EvictOverflowAsync(scope, Math.Max(1, settings.MaxEntriesPerScope));
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            NoteFailure(exception);
            LogStoreFailed(_logger, exception);
        }
    }

    public async Task InvalidateDocumentAsync(string tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (!RedisTag.IsRepresentable(tenantId))
        {
            // Nothing can have been stored for such a tenant (stores are skipped), so there is nothing to drop.
            return;
        }

        try
        {
            await EnsureIndexAsync(cancellationToken);

            // 1. Authoritative, read-your-writes: every entry that cited the document is listed in its set.
            long removed = 0;
            var setKey = DocumentSetKey(tenantId, documentId);
            var members = await _database.SetMembersAsync(setKey);
            if (members.Length > 0)
            {
                removed += await _database.KeyDeleteAsync(members.Select(member => (RedisKey)(string)member!).ToArray());

                // Remove only what was read, so an entry stored concurrently keeps its set membership.
                await _database.SetRemoveAsync(setKey, members);
            }

            // 2. Safety net through the index for entries without a set (written by an older version, set expired).
            var filter = $"{RedisTag.Filter(TenantField, tenantId)} {RedisTag.Filter(DocumentsField, documentId.ToString("D"))}";
            var offset = 0;
            for (var round = 0; round < MaxInvalidationRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _search.SearchAsync(
                    _indexName, new Query(filter).SetNoContent().Limit(offset, InvalidationPageSize).Dialect(2));
                if (page.Documents.Count == 0)
                {
                    break;
                }

                var keys = page.Documents.Select(d => (RedisKey)d.Id).ToArray();
                var deleted = await _database.KeyDeleteAsync(keys);
                removed += deleted;

                // A deleted key (by us, a concurrent invalidation or expiry) leaves the index at once, so the next page
                // starts at offset 0 again. Only a page with nothing left to delete (keys the index still lists but the
                // keyspace no longer has) is stepped over, so that it cannot be re-read forever.
                if (deleted == 0)
                {
                    offset += keys.Length;
                }
            }

            LogInvalidated(_logger, removed, documentId);
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            NoteFailure(exception);
            throw;
        }
    }

    public void Dispose() => _indexGate.Dispose();

    /// <summary>Creates the index once per process; safe when several gateway instances start at the same time.</summary>
    internal async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (_indexReady)
        {
            return;
        }

        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            if (_indexReady)
            {
                return;
            }

            try
            {
                await _search.CreateAsync(_indexName, IndexDefinition(_keyPrefix), IndexSchema());
                LogIndexCreated(_logger, _indexName);
            }
            catch (RedisServerException exception) when (exception.Message.Contains("Index already exists", StringComparison.OrdinalIgnoreCase))
            {
                LogIndexExists(_logger, _indexName);
            }

            await WaitForInitialScanAsync(cancellationToken);
            _indexReady = true;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    /// <summary>
    /// FT.CREATE returns at once and scans the existing keyspace in the background. A hash written while that scan is
    /// running can be indexed twice (delete + add), so for a moment it vanishes from the results. That is harmless for
    /// a cache (a miss) but makes the first lookups after a start or an index re-creation unreliable, so wait — a
    /// bounded time, never failing — until the scan has finished.
    /// </summary>
    private async Task WaitForInitialScanAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < InitialScanMaxPolls; attempt++)
        {
            try
            {
                var info = await _search.InfoAsync(_indexName);
                if (info.Indexing == 0)
                {
                    return;
                }
            }
            catch (RedisServerException)
            {
                return; // The index is gone again or INFO is unsupported: nothing to wait for.
            }

            await Task.Delay(InitialScanPollInterval, cancellationToken);
        }

        LogInitialScanSlow(_logger, _indexName);
    }

    internal static bool IsRedisFailure(Exception exception) =>
        exception is RedisException or RedisTimeoutException or RedisCommandException;

    internal static FTCreateParams IndexDefinition(string keyPrefix) =>
        new FTCreateParams().On(IndexDataType.HASH).Prefix(keyPrefix);

    internal static Schema IndexSchema()
    {
        var separator = RedisTag.SingleValueSeparator.ToString();
        return new Schema()
            .AddTagField(TenantField, separator: separator, caseSensitive: true)
            .AddTagField(NamespaceField, separator: separator, caseSensitive: true)
            .AddTagField(TierField, separator: separator, caseSensitive: true)
            .AddTagField(DocumentsField, separator: ",")
            .AddNumericField(CreatedField, sortable: true)
            .AddVectorField(EmbeddingField, Schema.VectorField.VectorAlgo.HNSW, new Dictionary<string, object>
            {
                ["TYPE"] = "FLOAT32",
                ["DIM"] = EmbeddingDefaults.Dimensions,
                ["DISTANCE_METRIC"] = "COSINE",
            });
    }

    private static string ScopeFilter(SemanticCacheScope scope) =>
        $"{RedisTag.Filter(TenantField, scope.TenantId)} {RedisTag.Filter(NamespaceField, scope.Namespace)} {RedisTag.Filter(TierField, scope.ModelTier)}";

    private CacheCandidate? ToCandidate(Document document, SemanticCacheScope scope, SemanticCacheOptions settings)
    {
        // Defence in depth: the pre-filter already restricted the search to this scope. Verify the stored values
        // exactly anyway, so no indexing or escaping subtlety can ever hand out another partition's answer.
        if (!Matches(document[TenantField], scope.TenantId)
            || !Matches(document[NamespaceField], scope.Namespace)
            || !Matches(document[TierField], scope.ModelTier))
        {
            LogScopeMismatch(_logger);
            return null;
        }

        if (!document[CreatedField].TryParse(out long createdMs) || createdMs < 0 || createdMs > MaxUnixMilliseconds)
        {
            LogUnreadableEntry(_logger);
            return null;
        }

        if (_timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(createdMs) >= settings.Ttl)
        {
            return null;
        }

        if (!double.TryParse((string?)document[DistanceField], NumberStyles.Float, CultureInfo.InvariantCulture, out var distance))
        {
            LogUnreadableEntry(_logger);
            return null;
        }

        // Cosine distance is 1 - cosine similarity; a NaN fails the comparison and is a miss.
        var similarity = Math.Clamp(1 - distance, -1, 1);
        if (!(similarity >= settings.SimilarityThreshold))
        {
            return null;
        }

        try
        {
            var payload = (string?)document[PayloadField];
            var answer = payload is null ? null : JsonSerializer.Deserialize(payload, CacheJsonContext.Default.CachedAnswer);
            if (answer is null || answer.Question is null || answer.Answer is null || answer.Model is null || answer.Sources is null)
            {
                LogUnreadableEntry(_logger);
                return null;
            }

            return new CacheCandidate(answer, similarity);
        }
        catch (JsonException)
        {
            LogUnreadableEntry(_logger);
            return null;
        }
    }

    /// <summary>Keeps the scope at <paramref name="maxEntries"/> by dropping its oldest entries (best effort, not atomic).</summary>
    private async Task EvictOverflowAsync(SemanticCacheScope scope, int maxEntries)
    {
        var filter = ScopeFilter(scope);
        var count = await _search.SearchAsync(_indexName, new Query(filter).SetNoContent().Limit(0, 0).Dialect(2));
        var excess = count.TotalResults - maxEntries;
        if (excess <= 0)
        {
            return;
        }

        var oldest = await _search.SearchAsync(_indexName, new Query(filter)
            .SetNoContent()
            .SetSortBy(CreatedField, ascending: true)
            .Limit(0, (int)Math.Min(excess, MaxEvictionBatch))
            .Dialect(2));

        var keys = oldest.Documents.Select(d => (RedisKey)d.Id).ToArray();
        if (keys.Length > 0)
        {
            LogEvicted(_logger, await _database.KeyDeleteAsync(keys));
        }
    }

    /// <summary>If the index was dropped (FLUSHALL, operator), the next call re-creates it instead of failing forever.</summary>
    private void NoteFailure(Exception exception)
    {
        if (exception is RedisServerException server
            && (server.Message.Contains("Index not found", StringComparison.OrdinalIgnoreCase)
                || server.Message.Contains("Unknown index", StringComparison.OrdinalIgnoreCase)
                || server.Message.Contains("no such index", StringComparison.OrdinalIgnoreCase)))
        {
            _indexReady = false;
        }
    }

    private static bool Matches(RedisValue stored, string expected) =>
        !stored.IsNull && string.Equals((string?)stored, expected, StringComparison.Ordinal);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created semantic cache index {IndexName}")]
    private static partial void LogIndexCreated(ILogger logger, string indexName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Semantic cache index {IndexName} already exists")]
    private static partial void LogIndexExists(ILogger logger, string indexName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Semantic cache index {IndexName} is still scanning existing keys; continuing without waiting further")]
    private static partial void LogInitialScanSlow(ILogger logger, string indexName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Semantic cache lookup failed; treating it as a miss")]
    private static partial void LogLookupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Semantic cache store failed; the answer is not cached")]
    private static partial void LogStoreFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Semantic cache scope cannot be partitioned exactly (control characters, surrounding whitespace or too long); caching is skipped for it")]
    private static partial void LogScopeNotCacheable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Semantic cache search returned an entry outside the requested scope; it was discarded")]
    private static partial void LogScopeMismatch(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discarded an unreadable semantic cache entry")]
    private static partial void LogUnreadableEntry(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Evicted {Count} oldest semantic cache entries to respect MaxEntriesPerScope")]
    private static partial void LogEvicted(ILogger logger, long count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Invalidated {Count} semantic cache entries citing document {DocumentId}")]
    private static partial void LogInvalidated(ILogger logger, long count, Guid documentId);
}
