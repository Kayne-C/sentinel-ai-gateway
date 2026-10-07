using System.Globalization;

namespace Sentinel.Infrastructure.CostControls.Budgets;

/// <summary>A budget period: its id (part of the counter key) and the instant the next period starts.</summary>
internal readonly record struct BudgetPeriod(string Id, DateTimeOffset EndsAt);

/// <summary>
/// Calendar periods in UTC, from the injected clock: the tenant budget is monthly, the subject budget daily. UTC on
/// purpose, so every gateway instance (and every time zone the tenant operates in) agrees on when a period resets.
/// </summary>
internal static class BudgetPeriods
{
    /// <summary>
    /// How long a counter outlives its period. Instances whose clocks lag slightly still find the counter of the
    /// period they believe is current, and a settlement arriving just after the reset can still correct it.
    /// </summary>
    public static readonly TimeSpan ExpiryGrace = TimeSpan.FromHours(1);

    public static BudgetPeriod Month(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        var start = new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return new BudgetPeriod(start.ToString("yyyyMM", CultureInfo.InvariantCulture), new DateTimeOffset(start.AddMonths(1)));
    }

    public static BudgetPeriod Day(DateTimeOffset now)
    {
        var start = now.UtcDateTime.Date;
        return new BudgetPeriod(start.ToString("yyyyMMdd", CultureInfo.InvariantCulture), new DateTimeOffset(start.AddDays(1)));
    }

    /// <summary>Time until the counter of <paramref name="period"/> may disappear (never zero or negative).</summary>
    public static TimeSpan TimeToLive(BudgetPeriod period, DateTimeOffset now)
    {
        var ttl = period.EndsAt + ExpiryGrace - now;
        return ttl > TimeSpan.FromSeconds(1) ? ttl : TimeSpan.FromSeconds(1);
    }
}
