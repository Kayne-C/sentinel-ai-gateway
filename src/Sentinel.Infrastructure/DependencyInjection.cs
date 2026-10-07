using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Sentinel.Infrastructure.AI;
using Sentinel.Infrastructure.Audit;
using Sentinel.Infrastructure.CostControls;
using Sentinel.Infrastructure.Knowledge;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure;

public static class DependencyInjection
{
    public const string SqlServerMigrationsAssembly = "Sentinel.Migrations.SqlServer";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        // A pooled factory: components that must commit independently of the request's unit of work (the audit
        // log) create their own short-lived context; everything else uses the scoped one.
        services.AddPooledDbContextFactory<SentinelDbContext>((sp, options) =>
        {
            var database = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            switch (database.Provider)
            {
                case DatabaseProvider.SqlServer:
                    options.UseSqlServer(database.ConnectionString, sql => sql
                        .MigrationsAssembly(SqlServerMigrationsAssembly)
                        .UseCompatibilityLevel(170)
                        .EnableRetryOnFailure(maxRetryCount: 5));
                    break;
                default:
                    options.UseSqlite(database.ConnectionString);
                    break;
            }
        });

        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<SentinelDbContext>>().CreateDbContext());

        services.AddKnowledgeStore(configuration);
        services.AddCostControls(configuration);
        services.AddAuditLog(configuration);
        services.AddAiProviders(configuration);
        return services;
    }

    public static IHealthChecksBuilder AddInfrastructureHealthChecks(this IHealthChecksBuilder builder) => builder
        .AddDbContextCheck<SentinelDbContext>("database", tags: ["ready"]);

    /// <summary>Applies migrations (SQLite: creates the schema) when configured.</summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();

        if (options.ApplyMigrationsOnStartup)
        {
            if (options.Provider == DatabaseProvider.Sqlite)
            {
                await db.Database.EnsureCreatedAsync(cancellationToken);
            }
            else
            {
                await db.Database.MigrateAsync(cancellationToken);
            }
        }
    }
}
