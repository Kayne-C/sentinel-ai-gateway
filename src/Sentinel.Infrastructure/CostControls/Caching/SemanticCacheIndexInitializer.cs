using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sentinel.Infrastructure.CostControls.Caching;

/// <summary>
/// Creates the cache index when the gateway starts, off the startup path: the cache (and the Redis connection it
/// opens) is resolved only after the first yield, so an unreachable Redis neither delays nor fails startup. If it
/// fails here, the cache retries lazily on first use.
/// </summary>
internal sealed partial class SemanticCacheIndexInitializer(
    IServiceProvider services,
    ILogger<SemanticCacheIndexInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await services.GetRequiredService<RedisSemanticCache>().EnsureIndexAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception exception) when (RedisSemanticCache.IsRedisFailure(exception))
        {
            LogDeferred(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Semantic cache index could not be ensured at startup; it will be retried on first use")]
    private static partial void LogDeferred(ILogger logger, Exception exception);
}
