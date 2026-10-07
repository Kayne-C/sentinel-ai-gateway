using Sentinel.Application.Abstractions;
using Sentinel.Application.Diagnostics;

namespace Sentinel.Infrastructure.CostControls.Budgets;

internal readonly record struct BudgetLimits(long TenantMonthly, long SubjectDaily)
{
    public static BudgetLimits For(BudgetOptions options, string tenantId)
    {
        var tenant = options.Tenants is not null && options.Tenants.TryGetValue(tenantId, out var overrides) ? overrides : null;
        return new BudgetLimits(
            Math.Max(0, tenant?.MonthlyTokens ?? options.DefaultTenantMonthlyTokens),
            Math.Max(0, tenant?.SubjectDailyTokens ?? options.DefaultSubjectDailyTokens));
    }
}

/// <summary>
/// The reservation rule, shared by both implementations (the Redis Lua script mirrors it line by line): a
/// reservation is granted only if it fits both budgets, and an exhausted budget refuses even a zero-token request,
/// so "reserve nothing, settle the real usage later" cannot be used to spend past the limit. The tenant is checked
/// first: its period never ends before the subject's, so its reset is the honest Retry-After when both are exhausted.
/// </summary>
internal static class BudgetRules
{
    public const string DeniedByTenant = "tenant";
    public const string DeniedBySubject = "subject";

    public static string? DeniedBy(long tenantUsed, long subjectUsed, long tokens, BudgetLimits limits)
    {
        if (tenantUsed >= limits.TenantMonthly || tenantUsed + tokens > limits.TenantMonthly)
        {
            return DeniedByTenant;
        }

        if (subjectUsed >= limits.SubjectDaily || subjectUsed + tokens > limits.SubjectDaily)
        {
            return DeniedBySubject;
        }

        return null;
    }

    public static void Validate(BudgetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectId, nameof(request));
        ArgumentOutOfRangeException.ThrowIfNegative(request.EstimatedTokens, nameof(request));
    }

    public static BudgetLease Granted(BudgetRequest request, long tenantUsed, long subjectUsed, BudgetLimits limits) =>
        new(true, request.TenantId, request.SubjectId, request.EstimatedTokens,
            Remaining(limits.TenantMonthly, tenantUsed), Remaining(limits.SubjectDaily, subjectUsed), null, null);

    public static BudgetLease Denied(
        BudgetRequest request, string deniedBy, long tenantUsed, long subjectUsed, BudgetLimits limits,
        BudgetPeriod month, BudgetPeriod day, DateTimeOffset now)
    {
        var resetsAt = deniedBy == DeniedByTenant ? month.EndsAt : day.EndsAt;
        SentinelTelemetry.BudgetRejections.Add(1, new KeyValuePair<string, object?>("scope", deniedBy));
        return BudgetLease.Denied(
            request, deniedBy, Remaining(limits.TenantMonthly, tenantUsed), Remaining(limits.SubjectDaily, subjectUsed), resetsAt - now);
    }

    public static BudgetStatus Status(
        string tenantId, BudgetLimits limits, long tenantUsed, long subjectUsed, BudgetPeriod month, BudgetPeriod day) =>
        new(tenantId, limits.TenantMonthly, tenantUsed, month.EndsAt.UtcDateTime, limits.SubjectDaily, subjectUsed, day.EndsAt.UtcDateTime);

    private static long Remaining(long limit, long used) => Math.Max(0, limit - used);
}
