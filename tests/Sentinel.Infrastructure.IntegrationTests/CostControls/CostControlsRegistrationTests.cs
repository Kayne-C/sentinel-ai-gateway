using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sentinel.Application;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Application.Features.Usage;
using Sentinel.Domain.Identity;
using Sentinel.Infrastructure.CostControls;
using Sentinel.Infrastructure.CostControls.Budgets;
using Sentinel.Infrastructure.CostControls.Caching;
using Sentinel.Infrastructure.CostControls.Routing;
using Sentinel.Infrastructure.CostControls.Tokens;
using StackExchange.Redis;

namespace Sentinel.Infrastructure.IntegrationTests.CostControls;

public sealed class CostControlsRegistrationTests
{
    [Fact]
    public void In_memory_providers_are_the_default_and_need_no_redis()
    {
        var services = Services([]);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<InMemorySemanticCache>(provider.GetRequiredService<ISemanticCache>());
        Assert.IsType<InMemoryTokenBudget>(provider.GetRequiredService<ITokenBudget>());
        Assert.IsType<TiktokenTokenCounter>(provider.GetRequiredService<ITokenCounter>());
        Assert.IsType<ModelRouter>(provider.GetRequiredService<IModelRouter>());
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IConnectionMultiplexer));
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(SemanticCacheIndexInitializer));
    }

    [Fact]
    public void Options_are_bound_from_their_sections()
    {
        using var provider = Services(new Dictionary<string, string?>
        {
            ["Routing:ReasoningThreshold"] = "0.7",
            ["Routing:LongPromptTokens"] = "1000",
            ["Routing:AllowRequestedModel"] = "false",
            ["SemanticCache:SimilarityThreshold"] = "0.95",
            ["SemanticCache:Ttl"] = "02:00:00",
            ["SemanticCache:MaxEntriesPerScope"] = "10",
            ["Budgets:DefaultTenantMonthlyTokens"] = "123",
            ["Budgets:Tenants:contoso:SubjectDailyTokens"] = "45",
        }).BuildServiceProvider();

        var routing = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;
        var cache = provider.GetRequiredService<IOptions<SemanticCacheOptions>>().Value;
        var budgets = provider.GetRequiredService<IOptions<BudgetOptions>>().Value;

        Assert.Equal(0.7, routing.ReasoningThreshold);
        Assert.Equal(1000, routing.LongPromptTokens);
        Assert.False(routing.AllowRequestedModel);
        Assert.Equal(0.95, cache.SimilarityThreshold);
        Assert.Equal(TimeSpan.FromHours(2), cache.Ttl);
        Assert.Equal(10, cache.MaxEntriesPerScope);
        Assert.Equal(123, budgets.DefaultTenantMonthlyTokens);
        Assert.Equal(45, budgets.Tenants["contoso"].SubjectDailyTokens);
        Assert.Null(budgets.Tenants["contoso"].MonthlyTokens);
        Assert.True(budgets.Tenants.ContainsKey("CONTOSO"), "tenant overrides are looked up case-insensitively");
    }

    [Theory]
    [InlineData("SemanticCache:Provider")]
    [InlineData("Budgets:Provider")]
    public async Task A_redis_provider_without_a_connection_string_fails_at_startup(string providerKey)
    {
        using var host = Host([new(providerKey, "Redis")]);

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(CancellationToken.None));

        Assert.Contains("ConnectionStrings:Redis is required", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Routing:ReasoningThreshold", "1.5")]
    [InlineData("Routing:ReasoningThreshold", "-0.1")]
    [InlineData("Routing:LongPromptTokens", "0")]
    [InlineData("Routing:ContextHeavyTokens", "-1")]
    [InlineData("SemanticCache:SimilarityThreshold", "0")]
    [InlineData("SemanticCache:SimilarityThreshold", "1.01")]
    [InlineData("SemanticCache:Ttl", "00:00:00")]
    [InlineData("SemanticCache:MaxEntriesPerScope", "0")]
    [InlineData("SemanticCache:IndexName", " ")]
    [InlineData("Budgets:DefaultSubjectDailyTokens", "-1")]
    [InlineData("Budgets:Tenants:contoso:MonthlyTokens", "-5")]
    public async Task Invalid_settings_fail_at_startup(string key, string value)
    {
        using var host = Host([new(key, value)]);

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Default_settings_start()
    {
        using var host = Host([]);

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_unreachable_redis_neither_blocks_resolution_nor_breaks_the_cache_but_budgets_fail_closed()
    {
        // Port 1 refuses connections immediately; short timeouts keep the test fast.
        using var provider = Services(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = "127.0.0.1:1,connectTimeout=200,asyncTimeout=500,syncTimeout=500",
            ["SemanticCache:Provider"] = "Redis",
            ["Budgets:Provider"] = "Redis",
        }).BuildServiceProvider();

        var multiplexer = provider.GetRequiredService<IConnectionMultiplexer>();
        Assert.False(multiplexer.IsConnected);

        var cache = provider.GetRequiredService<ISemanticCache>();
        Assert.IsType<RedisSemanticCache>(cache);
        var embedding = TestVectors.Random(1);
        var scope = new SemanticCacheScope("contoso", "ask", "fast");
        await cache.StoreAsync(scope, embedding, new CachedAnswer("q", "a", [], "m", 1, 1, new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc)), CancellationToken.None);
        Assert.Null(await cache.FindAsync(scope, embedding, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => cache.InvalidateDocumentAsync("contoso", Guid.NewGuid(), CancellationToken.None));

        var budget = provider.GetRequiredService<ITokenBudget>();
        Assert.IsType<RedisTokenBudget>(budget);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => budget.ReserveAsync(new BudgetRequest("contoso", "alice", 10), CancellationToken.None));
        Assert.True(RedisSemanticCache.IsRedisFailure(failure), failure.GetType().FullName);
    }

    [Fact]
    public void A_host_supplied_connection_is_reused()
    {
        var services = new ServiceCollection();
        using var existing = ConnectionMultiplexer.Connect("127.0.0.1:1,abortConnect=false,connectTimeout=200");
        services.AddSingleton<IConnectionMultiplexer>(existing);
        services.AddLogging();
        services.AddCostControls(Configuration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = "127.0.0.1:2",
            ["Budgets:Provider"] = "Redis",
        }));

        using var provider = services.BuildServiceProvider();

        Assert.Same(existing, provider.GetRequiredService<IConnectionMultiplexer>());
        Assert.Single(services, d => d.ServiceType == typeof(IConnectionMultiplexer));
    }

    [Fact]
    public async Task The_usage_query_reports_the_callers_budget_through_the_application_pipeline()
    {
        var configuration = Configuration(new Dictionary<string, string?> { ["Budgets:DefaultSubjectDailyTokens"] = "5000" });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication(configuration);
        services.AddCostControls(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await provider.GetRequiredService<ITokenBudget>().ReserveAsync(new BudgetRequest("tenant-1", "oid-1", 1_200), CancellationToken.None);
        await provider.GetRequiredService<ITokenBudget>().ReserveAsync(new BudgetRequest("tenant-1", "oid-2", 300), CancellationToken.None);

        await using var scope = provider.CreateAsyncScope();
        var caller = new CallerIdentity("tenant-1", "oid-1", null, [], [SentinelRoles.User], CallerKind.User);
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new GetUsageQuery(caller), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1_200, result.Value.SubjectUsed);
        Assert.Equal(5_000, result.Value.SubjectLimit);
        Assert.Equal(1_500, result.Value.TenantUsed);
    }

    private static IHost Host(KeyValuePair<string, string?>[] settings)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        builder.Services.AddCostControls(builder.Configuration);
        return builder.Build();
    }

    private static ServiceCollection Services(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddCostControls(Configuration(settings));
        return services;
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}
