using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Sentinel.Infrastructure.CostControls;
using Sentinel.Infrastructure.CostControls.Caching;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Sentinel.Infrastructure.IntegrationTests.CostControls;

/// <summary>
/// One <c>redis:8</c> container for the cost-control tests. Docker being unavailable skips the tests; a Redis image
/// without the Query Engine, or a broken index definition, fails them (the fixture creates the production index).
/// </summary>
public sealed class RedisContainerFixture : IAsyncLifetime
{
    public const string Image = "redis:8";

    private RedisContainer? _container;
    private ConnectionMultiplexer? _connection;

    public string ConnectionString { get; private set; } = string.Empty;

    public IConnectionMultiplexer Connection => _connection ?? throw new InvalidOperationException("Redis is not available.");

    private string? UnavailableReason { get; set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new RedisBuilder(Image).Build();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await _container.StartAsync(timeout.Token);
            ConnectionString = _container.GetConnectionString();
            _connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        }
#pragma warning disable CA1031 // Any failure to obtain a container means "no Docker here": skip, do not fail.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            UnavailableReason = $"Docker/{Image} unavailable: {exception.GetType().Name}: {exception.Message}";
            await DisposeAsync();
            return;
        }

        // Not caught on purpose: if FT.CREATE with the production schema fails, the tests must fail, not skip.
        using var probe = new RedisSemanticCache(
            Connection,
            new TestOptionsMonitor<SemanticCacheOptions>(new SemanticCacheOptions { IndexName = "fixture-probe", KeyPrefix = "fixture-probe:" }),
            new FakeTimeProvider(),
            NullLogger<RedisSemanticCache>.Instance);
        await probe.EnsureIndexAsync(CancellationToken.None);
    }

    /// <summary>Call first in every test that needs Redis.</summary>
    public void SkipIfUnavailable()
    {
        if (UnavailableReason is not null)
        {
            Assert.Skip(UnavailableReason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class RedisContainerGroup : ICollectionFixture<RedisContainerFixture>
{
    public const string Name = "CostControls.Redis";
}
