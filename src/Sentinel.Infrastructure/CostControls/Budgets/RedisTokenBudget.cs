using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using StackExchange.Redis;

namespace Sentinel.Infrastructure.CostControls.Budgets;

/// <summary>
/// Token budgets shared by all gateway instances. The reservation is one Lua script — read both counters, decide,
/// charge both — which Redis runs atomically, so concurrent requests on any number of instances can never overspend
/// and a refusal never charges anything.
/// </summary>
/// <remarks>
/// <para>Counters expire a grace period after their period ends. The expiry is set as a TTL computed from the injected
/// clock (PEXPIRE) rather than as an absolute PEXPIREAT on the Redis clock: the gateway's clock already decides which
/// period a counter belongs to, and taking the expiry from the same clock means a Redis server whose clock runs ahead
/// cannot drop a counter (and with it the tenant's usage) before its period is over.</para>
/// <para>When Redis is unreachable the calls throw: budgets are a spending control, so callers should fail closed.</para>
/// </remarks>
internal sealed partial class RedisTokenBudget(
    IConnectionMultiplexer redis,
    IOptionsMonitor<BudgetOptions> options,
    TimeProvider timeProvider,
    ILogger<RedisTokenBudget> logger) : ITokenBudget
{
    /// <summary>
    /// KEYS: tenant month counter, subject day counter. ARGV: tokens, tenant limit, subject limit, tenant TTL ms,
    /// subject TTL ms. Returns { granted (0|1), deniedBy, tenantUsed, subjectUsed }. Mirrors <see cref="BudgetRules.DeniedBy"/>.
    /// </summary>
    internal const string ReserveScript = """
        local tokens = tonumber(ARGV[1])
        local tenantLimit = tonumber(ARGV[2])
        local subjectLimit = tonumber(ARGV[3])
        local tenantUsed = tonumber(redis.call('GET', KEYS[1]) or '0')
        local subjectUsed = tonumber(redis.call('GET', KEYS[2]) or '0')
        if tenantUsed >= tenantLimit or tenantUsed + tokens > tenantLimit then
          return {0, 'tenant', tenantUsed, subjectUsed}
        end
        if subjectUsed >= subjectLimit or subjectUsed + tokens > subjectLimit then
          return {0, 'subject', tenantUsed, subjectUsed}
        end
        tenantUsed = redis.call('INCRBY', KEYS[1], ARGV[1])
        subjectUsed = redis.call('INCRBY', KEYS[2], ARGV[1])
        redis.call('PEXPIRE', KEYS[1], ARGV[4])
        redis.call('PEXPIRE', KEYS[2], ARGV[5])
        return {1, '', tenantUsed, subjectUsed}
        """;

    /// <summary>
    /// KEYS: tenant month counter, subject day counter. ARGV: delta (may be negative), tenant TTL ms, subject TTL ms.
    /// Applies the delta to both counters, flooring at zero so a refund can never create credit.
    /// </summary>
    internal const string SettleScript = """
        local function apply(key, ttl)
          local updated = redis.call('INCRBY', key, ARGV[1])
          if updated < 0 then
            redis.call('SET', key, '0')
            updated = 0
          end
          redis.call('PEXPIRE', key, ttl)
          return updated
        end
        return {apply(KEYS[1], ARGV[2]), apply(KEYS[2], ARGV[3])}
        """;

    private readonly IDatabase _database = redis.GetDatabase();
    private readonly LeaseLedger _ledger = new();

    public async Task<BudgetLease> ReserveAsync(BudgetRequest request, CancellationToken cancellationToken)
    {
        BudgetRules.Validate(request);
        cancellationToken.ThrowIfCancellationRequested();

        var now = timeProvider.GetUtcNow();
        var month = BudgetPeriods.Month(now);
        var day = BudgetPeriods.Day(now);
        var limits = BudgetLimits.For(options.CurrentValue, request.TenantId);

        var result = (RedisResult[])(await _database.ScriptEvaluateAsync(
            ReserveScript,
            [BudgetKeys.Tenant(request.TenantId, month), BudgetKeys.Subject(request.TenantId, request.SubjectId, day)],
            [
                request.EstimatedTokens,
                limits.TenantMonthly,
                limits.SubjectDaily,
                Milliseconds(BudgetPeriods.TimeToLive(month, now)),
                Milliseconds(BudgetPeriods.TimeToLive(day, now)),
            ]))!;

        var granted = (long)result[0] == 1;
        var tenantUsed = (long)result[2];
        var subjectUsed = (long)result[3];

        if (granted)
        {
            var lease = BudgetRules.Granted(request, tenantUsed, subjectUsed, limits);
            _ledger.Track(lease, month, day);
            return lease;
        }

        var deniedBy = (string?)result[1] == BudgetRules.DeniedBySubject ? BudgetRules.DeniedBySubject : BudgetRules.DeniedByTenant;
        var denied = BudgetRules.Denied(request, deniedBy, tenantUsed, subjectUsed, limits, month, day, now);
        LogDenied(logger, deniedBy, denied.RetryAfter!.Value.TotalSeconds);
        return denied;
    }

    public async Task SettleAsync(BudgetLease lease, int actualTokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfNegative(actualTokens);
        cancellationToken.ThrowIfCancellationRequested();

        // A denied lease reserved nothing and the provider was not called: there is nothing to settle.
        if (!lease.Granted)
        {
            return;
        }

        if (!_ledger.TryBeginSettlement(lease, out var reservation))
        {
            LogDuplicateSettlement(logger);
            return;
        }

        var delta = (long)actualTokens - lease.ReservedTokens;
        if (delta == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var month = reservation?.Month ?? BudgetPeriods.Month(now);
        var day = reservation?.Day ?? BudgetPeriods.Day(now);
        try
        {
            await _database.ScriptEvaluateAsync(
                SettleScript,
                [BudgetKeys.Tenant(lease.TenantId, month), BudgetKeys.Subject(lease.TenantId, lease.SubjectId, day)],
                [delta, Milliseconds(BudgetPeriods.TimeToLive(month, now)), Milliseconds(BudgetPeriods.TimeToLive(day, now))]);
        }
        catch
        {
            LeaseLedger.AbortSettlement(reservation);
            throw;
        }
    }

    public async Task<BudgetStatus> GetStatusAsync(string tenantId, string subjectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        cancellationToken.ThrowIfCancellationRequested();

        var now = timeProvider.GetUtcNow();
        var month = BudgetPeriods.Month(now);
        var day = BudgetPeriods.Day(now);
        var limits = BudgetLimits.For(options.CurrentValue, tenantId);

        // One MGET: both keys share the tenant hash tag, hence one cluster slot.
        var values = await _database.StringGetAsync(
            [BudgetKeys.Tenant(tenantId, month), BudgetKeys.Subject(tenantId, subjectId, day)]);

        return BudgetRules.Status(tenantId, limits, Used(values[0]), Used(values[1]), month, day);
    }

    private static long Used(RedisValue value) => value.TryParse(out long used) ? Math.Max(0, used) : 0;

    private static long Milliseconds(TimeSpan ttl) => (long)Math.Ceiling(ttl.TotalMilliseconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Token budget denied by the {Scope} budget; retry after {RetryAfterSeconds:0} s")]
    private static partial void LogDenied(ILogger logger, string scope, double retryAfterSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A token budget lease was settled more than once; the repeated settlement was ignored")]
    private static partial void LogDuplicateSettlement(ILogger logger);
}
