using System.Text;

namespace Sentinel.Infrastructure.CostControls.Budgets;

/// <summary>
/// Counter keys: <c>budget:{tenant}:m:yyyyMM</c> and <c>budget:{tenant}:subject:d:yyyyMMdd</c>.
/// </summary>
/// <remarks>
/// Ids are percent-encoded (everything except <c>A–Z a–z 0–9 - . _ ~ @</c>, as UTF-8), which makes the mapping
/// injective: without it, tenant "a:b" with subject "c" and tenant "a" with subject "b:c" would share one counter, and
/// one could exhaust the other's budget. The braces are a Redis Cluster hash tag on the tenant: both counters of a
/// reservation hash to the same slot, as the single-script atomic reservation requires. Encoding guarantees the
/// tenant part contains no brace of its own.
/// </remarks>
internal static class BudgetKeys
{
    public const string Prefix = "budget:";
    public const int MaxIdLength = 256;

    public static string Tenant(string tenantId, BudgetPeriod month) => $"{Prefix}{{{Encode(tenantId)}}}:m:{month.Id}";

    public static string Subject(string tenantId, string subjectId, BudgetPeriod day) =>
        $"{Prefix}{{{Encode(tenantId)}}}:{Encode(subjectId)}:d:{day.Id}";

    internal static string Encode(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > MaxIdLength)
        {
            throw new ArgumentException($"Identifiers longer than {MaxIdLength} characters cannot be budgeted.", nameof(id));
        }

        var builder = new StringBuilder(id.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '@')
            {
                builder.Append(c);
                continue;
            }

            Rune rune;
            if (char.IsHighSurrogate(c) && i + 1 < id.Length && char.IsLowSurrogate(id[i + 1]))
            {
                rune = new Rune(c, id[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                // A lone surrogate has no UTF-8 form; replacing it would make distinct ids collide.
                throw new ArgumentException("Identifiers must be well-formed UTF-16.", nameof(id));
            }
            else
            {
                rune = new Rune(c);
            }

            var written = rune.EncodeToUtf8(utf8);
            foreach (var b in utf8[..written])
            {
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
