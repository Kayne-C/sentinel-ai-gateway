using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sentinel.Application.Features.Proxy;
using Sentinel.Application.Guardrails;

namespace Sentinel.Application;

/// <summary>Non-handler application services (prompt guard, proxy service). Handlers are discovered by scanning.</summary>
internal static class ApplicationServices
{
    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IPromptGuard, PromptGuard>();
        services.AddScoped<IChatProxyService, ChatProxyService>();
    }
}
