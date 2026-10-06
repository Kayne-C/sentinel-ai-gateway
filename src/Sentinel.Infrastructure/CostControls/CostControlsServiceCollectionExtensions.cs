using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Sentinel.Infrastructure.CostControls;

/// <summary>Semantic cache, token budgets, model routing, token counting.</summary>
public static class CostControlsServiceCollectionExtensions
{
    public static IServiceCollection AddCostControls(this IServiceCollection services, IConfiguration configuration)
    {
        _ = configuration;
        return services;
    }
}
