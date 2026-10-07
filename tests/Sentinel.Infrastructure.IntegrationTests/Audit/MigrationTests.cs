using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.IntegrationTests.Audit;

/// <summary>The shipped SQL Server migrations, applied to a real SQL Server 2025 (not the EnsureCreated schema).</summary>
[Collection(AuditSqlServerCollectionDefinition.Name)]
public sealed class MigrationTests(AuditSqlServerFixture fixture)
{
    [Fact]
    public async Task Migrations_create_native_vector_columns_and_an_append_only_ledger_for_the_audit_log()
    {
        fixture.SkipIfUnavailable();
        var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = "SentinelMigrationTests" }.ConnectionString;
        await using var services = AuditTestServices.ForSqlServer(connectionString);

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SentinelDbContext>().Database.MigrateAsync();
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        Assert.Equal("vector", await ScalarAsync(connection,
            "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'DocumentChunks' AND COLUMN_NAME = 'Embedding'"));

        Assert.Equal("APPEND_ONLY_LEDGER_TABLE", await ScalarAsync(connection, "SELECT ledger_type_desc FROM sys.tables WHERE name = 'AuditEntries'"));

        // The migrated schema carries real traffic: append, verify the chain, and SQL Server refuses a tamper attempt.
        var audit = services.CreateAsyncScope().ServiceProvider.GetRequiredService<IAuditLog>();
        for (var i = 0; i < 5; i++)
        {
            await audit.AppendAsync(AuditTestData.Event("migrated-tenant"), CancellationToken.None);
        }

        var verification = await audit.VerifyAsync("migrated-tenant", CancellationToken.None);
        Assert.True(verification.IsIntact, verification.Reason);
        Assert.Equal(5, verification.EntriesChecked);

        await using var tamper = new SqlCommand("UPDATE AuditEntries SET Outcome = 'Allowed' WHERE TenantId = 'migrated-tenant'", connection);
        await Assert.ThrowsAsync<SqlException>(() => tamper.ExecuteNonQueryAsync());
    }

    private static async Task<string?> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
