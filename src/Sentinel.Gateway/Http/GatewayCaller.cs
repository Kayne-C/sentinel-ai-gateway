using Microsoft.Extensions.Options;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Gateway.Security;

namespace Sentinel.Gateway.Http;

/// <summary>Thrown when a validated token cannot be turned into a caller (wrong tenant, group overage...).</summary>
public sealed class GatewayAuthException(Error error) : Exception(error.Description)
{
    public Error Error { get; } = error;
}

/// <summary>
/// Minimal-API parameter that resolves the caller from the authenticated principal. Endpoints receive an identity or the
/// request is refused; there is no path that reaches a handler with a made-up tenant.
/// </summary>
public sealed class GatewayCaller
{
    private GatewayCaller(CallerIdentity identity) => Identity = identity;

    public CallerIdentity Identity { get; }

    public static ValueTask<GatewayCaller?> BindAsync(HttpContext context) => ValueTask.FromResult<GatewayCaller?>(Resolve(context));

    public static GatewayCaller Resolve(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<GatewayAuthOptions>>().Value;
        var result = CallerIdentityFactory.Create(context.User, options.AllowedTenants);
        return result.IsSuccess ? new GatewayCaller(result.Value) : throw new GatewayAuthException(result.Error);
    }
}
