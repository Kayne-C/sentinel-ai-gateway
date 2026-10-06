using System.Diagnostics;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Application.Diagnostics;
using Sentinel.Domain.Common;
using Sentinel.Guardrails.Pii;

namespace Sentinel.Application.Features.Ask;

/// <summary>Bookkeeping shared by the RAG handler and the proxy, so both bill, price and audit model calls alike.</summary>
internal static class ModelCallAccounting
{
    public const string BudgetUnavailable = "Budget.Unavailable";
    public const string RequestCancelled = "Request.Cancelled";
    public const string InternalError = "Internal.Error";

    /// <summary>
    /// The routed tier's settings, else the default tier's, else a zero-priced stand-in. Unlike
    /// <see cref="ModelCatalogOptions.GetTier"/> this never throws: a missing price list must not turn an answered
    /// request into an exception after the provider has already been paid.
    /// </summary>
    public static ModelTierOptions ResolveTier(ModelCatalogOptions catalog, ModelRoute route)
    {
        if (catalog.Tiers.TryGetValue(route.Tier, out var tier) || catalog.Tiers.TryGetValue(catalog.DefaultTier, out tier))
        {
            return tier;
        }

        return new ModelTierOptions { Model = route.Model };
    }

    public static string ModelFor(ModelRoute route, ModelTierOptions tier) =>
        string.IsNullOrWhiteSpace(route.Model) ? tier.Model : route.Model;

    public static int? ToTokenCount(long? value) => value is >= 0 ? (int)Math.Min(value.Value, int.MaxValue) : null;

    /// <summary>Audit/response shape: PII type name → occurrences, merged across the given sources.</summary>
    public static Dictionary<string, int> PiiTypeCounts(params ReadOnlySpan<IReadOnlyDictionary<PiiType, int>> sources)
    {
        var merged = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var (type, count) in source)
            {
                var name = type.ToString();
                merged[name] = merged.GetValueOrDefault(name) + count;
            }
        }

        return merged;
    }

    public static string PromptDigest(string redactedPrompt) => HashChain.Sha256Hex(redactedPrompt);

    public static string? CurrentTraceId() => Activity.Current?.TraceId.ToHexString();

    public static void RecordUsage(string tier, int promptTokens, int completionTokens, decimal cost)
    {
        var tierTag = new KeyValuePair<string, object?>("model.tier", tier);
        SentinelTelemetry.TokensConsumed.Add(promptTokens, tierTag, new KeyValuePair<string, object?>("direction", "input"));
        SentinelTelemetry.TokensConsumed.Add(completionTokens, tierTag, new KeyValuePair<string, object?>("direction", "output"));
        SentinelTelemetry.EstimatedCost.Add((double)cost, tierTag);
    }

    public static void RecordBudgetRejection(BudgetLease lease) =>
        SentinelTelemetry.BudgetRejections.Add(1, new KeyValuePair<string, object?>("scope", lease.DeniedBy ?? "unknown"));

    public static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
}
