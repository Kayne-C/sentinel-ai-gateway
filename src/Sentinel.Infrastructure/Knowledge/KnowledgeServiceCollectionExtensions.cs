using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Sentinel.Infrastructure.Knowledge;

/// <summary>Knowledge base: EF Core records, vector search, repository.</summary>
public static class KnowledgeServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledgeStore(this IServiceCollection services, IConfiguration configuration)
    {
        _ = configuration;
        return services;
    }
}
