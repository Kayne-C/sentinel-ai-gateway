using Microsoft.EntityFrameworkCore;
using Sentinel.Infrastructure.Audit;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.IntegrationTests.Audit;

/// <summary>
/// The ledger script replaces EF's <c>CreateTable</c> in the SQL Server migration, so it has to stay what EF would
/// generate for the model. These compare against EF's own DDL (no database needed: the script is generated offline).
/// </summary>
public sealed class AuditLedgerSqlTests
{
    private const string LedgerOption = " WITH (LEDGER = ON (APPEND_ONLY = ON))";

    [Fact]
    public void The_ledger_table_script_is_the_ef_create_table_plus_the_append_only_ledger_option()
    {
        var efCreateTable = Assert.Single(
            EfStatements(),
            s => s.StartsWith("CREATE TABLE [AuditEntries] (", StringComparison.Ordinal));

        Assert.EndsWith("\n);", efCreateTable, StringComparison.Ordinal);
        Assert.Equal(efCreateTable[..^1] + LedgerOption + ";", Normalize(AuditLedgerSql.CreateTable));
    }

    [Fact]
    public void The_index_script_is_the_ef_create_index_statements()
    {
        var efIndexes = EfStatements().Where(s => s.Contains(" ON [AuditEntries] ", StringComparison.Ordinal)).ToList();

        Assert.Equal(efIndexes, Normalize(AuditLedgerSql.CreateIndexes).Split('\n'));
        Assert.Contains(efIndexes, s => s.StartsWith("CREATE UNIQUE INDEX [IX_AuditEntries_TenantId_Sequence]", StringComparison.Ordinal));
    }

    private static List<string> EfStatements()
    {
        using var db = new SentinelDbContext(new DbContextOptionsBuilder<SentinelDbContext>()
            .UseSqlServer("Server=unused;Database=unused", sql => sql.UseCompatibilityLevel(170))
            .Options);

        // The script separates statements with lines reading "GO".
        var statements = new List<string>();
        var current = new List<string>();
        foreach (var line in Normalize(db.Database.GenerateCreateScript()).Split('\n').Append("GO"))
        {
            if (line.Trim() != "GO")
            {
                current.Add(line);
                continue;
            }

            var statement = string.Join('\n', current).Trim();
            if (statement.Length > 0)
            {
                statements.Add(statement);
            }

            current.Clear();
        }

        return statements;
    }

    private static string Normalize(string sql) => sql.ReplaceLineEndings("\n").Trim();
}
