using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sentinel.Domain.Identity;

namespace Sentinel.Gateway.Security;

public static class GatewayAuthentication
{
    /// <summary>
    /// Everything that depends on configuration is read through <see cref="IOptions{TOptions}"/> after the host is built,
    /// never from <c>IConfiguration</c> at registration time: configuration sources added later (environment, key vault,
    /// test hosts) must count, and a security setting must not silently fall back to a different file's value.
    /// </summary>
    public static IServiceCollection AddGatewayAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GatewayAuthOptions>().Bind(configuration.GetSection(GatewayAuthOptions.Section)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<GatewayAuthOptions>, GatewayAuthOptionsValidator>());
        services.AddSingleton<DevTokenIssuer>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<GatewayAuthOptions>>((jwt, auth) => ConfigureJwt(jwt, auth.Value));

        services.AddAuthorizationBuilder()
            .AddPolicy(GatewayPolicies.AnyRole, policy => policy.RequireAuthenticatedUser().RequireRole(GatewayPolicies.AnyRoles))
            .AddPolicy(GatewayPolicies.User, policy => policy.RequireAuthenticatedUser().RequireRole(GatewayPolicies.UserRoles))
            .AddPolicy(GatewayPolicies.Admin, policy => policy.RequireAuthenticatedUser().RequireRole(SentinelRoles.Admin))
            .AddPolicy(GatewayPolicies.Proxy, policy => policy.RequireAuthenticatedUser().RequireRole(GatewayPolicies.ProxyRoles));

        return services;
    }

    private static void ConfigureJwt(JwtBearerOptions jwt, GatewayAuthOptions options)
    {
        jwt.MapInboundClaims = false; // keep the raw claim names: tid, oid, groups, roles
        if (options.Mode == AuthMode.Development)
        {
            jwt.TokenValidationParameters = DevTokenIssuer.ValidationParameters(options.Development);
            return;
        }

        var entra = options.Entra;
        var multiTenant = entra.IsMultiTenant;
        var instance = entra.Instance.TrimEnd('/');
        var allowed = options.AllowedTenants.Select(t => t.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var audiences = entra.Audiences.Count > 0 ? entra.Audiences : [$"api://{entra.ClientId}", entra.ClientId];

        jwt.Authority = $"{instance}/{(multiTenant ? "organizations" : entra.TenantId)}/v2.0";
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidAudiences = audiences,
            NameClaimType = "name",
            RoleClaimType = "roles",
            ValidateIssuer = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        if (multiTenant)
        {
            // The "organizations" metadata carries a {tenantid} template, so the issuer is validated per token:
            // it must be exactly https://login.microsoftonline.com/{tid}/v2.0 of an allowed tenant.
            jwt.TokenValidationParameters.IssuerValidator = (issuer, token, _) =>
            {
                var tid = (token as Microsoft.IdentityModel.JsonWebTokens.JsonWebToken)?.GetPayloadValue<string>("tid");
                if (string.IsNullOrWhiteSpace(tid) || !allowed.Contains(tid.ToLowerInvariant())
                    || !string.Equals(issuer, $"{instance}/{tid}/v2.0", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SecurityTokenInvalidIssuerException("The token was not issued by an allowed tenant.") { InvalidIssuer = issuer };
                }

                return issuer;
            };
        }
    }
}

/// <summary>Fails startup, not the first request, when authentication is misconfigured.</summary>
internal sealed class GatewayAuthOptionsValidator(IHostEnvironment environment) : IValidateOptions<GatewayAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayAuthOptions options)
    {
        var errors = new List<string>();
        if (options.Mode == AuthMode.Development)
        {
            if (!environment.IsDevelopment() && !options.Development.AcknowledgeInsecure)
            {
                errors.Add(
                    "Auth:Mode=Development issues demo tokens for anyone who can reach the gateway. It is only allowed in the " +
                    "Development environment unless Auth:Development:AcknowledgeInsecure is set explicitly.");
            }

            if (options.Development.SigningKey.Length < 32)
            {
                errors.Add("Auth:Development:SigningKey must be at least 32 characters.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.Entra.ClientId))
            {
                errors.Add("Auth:Entra:ClientId is required when Auth:Mode is Entra.");
            }

            if (options.AllowedTenants.Count == 0)
            {
                errors.Add("Auth:AllowedTenants must list the Entra tenant ids this gateway serves.");
            }
            else if (options.AllowedTenants.Any(t => string.IsNullOrWhiteSpace(t) || t.Trim().Equals("*", StringComparison.Ordinal)))
            {
                errors.Add("Auth:AllowedTenants must contain concrete tenant ids (no empty entries, no wildcard).");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>Issues demo tokens that look like Entra v2 access tokens (tid, oid, groups, roles, idtyp).</summary>
public sealed class DevTokenIssuer(IOptions<GatewayAuthOptions> options, TimeProvider clock)
{
    public static TokenValidationParameters ValidationParameters(DevelopmentAuthOptions development) => new()
    {
        ValidIssuer = DevelopmentAuthOptions.Issuer,
        ValidAudience = DevelopmentAuthOptions.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(development.SigningKey)),
        NameClaimType = "name",
        RoleClaimType = "roles",
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1),
    };

    public string Issue(DemoPersona persona)
    {
        if (options.Value.Mode != AuthMode.Development)
        {
            throw new InvalidOperationException("Demo tokens are only available when Auth:Mode is Development.");
        }

        var development = options.Value.Development;
        var now = clock.GetUtcNow().UtcDateTime;
        var claims = new List<Claim>
        {
            new("tid", persona.TenantId),
            new("oid", persona.ObjectId),
            new("sub", persona.ObjectId),
            new("name", persona.DisplayName),
            new("idtyp", persona.Application ? "app" : "user"),
        };
        claims.AddRange(persona.Groups.Select(g => new Claim("groups", g)));
        claims.AddRange(persona.Roles.Select(r => new Claim("roles", r)));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = DevelopmentAuthOptions.Issuer,
            Audience = DevelopmentAuthOptions.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now,
            IssuedAt = now,
            Expires = now.Add(development.TokenLifetime),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(development.SigningKey)), SecurityAlgorithms.HmacSha256),
        };

        return new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(descriptor);
    }
}
