using System.Runtime.CompilerServices;
using Sentinel.Application.Abstractions;

namespace Sentinel.Infrastructure.CostControls.Budgets;

/// <summary>
/// Remembers, per granted lease <i>object</i>, the periods it charged and whether it has been settled.
/// </summary>
/// <remarks>
/// Two problems this solves without changing the <see cref="BudgetLease"/> contract:
/// a settlement that arrives after midnight or month end corrects the period that was actually charged (otherwise a
/// large reservation made at 23:59 and refunded at 00:01 would hand the new day free tokens), and a second settlement
/// of the same lease is ignored (otherwise a bug calling it twice would refund twice). Keyed by reference identity —
/// the lease is a record, and value equality would conflate two identical reservations — and weakly, so entries die
/// with the lease. A lease that was not issued by this instance (deserialised, copied with <c>with</c>) falls back to
/// the current periods.
/// </remarks>
internal sealed class LeaseLedger
{
    private readonly ConditionalWeakTable<BudgetLease, Reservation> _reservations = new();

    public void Track(BudgetLease lease, BudgetPeriod month, BudgetPeriod day) =>
        _reservations.AddOrUpdate(lease, new Reservation(month, day));

    /// <summary>
    /// Claims the right to settle <paramref name="lease"/>. False if it was already settled. <paramref name="reservation"/>
    /// is null for leases this instance did not issue.
    /// </summary>
    public bool TryBeginSettlement(BudgetLease lease, out Reservation? reservation)
    {
        if (_reservations.TryGetValue(lease, out var tracked))
        {
            reservation = tracked;
            return Interlocked.Exchange(ref tracked.SettledFlag, 1) == 0;
        }

        reservation = null;
        return true;
    }

    /// <summary>The settlement failed (e.g. Redis unavailable): allow a retry.</summary>
    public static void AbortSettlement(Reservation? reservation)
    {
        if (reservation is not null)
        {
            Volatile.Write(ref reservation.SettledFlag, 0);
        }
    }

    internal sealed class Reservation(BudgetPeriod month, BudgetPeriod day)
    {
#pragma warning disable CA1051 // Interlocked needs a field.
        internal int SettledFlag;
#pragma warning restore CA1051

        public BudgetPeriod Month { get; } = month;

        public BudgetPeriod Day { get; } = day;
    }
}
