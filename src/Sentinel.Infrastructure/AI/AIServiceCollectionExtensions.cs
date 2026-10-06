using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Sentinel.Infrastructure.AI;

/// <summary>Chat and embedding providers.</summary>
public static class AIServiceCollectionExtensions
{
    public static IServiceCollection AddAiProviders(this IServiceCollection services, IConfiguration configuration)
    {
        _ = configuration;
        return services;
    }
}
