using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NRedisStack;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Infrastructure.CostControls;
using Sentinel.Infrastructure.CostControls.Caching;
using StackExchange.Redis;

namespace Sentinel.Infrastructure.IntegrationTests.CostControls;

public sealed class InMemorySemanticCacheTests : SemanticCacheBehaviour
{
    protected override ISemanticCache CreateCache(SemanticCacheOptions options) =>
        new InMemorySemanticCache(new TestOptionsMonitor<SemanticCacheOptions>(options), Time);
}

[Collection(RedisContainerGroup.Name)]
public sealed class RedisSemanticCacheTests(RedisContainerFixture redis) : SemanticCacheBehaviour
{
    /// <summary>Every test (and every cache within a test) gets its own index and key prefix.</summary>
    private readonly string _isolation = Guid.NewGuid().ToString("N");
    private int _caches;

    [Fact]
    public async Task Entries_are_hashes_with_the_documented_fields_and_a_redis_ttl()
    {
        var options = Isolated(new SemanticCacheOptions { Ttl = TimeSpan.FromHours(6) });
        var cache = Create(options);
        var embedding = TestVectors.Random(1);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await cache.StoreAsync(Scope(tenant: "contoso.com"), embedding, Answer("q", first, second, first), CancellationToken.None);

        var key = Assert.Single(await KeysAsync(options.KeyPrefix));
        var fields = (await redis.Connection.GetDatabase().HashGetAllAsync(key)).ToDictionary(e => (string)e.Name!, e => e.Value);

        Assert.Equal("contoso.com", (string?)fields[RedisSemanticCache.TenantField]);
        Assert.Equal("ask", (string?)fields[RedisSemanticCache.NamespaceField]);
        Assert.Equal("fast", (string?)fields[RedisSemanticCache.TierField]);
        Assert.Equal($"{first:D},{second:D}", (string?)fields[RedisSemanticCache.DocumentsField]);
        Assert.Equal(Time.GetUtcNow().ToUnixTimeMilliseconds(), (long)fields[RedisSemanticCache.CreatedField]);
        Assert.Equal(EmbeddingDefaults.Dimensions * sizeof(float), ((byte[])fields[RedisSemanticCache.EmbeddingField]!).Length);
        using var payload = JsonDocument.Parse((string)fields[RedisSemanticCache.PayloadField]!);
        Assert.Equal("q", payload.RootElement.GetProperty("question").GetString());

        var ttl = await redis.Connection.GetDatabase().KeyTimeToLiveAsync(key);
        Assert.InRange(ttl!.Value, TimeSpan.FromHours(6) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task Index_creation_is_safe_when_several_instances_start_at_once()
    {
        var options = Isolated(new SemanticCacheOptions());
        var instances = Enumerable.Range(0, 8).Select(_ => (RedisSemanticCache)Create(options)).ToArray();

        await Task.WhenAll(instances.Select(i => i.EnsureIndexAsync(CancellationToken.None)));

        var indexes = await new SearchCommandsAsync(redis.Connection.GetDatabase())._ListAsync();
        Assert.Single(indexes, i => (string?)i == options.IndexName);

        var embedding = TestVectors.Random(1);
        await instances[0].StoreAsync(Scope(), embedding, Answer("shared"), CancellationToken.None);
        Assert.Equal("shared", (await instances[7].FindAsync(Scope(), embedding, CancellationToken.None))?.Answer.Question);
    }

    [Fact]
    public async Task A_dropped_index_is_recreated_and_existing_entries_are_found_again()
    {
        var options = Isolated(new SemanticCacheOptions());
        var cache = Create(options);
        var embedding = TestVectors.Random(1);
        await cache.StoreAsync(Scope(), embedding, Answer("survives"), CancellationToken.None);

        await new SearchCommandsAsync(redis.Connection.GetDatabase()).DropIndexAsync(options.IndexName);

        // The first lookup notices the missing index (a miss, not an exception); later ones re-create it, and the
        // re-created index picks up the hashes that are still there (asynchronous initial scan).
        Assert.Null(await cache.FindAsync(Scope(), embedding, CancellationToken.None));
        CacheCandidate? hit = null;
        for (var attempt = 0; attempt < 50 && hit is null; attempt++)
        {
            hit = await cache.FindAsync(Scope(), embedding, CancellationToken.None);
            if (hit is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.Equal("survives", hit?.Answer.Question);
    }

    protected override ISemanticCache CreateCache(SemanticCacheOptions options) => Create(Isolated(options));

    private RedisSemanticCache Create(SemanticCacheOptions options)
    {
        redis.SkipIfUnavailable();
        return new RedisSemanticCache(
            redis.Connection, new TestOptionsMonitor<SemanticCacheOptions>(options), Time, NullLogger<RedisSemanticCache>.Instance);
    }

    private SemanticCacheOptions Isolated(SemanticCacheOptions options)
    {
        var n = Interlocked.Increment(ref _caches);
        options.IndexName = $"test-{_isolation}-{n}";
        options.KeyPrefix = $"test:{_isolation}:{n}:";
        return options;
    }

    private async Task<List<RedisKey>> KeysAsync(string prefix)
    {
        redis.SkipIfUnavailable();
        var server = redis.Connection.GetServers()[0];
        var keys = new List<RedisKey>();
        await foreach (var key in server.KeysAsync(pattern: prefix + "*"))
        {
            keys.Add(key);
        }

        return keys;
    }
}
