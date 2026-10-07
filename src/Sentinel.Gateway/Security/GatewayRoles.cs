using Sentinel.Domain.Identity;

namespace Sentinel.Gateway.Security;

public static class GatewayPolicies
{
    /// <summary>Ask questions: <c>Sentinel.User</c> or <c>Sentinel.Admin</c>.</summary>
    public const string User = "sentinel.user";

    /// <summary>Manage documents, read the audit log: <c>Sentinel.Admin</c>.</summary>
    public const string Admin = "sentinel.admin";

    /// <summary>OpenAI-compatible proxy: <c>Sentinel.Proxy</c> or <c>Sentinel.Admin</c>.</summary>
    public const string Proxy = "sentinel.proxy";

    /// <summary>Any Sentinel role: read one's own token budget.</summary>
    public const string AnyRole = "sentinel.any";

    public static readonly string[] AnyRoles = [SentinelRoles.User, SentinelRoles.Admin, SentinelRoles.Proxy];
    public static readonly string[] UserRoles = [SentinelRoles.User, SentinelRoles.Admin];
    public static readonly string[] ProxyRoles = [SentinelRoles.Proxy, SentinelRoles.Admin];
}
