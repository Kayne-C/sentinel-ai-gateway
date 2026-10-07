using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Sentinel.Gateway.Http;

/// <summary>Request rate limit (section <c>RateLimiting</c>); token budgets are a separate, per-tenant control.</summary>
public sealed class GatewayRateLimitOptions
{
    public const string Section = "RateLimiting";

    public bool Enabled { get; set; } = true;

    public int RequestsPerSecondPerSubject { get; set; } = 20;

    public int Burst { get; set; } = 40;
}

public static class GatewayRateLimiting
{
    public static IServiceCollection AddGatewayRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GatewayRateLimitOptions>().Bind(configuration.GetSection(GatewayRateLimitOptions.Section));
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay) ? delay : TimeSpan.FromSeconds(1);
                context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await Results.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too many requests",
                        detail: "The request rate for this identity is too high.",
                        extensions: new Dictionary<string, object?> { ["code"] = "RateLimit.Exceeded" })
                    .ExecuteAsync(context.HttpContext);
            };

            // Partitioned by the authenticated subject (tenant + object id); anonymous callers share their IP's bucket,
            // so an unauthenticated flood cannot exhaust anybody else's allowance.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<GatewayRateLimitOptions>>().Value;
                if (!options.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter("off");
                }

                var tenant = context.User.FindFirst("tid")?.Value;
                var subject = context.User.FindFirst("oid")?.Value ?? context.User.FindFirst("sub")?.Value;
                var key = tenant is not null && subject is not null
                    ? $"subject:{tenant}:{subject}"
                    : $"ip:{context.Connection.RemoteIpAddress}";

                return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = Math.Max(1, options.Burst),
                    TokensPerPeriod = Math.Max(1, options.RequestsPerSecondPerSubject),
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
        });

        return services;
    }
}
