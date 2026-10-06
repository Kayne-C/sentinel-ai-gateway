using Sentinel.Application.Abstractions;
using Sentinel.Domain.Audit;

namespace Sentinel.Infrastructure.IntegrationTests.Audit;

/// <summary>Behaviour every store must show identically, run against SQLite and SQL Server.</summary>
internal static class AuditLogScenarios
{
    private const string Alice = "alice-oid";
    private const string Bob = "bob-oid";

    /// <summary>
    /// Ten entries one minute apart, alternating between two subjects; entries 4 and 8 Blocked, 6 Throttled. Another
    /// tenant holds a Blocked entry of the same subject inside the same time window, which must never show up.
    /// </summary>
    public static async Task ListingFiltersAndPagesNewestFirstAsync(IAuditLog log)
    {
        var tenant = AuditTestData.NewTenant();
        var otherTenant = AuditTestData.NewTenant();
        var appended = new List<AuditEntry>();
        for (var i = 0; i < 10; i++)
        {
            var outcome = i switch
            {
                3 or 7 => AuditOutcome.Blocked,
                5 => AuditOutcome.Throttled,
                _ => AuditOutcome.Allowed,
            };
            var auditEvent = AuditTestData.Event(tenant, i, i % 2 == 0 ? Alice : Bob, outcome, AuditTestData.BaseTime.AddMinutes(i));
            appended.Add(await log.AppendAsync(auditEvent, CancellationToken.None));
        }

        await log.AppendAsync(
            AuditTestData.Event(otherTenant, 0, Bob, AuditOutcome.Blocked, AuditTestData.BaseTime.AddMinutes(3)), CancellationToken.None);

        var all = await log.ListAsync(Query(tenant), CancellationToken.None);
        Assert.Equal(AuditTestData.Range(10, 1), AuditTestData.Sequences(all));
        Assert.Equal(10, all.TotalCount);
        Assert.False(all.HasNextPage);
        Assert.All(all.Items, e => Assert.Equal(tenant, e.Event.TenantId));
        Assert.Equivalent(appended[^1], all.Items[0], strict: true);
        Assert.Equivalent(appended[0], all.Items[^1], strict: true);

        var second = await log.ListAsync(Query(tenant) with { Page = 2, PageSize = 3 }, CancellationToken.None);
        Assert.Equal(AuditTestData.Range(7, 5), AuditTestData.Sequences(second));
        Assert.Equal(10, second.TotalCount);
        Assert.True(second.HasNextPage);

        var last = await log.ListAsync(Query(tenant) with { Page = 4, PageSize = 3 }, CancellationToken.None);
        Assert.Equal(AuditTestData.Range(1, 1), AuditTestData.Sequences(last));
        Assert.False(last.HasNextPage);

        var beyond = await log.ListAsync(Query(tenant) with { Page = 9, PageSize = 3 }, CancellationToken.None);
        Assert.Empty(beyond.Items);
        Assert.Equal(10, beyond.TotalCount);

        // Both bounds are inclusive.
        var window = await log.ListAsync(
            Query(tenant) with { FromUtc = AuditTestData.BaseTime.AddMinutes(2), ToUtc = AuditTestData.BaseTime.AddMinutes(5) },
            CancellationToken.None);
        Assert.Equal(AuditTestData.Range(6, 3), AuditTestData.Sequences(window));
        Assert.Equal(4, window.TotalCount);

        var bob = await log.ListAsync(Query(tenant) with { SubjectId = Bob }, CancellationToken.None);
        Assert.Equal([10L, 8, 6, 4, 2], AuditTestData.Sequences(bob));

        var blocked = await log.ListAsync(Query(tenant) with { Outcome = AuditOutcome.Blocked }, CancellationToken.None);
        Assert.Equal([8L, 4], AuditTestData.Sequences(blocked));

        var bobThrottledEarly = await log.ListAsync(
            Query(tenant) with { SubjectId = Bob, Outcome = AuditOutcome.Throttled, ToUtc = AuditTestData.BaseTime.AddMinutes(5) },
            CancellationToken.None);
        Assert.Equal([6L], AuditTestData.Sequences(bobThrottledEarly));

        var aliceBlocked = await log.ListAsync(Query(tenant) with { SubjectId = Alice, Outcome = AuditOutcome.Blocked }, CancellationToken.None);
        Assert.Empty(aliceBlocked.Items);
        Assert.Equal(0, aliceBlocked.TotalCount);
    }

    private static AuditQuery Query(string tenantId) => new(tenantId, null, null, null, null, 1, 50);
}
