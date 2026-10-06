using System.Globalization;
using System.Text;
using Sentinel.Domain.Common;

namespace Sentinel.Infrastructure.Audit;

/// <summary>
/// The exact text an audit entry's hash covers. Two properties matter more than anything else here:
/// <list type="bullet">
/// <item><b>Deterministic everywhere.</b> Every value is rendered with an explicit, culture-invariant format (never a
/// bare <c>ToString()</c> that a <c>tr-TR</c> or <c>de-DE</c> server would render differently), so every machine
/// recomputes the same hash for the same row.</item>
/// <item><b>Injective.</b> Two different rows never produce the same text, otherwise a row could be changed without
/// changing its hash. Text is quoted and escaped, so a value can neither contain the field separator (moving
/// characters between adjacent fields changes the input) nor be confused with <c>null</c>; numbers and timestamps
/// have exactly one rendering per value, however the store hands them back (decimal scale, <c>DateTimeKind</c>).</item>
/// </list>
/// It renders the columns as persisted (<see cref="AuditEntryRecord"/>), never the parsed event, so verification
/// judges what the database actually holds.
/// </summary>
internal static class AuditCanonicalForm
{
    /// <summary>
    /// First field of every hash input: keeps audit hashes apart from any other SHA-256 use of the same fields and
    /// lets a future format be told apart from this one.
    /// </summary>
    public const string FormatVersion = "sentinel.audit/v1";

    /// <summary>
    /// <see cref="HashChain.Compute"/> over the previous hash and <see cref="Fields"/>. The previous hash itself is
    /// additionally checked by the verifier's linkage rule (entry n must reference the hash of entry n - 1).
    /// </summary>
    public static string ComputeHash(AuditEntryRecord record) => HashChain.Compute(record.PreviousHash, Fields(record));

    /// <summary>Every column except the surrogate key and the hashes, in a fixed order that must never change.</summary>
    public static string[] Fields(AuditEntryRecord record) =>
    [
        FormatVersion,
        Text(record.TenantId),
        Integer(record.Sequence),
        Timestamp(record.OccurredAtUtc),
        Text(record.SubjectId),
        Text(record.Operation),
        Text(record.Outcome),
        Text(record.Model),
        Text(record.ModelTier),
        Integer(record.PromptTokens),
        Integer(record.CompletionTokens),
        Money(record.EstimatedCostUsd),
        Text(record.RedactedPiiJson),
        Text(record.GuardrailFindingsJson),
        Text(record.SourcesJson),
        Text(record.PromptDigest),
        Text(record.RedactedPrompt),
        Text(record.ErrorCode),
        Real(record.LatencyMs),
        Text(record.TraceId),
        Text(record.Subject),
    ];

    /// <summary>
    /// <c>null</c> unquoted; anything else in double quotes with backslash escapes for the quote, the backslash, control
    /// characters and every UTF-16 surrogate. The result contains no newline (the field separator) and no surrogate,
    /// so its UTF-8 encoding is lossless: even an unpaired surrogate, which UTF-8 would turn into U+FFFD, stays distinct.
    /// </summary>
    internal static string Text(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            if (character is '"' or '\\')
            {
                builder.Append('\\').Append(character);
            }
            else if (char.IsControl(character) || char.IsSurrogate(character))
            {
                builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.Append('"').ToString();
    }

    internal static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// ISO 8601 round-trip format, always rendered as UTC: stores return the instant that was written as UTC with
    /// <see cref="DateTimeKind.Unspecified"/>, which must not change the hash.
    /// </summary>
    internal static string Timestamp(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Scale-free: <c>1.5</c> and <c>1.50000000</c> (how a <c>decimal(18,8)</c> column hands it back) render the same,
    /// two different values never do. Nothing is rounded here; rounding to the column scale happens before hashing.
    /// </summary>
    internal static string Money(decimal value) =>
        value == 0m ? "0" : value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>Shortest round-trip rendering. Negative zero renders as zero because SQL Server stores -0.0 as 0.0.</summary>
    internal static string Real(double value) =>
        value == 0d ? "0" : value.ToString("R", CultureInfo.InvariantCulture);
}
