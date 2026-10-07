using System.Security.Claims;
using System.Text.Json;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;

namespace Sentinel.Gateway.Security;

/// <summary>
/// Turns a validated Entra ID access token into the <see cref="CallerIdentity"/> everything downstream is keyed by
/// (tenant, subject, group ACL principals). Only claims of the validated token are used, never headers or body fields.
/// </summary>
public static class CallerIdentityFactory
{
    public static readonly Error GroupsOverage = Error.Forbidden(
        "Auth.GroupsOverage",
        "The token does not carry the complete group list (group overage). Permissions are evaluated from group " +
        "membership, so the request is refused instead of guessing: assign groups to the app's roles or emit fewer groups.");

    public static readonly Error MissingClaims = Error.Unauthorized(
        "Auth.MissingClaims", "The token does not identify a tenant and a subject (tid, oid).");

    public static readonly Error TenantNotAllowed = Error.Forbidden("Auth.TenantNotAllowed", "This tenant is not served by this gateway.");

    public static Result<CallerIdentity> Create(ClaimsPrincipal principal, IReadOnlyCollection<string> allowedTenants)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var tenantId = principal.FindFirstValue("tid");
        var subjectId = principal.FindFirstValue("oid") ?? principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(subjectId))
        {
            return MissingClaims;
        }

        if (!allowedTenants.Contains(tenantId, StringComparer.OrdinalIgnoreCase))
        {
            return TenantNotAllowed;
        }

        if (HasGroupsOverage(principal))
        {
            return GroupsOverage;
        }

        var kind = string.Equals(principal.FindFirstValue("idtyp"), "app", StringComparison.OrdinalIgnoreCase)
            ? CallerKind.Application
            : CallerKind.User;

        return new CallerIdentity(
            tenantId.Trim().ToLowerInvariant(),
            subjectId.Trim().ToLowerInvariant(),
            principal.FindFirstValue("name") ?? principal.FindFirstValue("preferred_username"),
            principal.FindAll("groups").Select(c => c.Value),
            principal.FindAll("roles").Select(c => c.Value),
            kind);
    }

    /// <summary>
    /// With more than ~200 groups Entra drops the <c>groups</c> claim and points to Microsoft Graph instead
    /// (<c>_claim_names.groups</c>, or <c>hasgroups</c> in implicit-flow tokens). Treating that as "no groups" would make
    /// a heavily-grouped user see less, which is safe, but it would also hide the misconfiguration; refuse explicitly.
    /// </summary>
    private static bool HasGroupsOverage(ClaimsPrincipal principal)
    {
        if (string.Equals(principal.FindFirstValue("hasgroups"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var claimNames = principal.FindFirstValue("_claim_names");
        if (string.IsNullOrWhiteSpace(claimNames))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(claimNames);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("groups", out _);
        }
        catch (JsonException)
        {
            return true; // Unreadable overage marker: fail closed.
        }
    }
}
