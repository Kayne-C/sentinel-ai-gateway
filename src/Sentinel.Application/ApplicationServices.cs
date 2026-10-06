using Microsoft.Extensions.DependencyInjection;

namespace Sentinel.Application;

/// <summary>Non-handler application services (prompt guard, proxy service). Handlers are discovered by scanning.</summary>
internal static class ApplicationServices
{
    public static void Register(IServiceCollection services)
    {
        _ = services;
    }
}
