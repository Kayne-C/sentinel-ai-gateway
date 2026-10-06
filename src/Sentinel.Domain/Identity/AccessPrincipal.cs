namespace Sentinel.Domain.Identity;

/// <summary>Canonical, case-normalised ACL principal strings shared by documents and callers.</summary>
public static class AccessPrincipal
{
    public const string Everyone = "everyone";
    public const int MaxLength = 128;

    public static string User(string objectId) => "user:" + Normalize(objectId);

    public static string Group(string groupId) => "group:" + Normalize(groupId);

    /// <summary>
    /// Parses <c>everyone</c>, <c>user:{id}</c> or <c>group:{id}</c> (case-insensitive prefix, trimmed id).
    /// Anything else is rejected, so a typo can never silently widen or narrow access.
    /// </summary>
    public static bool TryParse(string? value, out string principal)
    {
        principal = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals(Everyone, StringComparison.OrdinalIgnoreCase))
        {
            principal = Everyone;
            return true;
        }

        var separator = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            return false;
        }

        var kind = trimmed[..separator].ToLowerInvariant();
        var id = trimmed[(separator + 1)..].Trim();
        if (id.Length == 0 || id.Any(char.IsWhiteSpace) || kind is not ("user" or "group"))
        {
            return false;
        }

        principal = kind == "user" ? User(id) : Group(id);
        return principal.Length <= MaxLength;
    }

    private static string Normalize(string id) => id.Trim().ToLowerInvariant();
}
