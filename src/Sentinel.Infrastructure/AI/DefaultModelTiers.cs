using Sentinel.Application.Common;

namespace Sentinel.Infrastructure.AI;

/// <summary>
/// Tiers used when <c>Models:Tiers</c> is not configured: the two local Ollama models, unpriced. Applied as a
/// post-configure step so the router, the price table and the chat clients all see the same catalogue.
/// </summary>
internal static class DefaultModelTiers
{
    public const string FastModel = "qwen2.5:0.5b";
    public const string ReasoningModel = "qwen2.5:1.5b";

    /// <summary>A fresh catalogue with the default tiers, for callers that must not mutate a shared options instance.</summary>
    public static ModelCatalogOptions Create(string defaultTier)
    {
        var options = new ModelCatalogOptions { DefaultTier = defaultTier };
        ApplyWhenEmpty(options);
        return options;
    }

    public static void ApplyWhenEmpty(ModelCatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Tiers.Count > 0)
        {
            return;
        }

        options.Tiers[ModelCatalogOptions.FastTier] = new ModelTierOptions { Model = FastModel, MaxOutputTokens = 512, ContextWindowTokens = 8192 };
        options.Tiers[ModelCatalogOptions.ReasoningTier] = new ModelTierOptions { Model = ReasoningModel, MaxOutputTokens = 1024, ContextWindowTokens = 8192 };
        if (!options.Tiers.ContainsKey(options.DefaultTier))
        {
            options.DefaultTier = ModelCatalogOptions.FastTier;
        }
    }
}
