using System.Text.Json;
using System.Transactions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Infrastructure.Audit;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.IntegrationTests.Audit;

/// <summary>The audit log on SQL Server 2025 with <c>AuditEntries</c> as an append-only ledger table.</summary>
[Collection(AuditSqlServerCollectionDefinition.Name)]
public sealed class SqlServerAuditLogTests(AuditSqlServerFixture fixture)
{
    /// <summary>"Updates are not allowed for the append only Ledger table" (raised for UPDATE and DELETE).</summary>
    private const int AppendOnlyLedgerViolation = 37359;

    /// <summary>"Truncate failed ... not a supported operation on system-versioned tables".</summary>
    private const int TruncateNotSupported = 13545;

    [Fact]
    public async Task Concurrent_appends_from_separate_scopes_form_one_gapless_verifiable_chain()
    {
        fixture.SkipIfUnavailable();
        var tenant = AuditTestData.NewTenant();

        var entries = await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Task.Run(async () =>
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            var log = scope.ServiceProvider.GetRequiredService<IAuditLog>();
            return await log.AppendAsync(AuditTestData.Event(tenant, i), CancellationToken.None);
        })));

        Assert.Equal(AuditTestData.Range(1, 60), entries.Select(e => e.Sequence).Order());
        Assert.Equal(AuditTestData.Range(1, 60), await fixture.SequencesAsync(tenant));
        var verification = await fixture.AuditLog.VerifyAsync(tenant, CancellationToken.None);
        Assert.True(verification.IsIntact, verification.Reason);
        Assert.Equal(60, verification.EntriesChecked);
        Assert.Equal(0, ((AuditLog)fixture.AuditLog).ActiveTenantLocks);
    }

    [Fact]
    public async Task Writers_in_different_processes_are_kept_gapless_by_the_unique_sequence_index()
    {
        fixture.SkipIfUnavailable();
        var tenant = AuditTestData.NewTenant();

        // Two service providers: two pooled factories and two independent sets of in-process locks, i.e. two gateway
        // instances racing for the same chain head.
        await using var first = fixture.CreateServices();
        await using var second = fixture.CreateServices();
        IAuditLog[] instances = [first.GetRequiredService<IAuditLog>(), second.GetRequiredService<IAuditLog>()];

        var entries = await Task.WhenAll(Enumerable.Range(0, 60).Select(i =>
            Task.Run(() => instances[i % 2].AppendAsync(AuditTestData.Event(tenant, i), CancellationToken.None))));

        Assert.Equal(AuditTestData.Range(1, 60), entries.Select(e => e.Sequence).Order());
        var verification = await fixture.AuditLog.VerifyAsync(tenant, CancellationToken.None);
        Assert.True(verification.IsIntact, verification.Reason);
        Assert.Equal(60, verification.EntriesChecked);
    }

    [Fact]
    public async Task Interleaved_tenants_keep_independent_chains()
    {
        fixture.SkipIfUnavailable();
        string[] tenants = [AuditTestData.NewTenant(), AuditTestData.NewTenant()];

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(() => fixture.AuditLog.AppendAsync(AuditTestData.Event(tenants[i % 2], i), CancellationToken.None))));

        foreach (var tenant in tenants)
        {
            Assert.Equal(AuditTestData.Range(1, 20), await fixture.SequencesAsync(tenant));
            var verification = await fixture.AuditLog.VerifyAsync(tenant, CancellationToken.None);
            Assert.True(verification.IsIntact, verification.Reason);
            Assert.Equal(20, verification.EntriesChecked);

            var listed = await fixture.AuditLog.ListAsync(new AuditQuery(tenant, null, null, null, null, 1, 200), CancellationToken.None);
            Assert.Equal(20, listed.TotalCount);
            Assert.All(listed.Items, e => Assert.Equal(tenant, e.Event.TenantId));
            Assert.Null(listed.Items.Single(e => e.Sequence == 1).PreviousHash);
        }
    }

    [Fact]
    public async Task Tenant_ids_that_differ_only_in_case_never_share_a_chain_or_a_listing()
    {
        fixture.SkipIfUnavailable();
        var lower = AuditTestData.NewTenant();
        var upper = lower.ToUpperInvariant();

        await fixture.AuditLog.AppendAsync(AuditTestData.Event(lower, 0), CancellationToken.None);
        await fixture.AuditLog.AppendAsync(AuditTestData.Event(upper, 1), CancellationToken.None);
        await fixture.AuditLog.AppendAsync(AuditTestData.Event(lower, 2), CancellationToken.None);

        // The database default collation is case-insensitive; the audit columns are not.
        Assert.Equal(AuditTestData.Range(1, 2), await fixture.SequencesAsync(lower));
        Assert.Equal(AuditTestData.Range(1, 1), await fixture.SequencesAsync(upper));
        var listed = await fixture.AuditLog.ListAsync(new AuditQuery(upper, null, null, null, null, 1, 50), CancellationToken.None);
        Assert.Equal(1, listed.TotalCount);
        Assert.Equal(upper, Assert.Single(listed.Items).Event.TenantId);
        Assert.True((await fixture.AuditLog.VerifyAsync(lower, CancellationToken.None)).IsIntact);
        Assert.True((await fixture.AuditLog.VerifyAsync(upper, CancellationToken.None)).IsIntact);
    }

    [Fact]
    public async Task Sql_server_itself_refuses_to_update_delete_or_truncate_audit_entries()
    {
        fixture.SkipIfUnavailable();
        var tenant = AuditTestData.NewTenant();
        for (var i = 0; i < 3; i++)
        {
            await fixture.AuditLog.AppendAsync(AuditTestData.Event(tenant, i), CancellationToken.None);
        }

        // The container's login is sysadmin: not even the most privileged DBA can rewrite history.
        var update = await Assert.ThrowsAsync<SqlException>(() => fixture.ExecuteAsync(
            "UPDATE [AuditEntries] SET [Outcome] = N'Blocked' WHERE [TenantId] = @tenant AND [Sequence] = 2", ("@tenant", tenant)));
        var delete = await Assert.ThrowsAsync<SqlException>(() => fixture.ExecuteAsync(
            "DELETE FROM [AuditEntries] WHERE [TenantId] = @tenant AND [Sequence] = 2", ("@tenant", tenant)));
        var truncate = await Assert.ThrowsAsync<SqlException>(() => fixture.ExecuteAsync("TRUNCATE TABLE [AuditEntries];"));

        Assert.Equal(AppendOnlyLedgerViolation, update.Number);
        Assert.Equal(AppendOnlyLedgerViolation, delete.Number);
        Assert.Equal(TruncateNotSupported, truncate.Number);

        var verification = await fixture.AuditLog.VerifyAsync(tenant, CancellationToken.None);
        Assert.True(verification.IsIntact, verification.Reason);
        Assert.Equal(3, verification.EntriesChecked);
        var digest = Assert.IsType<string>(verification.DatabaseLedgerDigest);
        using (var json = JsonDocument.Parse(digest))
        {
            Assert.Equal(AuditSqlServerFixture.DatabaseName, json.RootElement.GetProperty("database_name").GetString());
            Assert.StartsWith("0x", json.RootElement.GetProperty("hash").GetString(), StringComparison.Ordinal);
        }

        // The engine vouches for every ledger table against the digest it just produced; it raises an error otherwise.
        await fixture.ExecuteAsync("EXEC sys.sp_verify_database_ledger @digests = @digest;", ("@digest", digest));
    }

    [Fact]
    public async Task The_audit_table_is_an_append_only_ledger_whose_hidden_columns_the_ef_model_never_maps()
    {
        fixture.SkipIfUnavailable();

        var ledgerType = await fixture.QueryAsync(
            "SELECT [ledger_type_desc] FROM sys.tables WHERE [name] = N'AuditEntries'", reader => reader.GetString(0));
        Assert.Equal("APPEND_ONLY_LEDGER_TABLE", Assert.Single(ledgerType));

        var columns = await fixture.QueryAsync(
            "SELECT [name], [is_hidden], [generated_always_type_desc] FROM sys.columns WHERE [object_id] = OBJECT_ID(N'AuditEntries')",
            reader => (Name: reader.GetString(0), Hidden: reader.GetBoolean(1), GeneratedAlways: reader.GetString(2)));
        Assert.Equal(
            ["AS_SEQUENCE_NUMBER_START", "AS_TRANSACTION_ID_START"],
            columns.Where(c => c.Hidden).Select(c => c.GeneratedAlways).Order(StringComparer.Ordinal));

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var mapped = db.Model.FindEntityType(typeof(AuditEntryRecord))!.GetProperties().Select(p => p.GetColumnName());
        Assert.Equal(
            mapped.Order(StringComparer.Ordinal),
            columns.Where(c => !c.Hidden).Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_entry_survives_the_rollback_of_the_callers_ambient_transaction()
    {
        fixture.SkipIfUnavailable();
        var tenant = AuditTestData.NewTenant();

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await fixture.AuditLog.AppendAsync(AuditTestData.Event(tenant), CancellationToken.None);

            // Disposed without Complete(): whatever the caller did inside the scope is rolled back.
        }

        Assert.Equal(AuditTestData.Range(1, 1), await fixture.SequencesAsync(tenant));
    }

    [Fact]
    public async Task Listing_filters_by_time_subject_and_outcome_and_pages_newest_first()
    {
        fixture.SkipIfUnavailable();

        await AuditLogScenarios.ListingFiltersAndPagesNewestFirstAsync(fixture.AuditLog);
    }
}
