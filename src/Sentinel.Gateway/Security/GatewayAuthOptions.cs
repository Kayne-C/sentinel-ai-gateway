namespace Sentinel.Gateway.Security;

public enum AuthMode
{
    /// <summary>Microsoft Entra ID access tokens (production).</summary>
    Entra,

    /// <summary>Locally signed demo tokens for the personas of the demo corpus. Never for production.</summary>
    Development,
}

/// <summary>Authentication settings (section <c>Auth</c>). The default is the secure one: Entra ID.</summary>
public sealed class GatewayAuthOptions
{
    public const string Section = "Auth";

    public AuthMode Mode { get; set; } = AuthMode.Entra;

    /// <summary>
    /// Entra tenant ids (the <c>tid</c> claim) allowed to use this gateway; every tenant is isolated from the others.
    /// Empty = deny everything: a gateway that has not been told whom it serves serves nobody.
    /// </summary>
    public List<string> AllowedTenants { get; set; } = [];

    public EntraOptions Entra { get; set; } = new();

    public DevelopmentAuthOptions Development { get; set; } = new();
}

public sealed class EntraOptions
{
    /// <summary>Token authority host, e.g. <c>https://login.microsoftonline.com</c>.</summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com";

    /// <summary>
    /// A tenant id for a single-tenant app, or <c>organizations</c> for a multi-tenant app. In the multi-tenant case
    /// the issuer is validated per token against <see cref="GatewayAuthOptions.AllowedTenants"/>.
    /// </summary>
    public string TenantId { get; set; } = "organizations";

    /// <summary>Application (client) id of the gateway's app registration.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Accepted <c>aud</c> values; defaults to <c>api://{ClientId}</c> and the bare client id.</summary>
    public List<string> Audiences { get; set; } = [];

    public bool IsMultiTenant =>
        string.Equals(TenantId, "organizations", StringComparison.OrdinalIgnoreCase)
        || string.Equals(TenantId, "common", StringComparison.OrdinalIgnoreCase);
}

public sealed class DevelopmentAuthOptions
{
    public const string Issuer = "sentinel-dev";
    public const string Audience = "sentinel";

    /// <summary>HMAC key for demo tokens (at least 32 characters).</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Required outside the <c>Development</c> environment: demo tokens let anyone who can reach the gateway become any
    /// persona, so using them anywhere else (a compose demo, a test cluster) must be a deliberate decision.
    /// </summary>
    public bool AcknowledgeInsecure { get; set; }

    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(8);
}
