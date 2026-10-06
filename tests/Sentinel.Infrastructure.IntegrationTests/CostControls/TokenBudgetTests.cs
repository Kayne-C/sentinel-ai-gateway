using Microsoft.Extensions.Logging.Abstractions;
using Sentinel.Application.Abstractions;
using Sentinel.Infrastructure.CostControls;
using Sentinel.Infrastructure.CostControls.Budgets;
using StackExchange.Redis;

namespace Sentinel.Infrastructure.IntegrationTests.CostControls;

public sealed class InMemoryTokenBudgetTests : TokenBudgetBehaviour
{
    protected override ITokenBudget CreateBudget(BudgetOptions options) =>
        new InMemoryTokenBudget(new TestOptionsMonitor<BudgetOptions>(options), Time, NullLogger<InMemoryTokenBudget>.Instance);
}

[Collection(RedisContainerGroup.Name)]
public sealed class RedisTokenBudgetTests(RedisContainerFixture redis) : TokenBudgetBehaviour
{
    [Fact]
    public async Task Counters_use_the_documented_keys_and_expire_after_their_period()
    {
        var tenant = NewTenant();
        var budget = CreateBudget(new BudgetOptions());

        await budget.ReserveAsync(new BudgetRequest(tenant, "alice:1", 700), CancellationToken.None);

        var database = redis.Connection.GetDatabase();
        RedisKey monthKey = $"budget:{{{tenant}}}:m:209903";
        RedisKey dayKey = $"budget:{{{tenant}}}:alice%3A1:d:20990310";
        Assert.Equal(700, (long)await database.StringGetAsync(monthKey));
        Assert.Equal(700, (long)await database.StringGetAsync(dayKey));

        // TTLs follow the injected clock: period end + 1 h grace, counted from "now".
        var monthTtl = await database.KeyTimeToLiveAsync(monthKey);
        var dayTtl = await database.KeyTimeToLiveAsync(dayKey);
        var expectedMonth = new DateTimeOffset(2099, 4, 1, 1, 0, 0, TimeSpan.Zero) - Time.GetUtcNow();
        var expectedDay = new DateTimeOffset(2099, 3, 11, 1, 0, 0, TimeSpan.Zero) - Time.GetUtcNow();
        Assert.InRange(monthTtl!.Value, expectedMonth - TimeSpan.FromMinutes(1), expectedMonth);
        Assert.InRange(dayTtl!.Value, expectedDay - TimeSpan.FromMinutes(1), expectedDay);
    }

    [Fact]
    public void Both_counters_of_a_reservation_hash_to_the_same_cluster_slot()
    {
        redis.SkipIfUnavailable();
        foreach (var tenant in new[] { NewTenant(), "{evil} tenant", "a}b{c", "contoso.com" })
        {
            var month = BudgetKeys.Tenant(tenant, BudgetPeriods.Month(Time.GetUtcNow()));
            var day = BudgetKeys.Subject(tenant, "bob{1}", BudgetPeriods.Day(Time.GetUtcNow()));

            Assert.Equal(redis.Connection.HashSlot(month), redis.Connection.HashSlot(day));
        }
    }

    protected override ITokenBudget CreateBudget(BudgetOptions options)
    {
        redis.SkipIfUnavailable();
        return new RedisTokenBudget(redis.Connection, new TestOptionsMonitor<BudgetOptions>(options), Time, NullLogger<RedisTokenBudget>.Instance);
    }
}
