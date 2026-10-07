using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Sentinel.Infrastructure.Persistence;

public sealed class SentinelDbContext(DbContextOptions<SentinelDbContext> options) : DbContext(options)
{
    public DatabaseProvider Provider => Database.ProviderName switch
    {
        "Microsoft.EntityFrameworkCore.SqlServer" => DatabaseProvider.SqlServer,
        _ => DatabaseProvider.Sqlite,
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var provider = Provider;
        var configurations = typeof(SentinelDbContext).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IModelConfiguration).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in configurations)
        {
            ((IModelConfiguration)Activator.CreateInstance(type)!).Configure(modelBuilder, provider);
        }
    }

    /// <summary>Unique-key violation or deadlock: the caller should reload and retry.</summary>
    public static bool IsConcurrencyFailure(DbUpdateException exception) => exception.InnerException switch
    {
        SqlException sql => sql.Number is 2601 or 2627 or 1205,
        SqliteException sqlite => sqlite.SqliteErrorCode == 19 || sqlite.SqliteExtendedErrorCode is 2067 or 1555,
        _ => false,
    };
}
