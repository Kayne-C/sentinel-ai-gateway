using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Sentinel.Application.Diagnostics;

/// <summary>One ActivitySource and one Meter for the whole gateway (registered with OpenTelemetry by the hosts).</summary>
public static class SentinelTelemetry
{
    public const string SourceName = "Sentinel";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("sentinel.request.duration", "ms", "Duration of application use cases.");

    /// <summary>Tags: <c>pii.type</c>, <c>origin</c> (caller|context|output).</summary>
    public static readonly Counter<long> PiiRedactions =
        Meter.CreateCounter<long>("sentinel.guardrails.pii_redactions", "{entity}", "PII occurrences redacted.");

    /// <summary>Tags: <c>origin</c> (user|document|tool), <c>action</c> (blocked|quarantined).</summary>
    public static readonly Counter<long> InjectionDetections =
        Meter.CreateCounter<long>("sentinel.guardrails.injection_detections", "{detection}", "Prompt-injection detections.");

    /// <summary>Tags: <c>result</c> (hit|miss|rejected).</summary>
    public static readonly Counter<long> CacheLookups =
        Meter.CreateCounter<long>("sentinel.cache.lookups", "{lookup}", "Semantic cache lookups.");

    /// <summary>Tags: <c>model.tier</c>, <c>direction</c> (input|output).</summary>
    public static readonly Counter<long> TokensConsumed =
        Meter.CreateCounter<long>("sentinel.tokens.consumed", "{token}", "Tokens billed by model providers.");

    /// <summary>Tags: <c>model.tier</c>.</summary>
    public static readonly Counter<double> EstimatedCost =
        Meter.CreateCounter<double>("sentinel.cost.estimated", "USD", "Estimated provider cost.");

    /// <summary>Tags: <c>model.tier</c>.</summary>
    public static readonly Counter<double> CostAvoided =
        Meter.CreateCounter<double>("sentinel.cost.avoided", "USD", "Estimated cost avoided by semantic cache hits.");

    /// <summary>Tags: <c>scope</c> (tenant|subject).</summary>
    public static readonly Counter<long> BudgetRejections =
        Meter.CreateCounter<long>("sentinel.budget.rejections", "{request}", "Requests refused by token budgets.");
}
