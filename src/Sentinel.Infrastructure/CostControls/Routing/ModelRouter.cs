using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;

namespace Sentinel.Infrastructure.CostControls.Routing;

/// <summary>
/// Picks the cheapest tier that is likely good enough: simple prompts go to the default (fast) tier, prompts whose
/// complexity score reaches <see cref="RoutingOptions.ReasoningThreshold"/> go to the <c>reasoning</c> tier.
/// </summary>
/// <remarks>
/// A caller's requested model is an allow-list lookup, never a pass-through: it is honoured only when it names a
/// configured tier or a configured tier's model, and the route always carries the <i>configured</i> model id, so a
/// caller cannot make the gateway call an arbitrary (or more expensive) upstream deployment. Reasons explain every
/// decision; they never contain prompt text, and a rejected requested model is only echoed when it is a plain id.
/// </remarks>
internal sealed partial class ModelRouter(
    IOptionsMonitor<RoutingOptions> routingOptions,
    IOptionsMonitor<ModelCatalogOptions> catalogOptions) : IModelRouter
{
    internal const string BuiltInFastModel = "qwen2.5:0.5b";
    internal const string BuiltInReasoningModel = "qwen2.5:1.5b";

    public ModelRoute Route(RoutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = routingOptions.CurrentValue;
        var assessment = PromptComplexity.Assess(context.Prompt ?? string.Empty, context.PromptTokens, context.ContextTokens, options);
        var reasons = new List<string>(assessment.Reasons);

        var catalog = catalogOptions.CurrentValue;
        var tiers = UsableTiers(catalog);
        if (tiers.Count == 0)
        {
            tiers = BuiltInTiers();
            reasons.Add($"no model tiers configured: using built-in defaults ({ModelCatalogOptions.FastTier}={BuiltInFastModel}, {ModelCatalogOptions.ReasoningTier}={BuiltInReasoningModel}, zero prices)");
        }

        var defaultTier = ResolveDefaultTier(catalog.DefaultTier, tiers, reasons);

        if (!string.IsNullOrWhiteSpace(context.RequestedModel)
            && TryHonourRequestedModel(context.RequestedModel.Trim(), options, tiers, reasons) is { } requested)
        {
            return new ModelRoute(requested.Name, requested.Model, assessment.Score, reasons);
        }

        var threshold = options.ReasoningThreshold.ToString("0.00", CultureInfo.InvariantCulture);
        var score = assessment.Score.ToString("0.00", CultureInfo.InvariantCulture);
        if (assessment.Score >= options.ReasoningThreshold)
        {
            if (Find(tiers, ModelCatalogOptions.ReasoningTier) is { } reasoning)
            {
                reasons.Add($"complexity {score} >= {threshold}: {reasoning.Name} tier");
                return new ModelRoute(reasoning.Name, reasoning.Model, assessment.Score, reasons);
            }

            reasons.Add($"complexity {score} >= {threshold} but no {ModelCatalogOptions.ReasoningTier} tier is configured: default tier '{defaultTier.Name}'");
            return new ModelRoute(defaultTier.Name, defaultTier.Model, assessment.Score, reasons);
        }

        reasons.Add($"complexity {score} < {threshold}: default tier '{defaultTier.Name}'");
        return new ModelRoute(defaultTier.Name, defaultTier.Model, assessment.Score, reasons);
    }

    private static Tier? TryHonourRequestedModel(string requested, RoutingOptions options, List<Tier> tiers, List<string> reasons)
    {
        if (!options.AllowRequestedModel)
        {
            reasons.Add("requested model ignored: callers may not choose models (Routing:AllowRequestedModel=false)");
            return null;
        }

        if (Find(tiers, requested) is { } byTier)
        {
            reasons.Add($"requested model honoured: allow-listed tier '{byTier.Name}'");
            return byTier;
        }

        // Tiers are sorted by name, so two tiers sharing a model resolve the same way on every instance.
        foreach (var tier in tiers)
        {
            if (string.Equals(tier.Model, requested, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"requested model honoured: allow-listed as the model of tier '{tier.Name}'");
                return tier;
            }
        }

        var shown = PrintableModelId().IsMatch(requested) ? $"'{requested}'" : "(not a plain model id)";
        reasons.Add($"requested model {shown} ignored: not an allow-listed tier or model");
        return null;
    }

    private static Tier ResolveDefaultTier(string? configured, List<Tier> tiers, List<string> reasons)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Find(tiers, configured) is { } tier)
        {
            return tier;
        }

        var fallback = Find(tiers, ModelCatalogOptions.FastTier) ?? tiers[0];
        reasons.Add($"default tier '{configured}' is not configured: using '{fallback.Name}'");
        return fallback;
    }

    /// <summary>Configured tiers with a model id, sorted by name so every decision is order-independent.</summary>
    private static List<Tier> UsableTiers(ModelCatalogOptions catalog)
    {
        var tiers = new List<Tier>();
        if (catalog.Tiers is null)
        {
            return tiers;
        }

        foreach (var (name, tier) in catalog.Tiers)
        {
            if (!string.IsNullOrWhiteSpace(name) && tier is not null && !string.IsNullOrWhiteSpace(tier.Model))
            {
                tiers.Add(new Tier(name, tier.Model.Trim()));
            }
        }

        tiers.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return tiers;
    }

    private static List<Tier> BuiltInTiers() =>
    [
        new(ModelCatalogOptions.FastTier, BuiltInFastModel),
        new(ModelCatalogOptions.ReasoningTier, BuiltInReasoningModel),
    ];

    private static Tier? Find(List<Tier> tiers, string name) =>
        tiers.Find(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Only plain ids are echoed into reasons (which end up in logs and audit), never arbitrary caller text.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._:/-]{1,64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PrintableModelId();

    private sealed record Tier(string Name, string Model);
}
