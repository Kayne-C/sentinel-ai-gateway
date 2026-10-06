namespace Sentinel.Application.Common;

/// <summary>Model tiers the router can choose from, with list prices used for cost accounting.</summary>
public sealed class ModelCatalogOptions
{
    public const string Section = "Models";
    public const string FastTier = "fast";
    public const string ReasoningTier = "reasoning";

    public string DefaultTier { get; set; } = FastTier;

    public Dictionary<string, ModelTierOptions> Tiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ModelTierOptions GetTier(string tier) =>
        Tiers.TryGetValue(tier, out var options) ? options : Tiers[DefaultTier];
}

public sealed class ModelTierOptions
{
    /// <summary>Provider model id (e.g. <c>gpt-4.1-mini</c> deployment, <c>qwen2.5:1.5b</c> on Ollama).</summary>
    public string Model { get; set; } = string.Empty;

    public decimal InputPricePer1MTokens { get; set; }

    public decimal OutputPricePer1MTokens { get; set; }

    public int MaxOutputTokens { get; set; } = 512;

    public int ContextWindowTokens { get; set; } = 8192;

    public decimal EstimateCost(int promptTokens, int completionTokens) =>
        (promptTokens * InputPricePer1MTokens + completionTokens * OutputPricePer1MTokens) / 1_000_000m;
}

public sealed class RagOptions
{
    public const string Section = "Rag";

    public int TopK { get; set; } = 6;

    /// <summary>Chunks below this cosine similarity are not worth sending to the model.</summary>
    public double MinSimilarity { get; set; } = 0.25;

    /// <summary>Upper bound for retrieved context sent to the model.</summary>
    public int MaxContextTokens { get; set; } = 2500;

    public int MaxQuestionLength { get; set; } = 4000;

    public bool SemanticCacheEnabled { get; set; } = true;
}

public sealed class AuditPolicyOptions
{
    public const string Section = "Audit";

    /// <summary>Store the redacted prompt text (otherwise only its SHA-256 digest is kept).</summary>
    public bool StoreRedactedPrompts { get; set; }
}
