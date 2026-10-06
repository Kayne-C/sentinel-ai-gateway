using Microsoft.Extensions.Time.Testing;
using Sentinel.Application.Abstractions;
using Sentinel.Infrastructure.CostControls;

namespace Sentinel.Infrastructure.IntegrationTests.CostControls;

/// <summary>
/// The token-budget contract, run against every implementation. Tenants are unique per test so the shared Redis
/// container needs no cleanup; the clock is fake and starts mid-month, mid-day.
/// </summary>
public abstract class TokenBudgetBehaviour
{
    protected FakeTimeProvider Time { get; } = new(new DateTimeOffset(2099, 3, 10, 12, 0, 0, TimeSpan.Zero));

    protected abstract ITokenBudget CreateBudget(BudgetOptions options);

    [Fact]
    public async Task A_reservation_charges_the_tenant_and_the_subject()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 10_000, subjectDaily: 5_000);

        var lease = await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 1_200), CancellationToken.None);

        Assert.True(lease.Granted);
        Assert.Equal(1_200, lease.ReservedTokens);
        Assert.Equal(8_800, lease.TenantRemaining);
        Assert.Equal(3_800, lease.SubjectRemaining);
        Assert.Null(lease.RetryAfter);
        Assert.Null(lease.DeniedBy);

        var status = await budget.GetStatusAsync(tenant, "alice", CancellationToken.None);
        Assert.Equal(1_200, status.TenantUsed);
        Assert.Equal(1_200, status.SubjectUsed);
    }

    [Fact]
    public async Task Parallel_reservations_never_exceed_the_tenant_limit()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 100_000, subjectDaily: 1_000_000);

        var leases = await Task.WhenAll(Enumerable.Range(0, 300).Select(i =>
            Task.Run(() => budget.ReserveAsync(new BudgetRequest(tenant, $"subject-{i % 10}", 1_000), CancellationToken.None))));

        var granted = leases.Where(l => l.Granted).ToArray();
        Assert.Equal(100, granted.Length);
        Assert.All(leases.Where(l => !l.Granted), l => Assert.Equal("tenant", l.DeniedBy));

        // Every grant saw a distinct counter value: no two reservations raced past each other.
        Assert.Equal(
            Enumerable.Range(0, 100).Select(i => (long)i * 1_000).ToHashSet(),
            granted.Select(l => l.TenantRemaining).ToHashSet());
        Assert.Equal(100_000, (await budget.GetStatusAsync(tenant, "subject-0", CancellationToken.None)).TenantUsed);
    }

    [Fact]
    public async Task Parallel_reservations_never_exceed_the_subject_limit()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 10_000_000, subjectDaily: 100_000);

        var leases = await Task.WhenAll(Enumerable.Range(0, 300).Select(_ =>
            Task.Run(() => budget.ReserveAsync(new BudgetRequest(tenant, "alice", 1_000), CancellationToken.None))));

        Assert.Equal(100, leases.Count(l => l.Granted));
        Assert.All(leases.Where(l => !l.Granted), l => Assert.Equal("subject", l.DeniedBy));
        var status = await budget.GetStatusAsync(tenant, "alice", CancellationToken.None);
        Assert.Equal(100_000, status.SubjectUsed);
        Assert.Equal(100_000, status.TenantUsed);
    }

    [Fact]
    public async Task The_subject_limit_denies_without_charging_anything()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 1_000_000, subjectDaily: 5_000);
        await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 4_000), CancellationToken.None);

        var denied = await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 2_000), CancellationToken.None);

        Assert.False(denied.Granted);
        Assert.Equal("subject", denied.DeniedBy);
        Assert.Equal(0, denied.ReservedTokens);
        Assert.Equal(996_000, denied.TenantRemaining);
        Assert.Equal(1_000, denied.SubjectRemaining);
        Assert.Equal(TimeSpan.FromHours(12), denied.RetryAfter);

        var status = await budget.GetStatusAsync(tenant, "alice", CancellationToken.None);
        Assert.Equal(4_000, status.TenantUsed);
        Assert.Equal(4_000, status.SubjectUsed);

        // Another subject of the same tenant is unaffected.
        Assert.True((await budget.ReserveAsync(new BudgetRequest(tenant, "bob", 2_000), CancellationToken.None)).Granted);
    }

    [Fact]
    public async Task The_tenant_limit_denies_until_the_month_resets()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 10_000, subjectDaily: 1_000_000);
        await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 8_000), CancellationToken.None);

        var denied = await budget.ReserveAsync(new BudgetRequest(tenant, "bob", 3_000), CancellationToken.None);

        Assert.False(denied.Granted);
        Assert.Equal("tenant", denied.DeniedBy);
        Assert.Equal(2_000, denied.TenantRemaining);
        Assert.Equal(1_000_000, denied.SubjectRemaining);
        Assert.Equal(new DateTimeOffset(2099, 4, 1, 0, 0, 0, TimeSpan.Zero) - Time.GetUtcNow(), denied.RetryAfter);
        Assert.Equal(0, (await budget.GetStatusAsync(tenant, "bob", CancellationToken.None)).SubjectUsed);
    }

    [Fact]
    public async Task An_exhausted_budget_refuses_even_a_zero_token_reservation()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 1_000_000, subjectDaily: 1_000);

        Assert.True((await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 0), CancellationToken.None)).Granted);
        Assert.True((await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 1_000), CancellationToken.None)).Granted);

        var denied = await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 0), CancellationToken.None);
        Assert.False(denied.Granted);
        Assert.Equal("subject", denied.DeniedBy);
    }

    [Fact]
    public async Task A_zero_limit_blocks_the_tenant()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 0, subjectDaily: 1_000);

        var denied = await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 1), CancellationToken.None);

        Assert.False(denied.Granted);
        Assert.Equal("tenant", denied.DeniedBy);
    }

    [Fact]
    public async Task Tenant_overrides_replace_only_the_values_they_set()
    {
        var limited = NewTenant();
        var other = NewTenant();
        var options = new BudgetOptions { DefaultTenantMonthlyTokens = 50_000, DefaultSubjectDailyTokens = 7_000 };
        options.Tenants[limited] = new TenantBudgetOptions { MonthlyTokens = 9_000 };
        var budget = CreateBudget(options);

        var limitedStatus = await budget.GetStatusAsync(limited, "alice", CancellationToken.None);
        var otherStatus = await budget.GetStatusAsync(other, "alice", CancellationToken.None);

        Assert.Equal(9_000, limitedStatus.TenantLimit);
        Assert.Equal(7_000, limitedStatus.SubjectLimit);
        Assert.Equal(50_000, otherStatus.TenantLimit);
        Assert.Equal(7_000, otherStatus.SubjectLimit);
    }

    [Fact]
    public async Task Settling_refunds_unused_tokens()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 10_000, subjectDaily: 5_000);
        var lease = await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 3_000), CancellationToken.None);

        await budget.SettleAsync(lease, 1_100, CancellationToken.None);

        var status = await budget.GetStatusAsync(tenant, "alice", CancellationToken.None);
        Assert.Equal(1_100, status.TenantUsed);
        Assert.Equal(1_100, status.SubjectUsed);
    }

    [Fact]
    public async Task Settling_charges_tokens_beyond_the_reservation()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 10_000, subjectDaily: 5_000);
        var lease = await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 1_000), CancellationToken.None);

        await budget.SettleAsync(lease, 4_500, CancellationToken.None);

        var status = await budget.GetStatusAsync(tenant, "alice", CancellationToken.None);
        Assert.Equal(4_500, status.TenantUsed);
        Assert.Equal(4_500, status.SubjectUsed);
        Assert.False((await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 600), CancellationToken.None)).Granted);
    }

    [Fact]
    public async Task Settling_never_lets_a_counter_go_below_zero()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 100_000, subjectDaily: 100_000);
        var lease = await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 1_000), CancellationToken.None);

        // A lease this budget did not issue (a copy) claiming a larger reservation than was ever charged.
        await budget.SettleAsync(lease with { ReservedTokens = 50_000 }, 0, CancellationToken.None);

        var status = await budget.GetStatusAsync(tenant, "alice", CancellationToken.None);
        Assert.Equal(0, status.TenantUsed);
        Assert.Equal(0, status.SubjectUsed);
    }

    [Fact]
    public async Task Settling_the_same_lease_twice_has_no_further_effect()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 100_000, subjectDaily: 100_000);
        var lease = await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 1_000), CancellationToken.None);

        await budget.SettleAsync(lease, 400, CancellationToken.None);
        await budget.SettleAsync(lease, 400, CancellationToken.None);

        Assert.Equal(400, (await budget.GetStatusAsync(tenant, "alice", CancellationToken.None)).SubjectUsed);
    }

    [Fact]
    public async Task Settling_a_denied_lease_changes_nothing()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 100_000, subjectDaily: 1_000);
        await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 800), CancellationToken.None);
        var denied = await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 500), CancellationToken.None);

        await budget.SettleAsync(denied, 300, CancellationToken.None);

        Assert.Equal(800, (await budget.GetStatusAsync(tenant, "alice", CancellationToken.None)).SubjectUsed);
    }

    [Fact]
    public async Task The_daily_budget_resets_at_utc_midnight_but_the_monthly_one_does_not()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 1_000_000, subjectDaily: 1_000);
        Time.SetUtcNow(new DateTimeOffset(2099, 3, 10, 23, 59, 30, TimeSpan.Zero));
        await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 1_000), CancellationToken.None);
        var denied = await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 1), CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(30), denied.RetryAfter);

        Time.Advance(TimeSpan.FromSeconds(30));

        Assert.True((await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 1_000), CancellationToken.None)).Granted);
        var status = await budget.GetStatusAsync(tenant, "alice", CancellationToken.None);
        Assert.Equal(1_000, status.SubjectUsed);
        Assert.Equal(2_000, status.TenantUsed);
    }

    [Fact]
    public async Task Both_budgets_reset_when_a_new_month_starts()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 5_000, subjectDaily: 5_000);
        Time.SetUtcNow(new DateTimeOffset(2099, 3, 31, 23, 0, 0, TimeSpan.Zero));
        await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 5_000), CancellationToken.None);
        var denied = await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 10), CancellationToken.None);
        Assert.Equal("tenant", denied.DeniedBy);
        Assert.Equal(TimeSpan.FromHours(1), denied.RetryAfter);

        Time.Advance(TimeSpan.FromHours(1));

        Assert.True((await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 5_000), CancellationToken.None)).Granted);
        var status = await budget.GetStatusAsync(tenant, "alice", CancellationToken.None);
        Assert.Equal(5_000, status.TenantUsed);
        Assert.Equal(new DateTime(2099, 5, 1, 0, 0, 0, DateTimeKind.Utc), status.TenantPeriodResetsAtUtc);
    }

    [Fact]
    public async Task A_settlement_after_midnight_corrects_the_day_that_was_charged()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 1_000_000, subjectDaily: 100_000);
        Time.SetUtcNow(new DateTimeOffset(2099, 3, 10, 23, 59, 0, TimeSpan.Zero));
        var yesterday = await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 50_000), CancellationToken.None);

        Time.Advance(TimeSpan.FromMinutes(2));
        await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 10_000), CancellationToken.None);
        await budget.SettleAsync(yesterday, 0, CancellationToken.None);

        // The refund went to the day it was charged to; today's usage is untouched (no free tokens for today).
        var status = await budget.GetStatusAsync(tenant, "alice", CancellationToken.None);
        Assert.Equal(10_000, status.SubjectUsed);
        Assert.Equal(10_000, status.TenantUsed);
    }

    [Fact]
    public async Task Status_reports_limits_usage_and_reset_times()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 2_000_000, subjectDaily: 100_000);
        await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 2_500), CancellationToken.None);
        await budget.ReserveAsync(new BudgetRequest(tenant, "bob", 500), CancellationToken.None);

        var status = await budget.GetStatusAsync(tenant, "alice", CancellationToken.None);

        Assert.Equal(tenant, status.TenantId);
        Assert.Equal(2_000_000, status.TenantLimit);
        Assert.Equal(3_000, status.TenantUsed);
        Assert.Equal(100_000, status.SubjectLimit);
        Assert.Equal(2_500, status.SubjectUsed);
        Assert.Equal(new DateTime(2099, 4, 1, 0, 0, 0, DateTimeKind.Utc), status.TenantPeriodResetsAtUtc);
        Assert.Equal(DateTimeKind.Utc, status.TenantPeriodResetsAtUtc.Kind);
        Assert.Equal(new DateTime(2099, 3, 11, 0, 0, 0, DateTimeKind.Utc), status.SubjectPeriodResetsAtUtc);
    }

    [Fact]
    public async Task Separator_characters_in_ids_cannot_merge_two_budgets()
    {
        var tenant = NewTenant();
        var budget = Budget(tenantMonthly: 1_000_000, subjectDaily: 1_000);
        await budget.ReserveAsync(new BudgetRequest(tenant + ":x", "y", 1_000), CancellationToken.None);

        foreach (var (otherTenant, otherSubject) in new[]
                 {
                     (tenant, "x:y"), (tenant, "x%3Ay"), (tenant + ":x:y", "y"), (tenant + "}:x", "y"), (tenant + ":X", "y"), (tenant + ":x", "Y"),
                 })
        {
            var status = await budget.GetStatusAsync(otherTenant, otherSubject, CancellationToken.None);
            Assert.Equal(0, status.SubjectUsed);
            Assert.True((await budget.ReserveAsync(new BudgetRequest(otherTenant, otherSubject, 1_000), CancellationToken.None)).Granted);
        }
    }

    [Fact]
    public async Task Hostile_ids_are_budgeted_like_any_other()
    {
        var budget = Budget(tenantMonthly: 1_000_000, subjectDaily: 1_000);
        var tenant = "{" + NewTenant() + "} evil:*";
        const string subject = "user@contoso.com %41 {x}";

        Assert.True((await budget.ReserveAsync(new BudgetRequest(tenant, subject, 1_000), CancellationToken.None)).Granted);
        Assert.False((await budget.ReserveAsync(new BudgetRequest(tenant, subject, 1), CancellationToken.None)).Granted);
        Assert.Equal(1_000, (await budget.GetStatusAsync(tenant, subject, CancellationToken.None)).SubjectUsed);
    }

    [Fact]
    public async Task Invalid_requests_are_rejected()
    {
        var budget = Budget(tenantMonthly: 1_000, subjectDaily: 1_000);
        var tenant = NewTenant();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => budget.ReserveAsync(new BudgetRequest(tenant, "alice", -1), CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => budget.ReserveAsync(new BudgetRequest(" ", "alice", 1), CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => budget.ReserveAsync(new BudgetRequest(tenant, "", 1), CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => budget.ReserveAsync(new BudgetRequest(tenant, "lone" + (char)0xDC00, 1), CancellationToken.None));
        var lease = await budget.ReserveAsync(new BudgetRequest(tenant, "alice", 1), CancellationToken.None);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => budget.SettleAsync(lease, -5, CancellationToken.None));
    }

    protected static string NewTenant() => "tenant-" + Guid.NewGuid().ToString("N");

    private ITokenBudget Budget(long tenantMonthly, long subjectDaily) => CreateBudget(new BudgetOptions
    {
        DefaultTenantMonthlyTokens = tenantMonthly,
        DefaultSubjectDailyTokens = subjectDaily,
    });
}
