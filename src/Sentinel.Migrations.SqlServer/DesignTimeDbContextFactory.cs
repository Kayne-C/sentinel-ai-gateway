using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Sentinel.Infrastructure;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Migrations.SqlServer;

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when generating migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SentinelDbContext>
{
    public SentinelDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SentinelDbContext>()
        .UseSqlServer(
            "Server=localhost;Database=Sentinel;Integrated Security=true;TrustServerCertificate=True",
            sql => sql.MigrationsAssembly(DependencyInjection.SqlServerMigrationsAssembly).UseCompatibilityLevel(170))
        .Options);
}
