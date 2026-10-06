namespace Sentinel.Domain.Identity;

public enum CallerKind
{
    /// <summary>A person, signed in through Entra ID (delegated token).</summary>
    User,

    /// <summary>A workload (daemon, internal service) using an app-only token.</summary>
    Application,
}

/// <summary>
/// Who is asking, resolved once per request from the validated token (Entra ID claims: <c>tid</c>, <c>oid</c>,
/// <c>groups</c>, <c>roles</c>). Everything downstream — retrieval, cache, budgets, audit — is keyed by this, never
/// by anything the request body says.
/// </summary>
public sealed record CallerIdentity
{
    public CallerIdentity(string tenantId, string subjectId, string? displayName, IEnumerable<string> groups, IEnumerable<string> roles, CallerKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

        TenantId = tenantId;
        SubjectId = subjectId;
        DisplayName = displayName;
        Groups = groups.Where(g => !string.IsNullOrWhiteSpace(g)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Roles = roles.Where(r => !string.IsNullOrWhiteSpace(r)).ToHashSet(StringComparer.Ordinal);
        Kind = kind;
        Principals = BuildPrincipals(SubjectId, Groups);
    }

    public string TenantId { get; }

    /// <summary>Entra object id (<c>oid</c>) of the user or service principal.</summary>
    public string SubjectId { get; }

    public string? DisplayName { get; }

    /// <summary>Entra security group object ids.</summary>
    public IReadOnlySet<string> Groups { get; }

    /// <summary>App roles (e.g. <c>Sentinel.Admin</c>).</summary>
    public IReadOnlySet<string> Roles { get; }

    public CallerKind Kind { get; }

    /// <summary>
    /// The ACL principals this caller holds: <c>everyone</c>, <c>user:{oid}</c> and <c>group:{id}</c> per group.
    /// A document is visible iff its ACL shares at least one principal with this set.
    /// </summary>
    public IReadOnlySet<string> Principals { get; }

    public bool IsInRole(string role) => Roles.Contains(role);

    private static HashSet<string> BuildPrincipals(string subjectId, IEnumerable<string> groups)
    {
        var principals = new HashSet<string>(StringComparer.Ordinal) { AccessPrincipal.Everyone, AccessPrincipal.User(subjectId) };
        foreach (var group in groups)
        {
            principals.Add(AccessPrincipal.Group(group));
        }

        return principals;
    }
}

public static class SentinelRoles
{
    /// <summary>Manage the knowledge base, read the audit log.</summary>
    public const string Admin = "Sentinel.Admin";

    /// <summary>Ask questions over the knowledge base.</summary>
    public const string User = "Sentinel.User";

    /// <summary>Use the OpenAI-compatible proxy (typically granted to workloads).</summary>
    public const string Proxy = "Sentinel.Proxy";
}
