using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Guardrails.Injection;
using Sentinel.Guardrails.Pii;

namespace Sentinel.Guardrails;

public static class DependencyInjection
{
    public static IServiceCollection AddGuardrails(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PiiOptions>().Bind(configuration.GetSection(PiiOptions.Section));
        services.AddOptions<InjectionOptions>().Bind(configuration.GetSection(InjectionOptions.Section));
        return services;
    }
}
