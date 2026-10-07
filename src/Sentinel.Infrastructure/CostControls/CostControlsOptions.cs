namespace Sentinel.Infrastructure.CostControls;

/// <summary>Where cost-control state lives.</summary>
public enum CostControlsProvider
{
    /// <summary>Process-local state: one gateway instance, development and tests.</summary>
    InMemory,

    /// <summary>
    /// Shared by every gateway instance (Redis 8; the semantic cache needs its built-in Query Engine). Budgets must
    /// be shared as soon as more than one instance runs, otherwise each instance grants the full budget.
    /// </summary>
    Redis,
}

/// <summary>Complexity-based model routing (see <c>ModelRouter</c>).</summary>
public sealed class RoutingOptions
{
    public const string Section = "Routing";

    /// <summary>Complexity score (0–1) at or above which the <c>reasoning</c> tier is chosen.</summary>
    public double ReasoningThreshold { get; set; } = 0.5;

    /// <summary>Prompts at least this long count as long (half the weight from half this length).</summary>
    public int LongPromptTokens { get; set; } = 600;

    /// <summary>Retrieved context at least this long counts as heavy (half the weight from half this length).</summary>
    public int ContextHeavyTokens { get; set; } = 1500;

    /// <summary>
    /// Honour a caller's requested model, but only when it names a configured tier or a configured tier's model:
    /// callers can never make the gateway call an arbitrary upstream model.
    /// </summary>
    public bool AllowRequestedModel { get; set; } = true;
}

/// <summary>Semantic answer cache, partitioned by tenant, feature namespace and model tier.</summary>
public sealed class SemanticCacheOptions
{
    public const string Section = "SemanticCache";

    public CostControlsProvider Provider { get; set; } = CostControlsProvider.InMemory;

    /// <summary>
    /// Minimum cosine similarity for a hit. High on purpose: a wrong cached answer costs more trust than a model
    /// call costs money.
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.92;

    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Upper bound per scope, so one tenant cannot grow the shared cache (or evict others) without limit.</summary>
    public int MaxEntriesPerScope { get; set; } = 5000;

    /// <summary>Redis Query Engine index name.</summary>
    public string IndexName { get; set; } = "sentinel-cache";

    /// <summary>Prefix of the cache entry hashes; the index covers exactly the keys with this prefix.</summary>
    public string KeyPrefix { get; set; } = "sentinel:cache:";
}

/// <summary>Token budgets: a monthly budget per tenant and a daily budget per subject (user or workload).</summary>
public sealed class BudgetOptions
{
    public const string Section = "Budgets";

    public CostControlsProvider Provider { get; set; } = CostControlsProvider.InMemory;

    public long DefaultTenantMonthlyTokens { get; set; } = 2_000_000;

    public long DefaultSubjectDailyTokens { get; set; } = 100_000;

    /// <summary>Per-tenant overrides keyed by tenant id; unset values fall back to the defaults.</summary>
    public Dictionary<string, TenantBudgetOptions> Tenants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TenantBudgetOptions
{
    public long? MonthlyTokens { get; set; }

    public long? SubjectDailyTokens { get; set; }
}
