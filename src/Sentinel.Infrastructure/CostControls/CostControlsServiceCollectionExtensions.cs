using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Infrastructure.CostControls.Budgets;
using Sentinel.Infrastructure.CostControls.Caching;
using Sentinel.Infrastructure.CostControls.Routing;
using Sentinel.Infrastructure.CostControls.Tokens;
using StackExchange.Redis;

namespace Sentinel.Infrastructure.CostControls;

/// <summary>Semantic cache, token budgets, model routing, token counting.</summary>
public static class CostControlsServiceCollectionExtensions
{
    public const string RedisConnectionStringName = "Redis";

    /// <summary>
    /// Registers the cost controls. Providers are chosen from configuration at registration time
    /// (<c>SemanticCache:Provider</c>, <c>Budgets:Provider</c>: <c>InMemory</c> | <c>Redis</c>); an
    /// <see cref="IConnectionMultiplexer"/> is registered only when one of them is <c>Redis</c> (and only if the host
    /// has not registered one already). It connects lazily, on first resolution, with <c>AbortOnConnectFail=false</c>:
    /// a Redis outage does not prevent the gateway from starting, the first resolution waits at most the connect
    /// timeout, and the connection keeps retrying in the background.
    /// </summary>
    public static IServiceCollection AddCostControls(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Read when validated or resolved, not now: the live configuration may still change before the host starts.
        bool HasRedisConnectionString() => !string.IsNullOrWhiteSpace(configuration.GetConnectionString(RedisConnectionStringName));

        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<RoutingOptions>()
            .Bind(configuration.GetSection(RoutingOptions.Section))
            .Validate(o => o.ReasoningThreshold is >= 0 and <= 1, "Routing:ReasoningThreshold must be between 0 and 1.")
            .Validate(o => o.LongPromptTokens > 0 && o.ContextHeavyTokens > 0, "Routing:LongPromptTokens and Routing:ContextHeavyTokens must be positive.")
            .ValidateOnStart();

        services.AddOptions<SemanticCacheOptions>()
            .Bind(configuration.GetSection(SemanticCacheOptions.Section))
            .Validate(o => o.SimilarityThreshold is > 0 and <= 1, "SemanticCache:SimilarityThreshold must be in (0, 1].")
            .Validate(o => o.Ttl > TimeSpan.Zero, "SemanticCache:Ttl must be positive.")
            .Validate(o => o.MaxEntriesPerScope > 0, "SemanticCache:MaxEntriesPerScope must be positive.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.IndexName) && !string.IsNullOrWhiteSpace(o.KeyPrefix), "SemanticCache:IndexName and SemanticCache:KeyPrefix are required.")
            .Validate(o => o.Provider != CostControlsProvider.Redis || HasRedisConnectionString(), $"ConnectionStrings:{RedisConnectionStringName} is required when SemanticCache:Provider is Redis.")
            .ValidateOnStart();

        services.AddOptions<BudgetOptions>()
            .Bind(configuration.GetSection(BudgetOptions.Section))
            .Validate(o => o.DefaultTenantMonthlyTokens >= 0 && o.DefaultSubjectDailyTokens >= 0, "Budgets defaults must not be negative.")
            .Validate(o => o.Tenants is null || o.Tenants.Values.All(t => t is null || ((t.MonthlyTokens ?? 0) >= 0 && (t.SubjectDailyTokens ?? 0) >= 0)), "Budgets:Tenants limits must not be negative.")
            .Validate(o => o.Provider != CostControlsProvider.Redis || HasRedisConnectionString(), $"ConnectionStrings:{RedisConnectionStringName} is required when Budgets:Provider is Redis.")
            .ValidateOnStart();

        // Bound by AddApplication; declared here as well so the router also resolves when hosted without it.
        services.AddOptions<ModelCatalogOptions>();

        services.TryAddSingleton<ITokenCounter, TiktokenTokenCounter>();
        services.TryAddSingleton<IModelRouter, ModelRouter>();

        var cacheProvider = configuration.GetSection(SemanticCacheOptions.Section).GetValue(nameof(SemanticCacheOptions.Provider), CostControlsProvider.InMemory);
        var budgetProvider = configuration.GetSection(BudgetOptions.Section).GetValue(nameof(BudgetOptions.Provider), CostControlsProvider.InMemory);

        if (cacheProvider == CostControlsProvider.Redis)
        {
            services.TryAddSingleton<RedisSemanticCache>();
            services.TryAddSingleton<ISemanticCache>(sp => sp.GetRequiredService<RedisSemanticCache>());
            services.AddHostedService<SemanticCacheIndexInitializer>();
        }
        else
        {
            services.TryAddSingleton<ISemanticCache, InMemorySemanticCache>();
        }

        if (budgetProvider == CostControlsProvider.Redis)
        {
            services.TryAddSingleton<ITokenBudget, RedisTokenBudget>();
        }
        else
        {
            services.TryAddSingleton<ITokenBudget, InMemoryTokenBudget>();
        }

        if (cacheProvider == CostControlsProvider.Redis || budgetProvider == CostControlsProvider.Redis)
        {
            services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectRedis(configuration.GetConnectionString(RedisConnectionStringName)));
        }

        return services;
    }

    private static ConnectionMultiplexer ConnectRedis(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{RedisConnectionStringName} is required when a cost-control provider is Redis.");
        }

        var options = ConfigurationOptions.Parse(connectionString);

        // Do not fail while Redis is down: return after the first attempt and keep retrying in the background.
        // Commands fail meanwhile; the cache treats that as a miss, budgets surface it to the caller (fail closed).
        options.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(options);
    }
}
