using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;

namespace Sentinel.Infrastructure.CostControls.Budgets;

/// <summary>
/// Process-local token budgets with the same semantics as <see cref="RedisTokenBudget"/>: one lock makes
/// check-and-charge of both counters atomic. Only correct for a single gateway instance; with several, each would
/// grant the full budget.
/// </summary>
internal sealed partial class InMemoryTokenBudget(
    IOptionsMonitor<BudgetOptions> options,
    TimeProvider timeProvider,
    ILogger<InMemoryTokenBudget> logger) : ITokenBudget
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly LeaseLedger _ledger = new();
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    public Task<BudgetLease> ReserveAsync(BudgetRequest request, CancellationToken cancellationToken)
    {
        BudgetRules.Validate(request);
        cancellationToken.ThrowIfCancellationRequested();

        var now = timeProvider.GetUtcNow();
        var month = BudgetPeriods.Month(now);
        var day = BudgetPeriods.Day(now);
        var tenantKey = BudgetKeys.Tenant(request.TenantId, month);
        var subjectKey = BudgetKeys.Subject(request.TenantId, request.SubjectId, day);
        var limits = BudgetLimits.For(options.CurrentValue, request.TenantId);

        BudgetLease lease;
        lock (_gate)
        {
            SweepExpired(now);
            var tenant = Get(tenantKey, month, now);
            var subject = Get(subjectKey, day, now);

            var deniedBy = BudgetRules.DeniedBy(tenant.Used, subject.Used, request.EstimatedTokens, limits);
            if (deniedBy is not null)
            {
                lease = BudgetRules.Denied(request, deniedBy, tenant.Used, subject.Used, limits, month, day, now);
            }
            else
            {
                tenant.Used += request.EstimatedTokens;
                subject.Used += request.EstimatedTokens;
                lease = BudgetRules.Granted(request, tenant.Used, subject.Used, limits);
            }
        }

        if (lease.Granted)
        {
            _ledger.Track(lease, month, day);
        }
        else
        {
            LogDenied(logger, lease.DeniedBy!, lease.RetryAfter!.Value.TotalSeconds);
        }

        return Task.FromResult(lease);
    }

    public Task SettleAsync(BudgetLease lease, int actualTokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfNegative(actualTokens);
        cancellationToken.ThrowIfCancellationRequested();

        // A denied lease reserved nothing and the provider was not called: there is nothing to settle.
        if (!lease.Granted)
        {
            return Task.CompletedTask;
        }

        if (!_ledger.TryBeginSettlement(lease, out var reservation))
        {
            LogDuplicateSettlement(logger);
            return Task.CompletedTask;
        }

        var delta = (long)actualTokens - lease.ReservedTokens;
        if (delta == 0)
        {
            return Task.CompletedTask;
        }

        var now = timeProvider.GetUtcNow();
        var month = reservation?.Month ?? BudgetPeriods.Month(now);
        var day = reservation?.Day ?? BudgetPeriods.Day(now);

        lock (_gate)
        {
            Apply(BudgetKeys.Tenant(lease.TenantId, month), month, now, delta);
            Apply(BudgetKeys.Subject(lease.TenantId, lease.SubjectId, day), day, now, delta);
        }

        return Task.CompletedTask;
    }

    public Task<BudgetStatus> GetStatusAsync(string tenantId, string subjectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        cancellationToken.ThrowIfCancellationRequested();

        var now = timeProvider.GetUtcNow();
        var month = BudgetPeriods.Month(now);
        var day = BudgetPeriods.Day(now);
        var tenantKey = BudgetKeys.Tenant(tenantId, month);
        var subjectKey = BudgetKeys.Subject(tenantId, subjectId, day);
        var limits = BudgetLimits.For(options.CurrentValue, tenantId);

        long tenantUsed, subjectUsed;
        lock (_gate)
        {
            tenantUsed = Peek(tenantKey, now);
            subjectUsed = Peek(subjectKey, now);
        }

        return Task.FromResult(BudgetRules.Status(tenantId, limits, tenantUsed, subjectUsed, month, day));
    }

    private Counter Get(string key, BudgetPeriod period, DateTimeOffset now)
    {
        if (!_counters.TryGetValue(key, out var counter) || counter.ExpiresAt <= now)
        {
            counter = new Counter { ExpiresAt = now + BudgetPeriods.TimeToLive(period, now) };
            _counters[key] = counter;
        }

        return counter;
    }

    private long Peek(string key, DateTimeOffset now) =>
        _counters.TryGetValue(key, out var counter) && counter.ExpiresAt > now ? counter.Used : 0;

    /// <summary>Applies a settlement delta; a counter never goes below zero (a refund cannot create credit).</summary>
    private void Apply(string key, BudgetPeriod period, DateTimeOffset now, long delta)
    {
        if (now >= period.EndsAt + BudgetPeriods.ExpiryGrace)
        {
            // The period is over and its counter gone; correcting it would change nothing.
            return;
        }

        var counter = Get(key, period, now);
        counter.Used = Math.Max(0, counter.Used + delta);
    }

    private void SweepExpired(DateTimeOffset now)
    {
        if (now < _nextSweep)
        {
            return;
        }

        foreach (var (key, counter) in _counters)
        {
            if (counter.ExpiresAt <= now)
            {
                _counters.Remove(key);
            }
        }

        _nextSweep = now + SweepInterval;
    }

    private sealed class Counter
    {
        public long Used { get; set; }

        public DateTimeOffset ExpiresAt { get; init; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Token budget denied by the {Scope} budget; retry after {RetryAfterSeconds:0} s")]
    private static partial void LogDenied(ILogger logger, string scope, double retryAfterSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A token budget lease was settled more than once; the repeated settlement was ignored")]
    private static partial void LogDuplicateSettlement(ILogger logger);
}
