namespace Sentinel.Infrastructure.Persistence;

public enum DatabaseProvider
{
    /// <summary>Zero-dependency local development and API tests (vector similarity computed in process).</summary>
    Sqlite,

    /// <summary>SQL Server 2025 / Azure SQL: native <c>vector</c> type and <c>VECTOR_DISTANCE</c>, ledger tables.</summary>
    SqlServer,

    /// <summary>Oracle Database 23ai: AI Vector Search (<c>VECTOR</c> type), immutable tables.</summary>
    Oracle,
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    public string ConnectionString { get; set; } = "Data Source=sentinel.dev.db";

    public bool ApplyMigrationsOnStartup { get; set; }

    public bool SeedDemoData { get; set; }
}
