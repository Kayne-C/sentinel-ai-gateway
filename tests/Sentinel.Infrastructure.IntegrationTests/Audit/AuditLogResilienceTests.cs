using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Sentinel.Application.Abstractions;
using Sentinel.Infrastructure.Audit;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.IntegrationTests.Audit;

public sealed class AuditLogResilienceTests
{
    [Fact]
    public async Task The_infrastructure_registers_one_audit_log_shared_by_every_scope()
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddInfrastructure(new ConfigurationBuilder().Build())
            .BuildServiceProvider(validateScopes: true);
        await using var first = services.CreateAsyncScope();
        await using var second = services.CreateAsyncScope();

        // One instance per process: the per-tenant append locks only serialise writers that share them.
        var log = first.ServiceProvider.GetRequiredService<IAuditLog>();
        Assert.IsType<AuditLog>(log);
        Assert.Same(log, second.ServiceProvider.GetRequiredService<IAuditLog>());
    }

    [Fact]
    public async Task An_append_that_cannot_reach_the_database_fails_with_a_timeout_instead_of_hanging()
    {
        var time = new FakeTimeProvider();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(time)
            .AddSingleton<IDbContextFactory<SentinelDbContext>, UnreachableDatabase>()
            .AddAuditLog(new ConfigurationBuilder().Build())
            .BuildServiceProvider(validateScopes: true);
        var log = (AuditLog)services.GetRequiredService<IAuditLog>();

        var append = log.AppendAsync(AuditTestData.Event("tenant-a"), CancellationToken.None);
        time.Advance(AuditLog.AppendTimeout - TimeSpan.FromTicks(1));
        Assert.False(append.IsCompleted);

        time.Advance(TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<TimeoutException>(() => append);
        Assert.Equal(0, log.ActiveTenantLocks);
    }

    private sealed class UnreachableDatabase : IDbContextFactory<SentinelDbContext>
    {
        public SentinelDbContext CreateDbContext() => throw new NotSupportedException("The audit log only creates contexts asynchronously.");

        public async Task<SentinelDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }
}

public sealed class KeyedAsyncLockTests
{
    [Fact]
    public async Task Holders_of_one_key_take_turns_while_other_keys_proceed_and_no_gate_outlives_its_users()
    {
        var locks = new KeyedAsyncLock();
        var first = await locks.AcquireAsync("tenant-a", CancellationToken.None);
        var waiting = locks.AcquireAsync("tenant-a", CancellationToken.None);
        var other = await locks.AcquireAsync("tenant-b", CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        Assert.Equal(2, locks.ActiveKeys);

        first.Dispose();
        (await waiting).Dispose();
        other.Dispose();
        Assert.Equal(0, locks.ActiveKeys);
    }

    [Fact]
    public async Task A_waiter_that_gives_up_leaves_no_gate_behind()
    {
        var locks = new KeyedAsyncLock();
        var holder = await locks.AcquireAsync("tenant-a", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = locks.AcquireAsync("tenant-a", cancellation.Token);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        holder.Dispose();

        Assert.Equal(0, locks.ActiveKeys);
    }

    [Fact]
    public async Task Releasing_a_lease_twice_does_not_let_two_holders_in()
    {
        var locks = new KeyedAsyncLock();
        var lease = await locks.AcquireAsync("tenant-a", CancellationToken.None);
        lease.Dispose();
        lease.Dispose();

        var holder = await locks.AcquireAsync("tenant-a", CancellationToken.None);
        var waiting = locks.AcquireAsync("tenant-a", CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        holder.Dispose();
        (await waiting).Dispose();
        Assert.Equal(0, locks.ActiveKeys);
    }
}
