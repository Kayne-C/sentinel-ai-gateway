using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Infrastructure.Audit;
using Sentinel.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace Sentinel.Infrastructure.IntegrationTests.Audit;

[CollectionDefinition(Name)]
public sealed class AuditSqlServerCollectionDefinition : ICollectionFixture<AuditSqlServerFixture>
{
    public const string Name = "Audit on SQL Server 2025";
}

/// <summary>
/// One SQL Server 2025 container for the audit tests, its own database, the schema exactly as EF creates it and then
/// <c>AuditEntries</c> rebuilt from <see cref="AuditLedgerSql"/>, so ledger behaviour in these tests is the real thing.
/// Tests skip (never fail) when the container cannot start; a broken ledger script still fails them.
/// </summary>
public sealed class AuditSqlServerFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/mssql/server:2025-latest";
    public const string DatabaseName = "SentinelAuditTests";

    private MsSqlContainer? _container;
    private ServiceProvider? _services;

    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>One gateway "process" (production registration), shared by the tests of the collection.</summary>
    public IServiceProvider Services => _services ?? throw new InvalidOperationException(SkipReason);

    public IAuditLog AuditLog => Services.GetRequiredService<IAuditLog>();

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder(Image)
                .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "2048")
                .Build();
            await _container.StartAsync();
        }
        catch (Exception exception)
        {
            SkipReason = $"SQL Server 2025 container unavailable ({exception.GetType().Name}: {exception.Message})";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = DatabaseName }.ConnectionString;
        _services = AuditTestServices.ForSqlServer(ConnectionString);
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE [AuditEntries];");
            await db.Database.ExecuteSqlRawAsync(AuditLedgerSql.CreateTable);
            await db.Database.ExecuteSqlRawAsync(AuditLedgerSql.CreateIndexes);

            // sys.sp_verify_database_ledger runs under snapshot isolation.
            await db.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;");
        }
        catch
        {
            // A broken schema or ledger script must fail the tests, but must not leave the container running.
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
            _services = null;
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    public void SkipIfUnavailable()
    {
        if (SkipReason is not null)
        {
            Assert.Skip(SkipReason);
        }
    }

    /// <summary>A second gateway "process": its own pooled factory and its own in-process append locks.</summary>
    public ServiceProvider CreateServices() => AuditTestServices.ForSqlServer(ConnectionString);

    public async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = CreateCommand(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<List<T>> QueryAsync<T>(string sql, Func<SqlDataReader, T> read, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = CreateCommand(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    /// <summary>The tenant's sequences as SQL Server itself compares tenant ids (raw SQL, column collation).</summary>
    public async Task<long[]> SequencesAsync(string tenantId) =>
        [.. await QueryAsync(
            "SELECT [Sequence] FROM [AuditEntries] WHERE [TenantId] = @tenant ORDER BY [Sequence]",
            reader => reader.GetInt64(0),
            ("@tenant", tenantId))];

    private static SqlCommand CreateCommand(SqlConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
