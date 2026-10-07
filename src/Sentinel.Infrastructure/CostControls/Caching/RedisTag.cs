using System.Text;

namespace Sentinel.Infrastructure.CostControls.Caching;

/// <summary>
/// Writing and querying Redis Query Engine TAG values so that a tag filter matches exactly one value and nothing
/// else. The cache's tenant isolation depends on it, so the rules are spelled out:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Query escaping.</b> In a <c>@field:{value}</c> filter (DIALECT 2) every ASCII punctuation and whitespace
/// character is syntax (<c>-</c>, <c>.</c>, <c>@</c>, space, <c>*</c> prefix wildcard, <c>|</c> union, <c>}</c>,
/// <c>$</c> parameter...). Each one is preceded by a backslash. Non-ASCII characters must <i>not</i> be escaped: the
/// engine then no longer matches them (verified against Redis 8.10).</item>
/// <item><b>Exact storage.</b> Single-valued tags (tenant, namespace, tier) are declared CASESENSITIVE, otherwise
/// "Contoso" and "contoso" would share a partition, and with <see cref="SingleValueSeparator"/> as separator,
/// otherwise a tenant id containing a comma ("a,b") would be indexed as two tags and match a query for tenant "a".</item>
/// <item><b>Unrepresentable values.</b> The engine trims surrounding whitespace ("a " would collide with "a"), cannot
/// store the separator, and UTF-8 encoding would turn distinct lone surrogates into the same U+FFFD. Such values are
/// rejected by <see cref="IsRepresentable"/>; callers treat the scope as not cacheable (fail closed).</item>
/// </list>
/// Results are still re-checked against the scope after every query, so an escaping mistake could only cause a miss,
/// never a cross-tenant hit.
/// </remarks>
internal static class RedisTag
{
    /// <summary>U+001F (unit separator): a control character, which <see cref="IsRepresentable"/> never accepts.</summary>
    public const char SingleValueSeparator = '\u001F';

    public const int MaxValueLength = 256;

    /// <summary>Escapes <paramref name="value"/> for use between the braces of a TAG filter.</summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length * 2);
        foreach (var c in value)
        {
            if (char.IsAscii(c) && !char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary><c>@field:{escaped}</c>.</summary>
    public static string Filter(string field, string value) => $"@{field}:{{{Escape(value)}}}";

    /// <summary>
    /// True when the engine stores <paramref name="value"/> verbatim as a single tag, so exact matching is possible.
    /// </summary>
    public static bool IsRepresentable(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength)
        {
            return false;
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c))
            {
                return false;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }
}
