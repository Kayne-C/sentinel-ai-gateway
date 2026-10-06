using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions.Behaviors;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Application.Common;

namespace Sentinel.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddScoped<ISender, Sender>();
        foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var contract in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
            {
                services.AddScoped(contract, type);
            }
        }

        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(RequestTelemetryBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);

        services.AddOptions<ModelCatalogOptions>().Bind(configuration.GetSection(ModelCatalogOptions.Section));
        services.AddOptions<RagOptions>().Bind(configuration.GetSection(RagOptions.Section));
        services.AddOptions<AuditPolicyOptions>().Bind(configuration.GetSection(AuditPolicyOptions.Section));

        ApplicationServices.Register(services);
        return services;
    }
}
