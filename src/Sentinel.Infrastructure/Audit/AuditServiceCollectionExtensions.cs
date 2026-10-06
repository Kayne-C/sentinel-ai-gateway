using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Sentinel.Infrastructure.Audit;

/// <summary>Hash-chained audit log.</summary>
public static class AuditServiceCollectionExtensions
{
    public static IServiceCollection AddAuditLog(this IServiceCollection services, IConfiguration configuration)
    {
        _ = configuration;
        return services;
    }
}
