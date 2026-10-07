using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Sentinel.Gateway.IntegrationTests;

/// <summary>
/// A route added without authorization is the classic way a gateway ends up with an open door. Every endpoint the running
/// application maps must carry an authorization requirement, except a short, reviewed list.
/// </summary>
public sealed class EndpointAuthorizationTests(KnowledgeGatewayFactory factory) : IClassFixture<KnowledgeGatewayFactory>
{
    private static readonly string[] Anonymous = ["/health/live", "/health/ready", "/dev/personas", "/dev/token", "/openapi/{documentName}.json", "/scalar/{documentName?}"];

    [Fact]
    public void Every_endpoint_requires_authorization_unless_it_is_on_the_reviewed_list()
    {
        var endpoints = factory.Services.GetServices<EndpointDataSource>().SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(endpoints);

        var open = endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .Where(route => !Anonymous.Contains(route, StringComparer.Ordinal))
            .ToList();

        Assert.True(open.Count == 0, "Endpoints without authorization: " + string.Join(", ", open));
    }

    [Fact]
    public void The_api_and_proxy_surfaces_are_covered_by_named_policies()
    {
        var protectedRoutes = factory.Services.GetServices<EndpointDataSource>().SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? string.Empty).StartsWith("/api/", StringComparison.Ordinal)
                || (e.RoutePattern.RawText ?? string.Empty).StartsWith("/v1/", StringComparison.Ordinal))
            .ToList();

        Assert.True(protectedRoutes.Count >= 8);
        Assert.All(protectedRoutes, e =>
            Assert.True(
                e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => !string.IsNullOrEmpty(a.Policy)),
                $"{e.RoutePattern.RawText} must name an authorization policy."));
    }
}
