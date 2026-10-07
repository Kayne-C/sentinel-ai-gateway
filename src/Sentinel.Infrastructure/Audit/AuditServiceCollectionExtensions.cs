using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;

namespace Sentinel.Infrastructure.Audit;

/// <summary>Hash-chained audit log.</summary>
public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAuditLog"/>. Requires the pooled <c>IDbContextFactory&lt;SentinelDbContext&gt;</c> that
    /// <c>AddInfrastructure</c> registers: every append commits on its own context, independently of the request's
    /// unit of work.
    /// </summary>
    public static IServiceCollection AddAuditLog(this IServiceCollection services, IConfiguration configuration)
    {
        _ = configuration;
        services.TryAddSingleton(TimeProvider.System);

        // Bound from configuration by AddApplication; without it the safe default applies (prompts are not stored).
        services.AddOptions<AuditPolicyOptions>();

        // Singleton on purpose: the per-tenant append locks only serialise writers that share them.
        services.TryAddSingleton<IAuditLog, AuditLog>();
        return services;
    }
}
