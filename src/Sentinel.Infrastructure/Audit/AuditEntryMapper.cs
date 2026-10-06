using System.Text.Json;
using Sentinel.Domain.Audit;

namespace Sentinel.Infrastructure.Audit;

/// <summary>
/// Converts between the domain event and its persisted form. Normalisation happens once, before hashing, and always
/// produces exactly what every supported store hands back (UTC timestamp, cost rounded to the column scale, free text
/// as well-formed UTF-16 fitted to its column), so the hash computed at append time is the hash verification later
/// recomputes from the stored row, on SQL Server and SQLite alike.
/// </summary>
internal static class AuditEntryMapper
{
    /// <summary>Largest magnitude a <c>decimal(18,8)</c> column can hold.</summary>
    private const decimal MaxCost = 9_999_999_999.99999999m;

    private const string TruncationMark = "\u2026";

    /// <summary>
    /// Validates and normalises an event into an unchained record (sequence and hashes are set per append attempt).
    /// Free text can be influenced by callers (a requested model name, a document id), and no such value may be a way
    /// to make the record of a request disappear or fail verification: unpaired surrogates become U+FFFD (what SQLite
    /// and the JSON serializer would store anyway) and text longer than its column is truncated, never rejected.
    /// </summary>
    /// <param name="auditEvent">The event to record.</param>
    /// <param name="storeRedactedPrompt">
    /// The audit policy (<c>AuditPolicyOptions.StoreRedactedPrompts</c>). Producers are expected to honour it already;
    /// the store enforces it once more, so no producer mistake can persist prompt text the policy forbids (the
    /// digest is always kept).
    /// </param>
    /// <exception cref="ArgumentException">The event cannot be recorded faithfully (missing identity, undefined enum, out-of-range number).</exception>
    public static AuditEntryRecord ToTemplate(AuditEvent auditEvent, bool storeRedactedPrompt)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        if (!IsIdentifier(auditEvent.TenantId))
        {
            throw new ArgumentException($"TenantId is required and must not exceed {AuditSchema.IdentifierMaxLength} characters.", nameof(auditEvent));
        }

        if (!IsIdentifier(auditEvent.SubjectId))
        {
            throw new ArgumentException($"SubjectId is required and must not exceed {AuditSchema.IdentifierMaxLength} characters.", nameof(auditEvent));
        }

        if (!Enum.IsDefined(auditEvent.Operation) || !Enum.IsDefined(auditEvent.Outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(auditEvent), "Operation and Outcome must be defined enum values.");
        }

        var cost = decimal.Round(auditEvent.EstimatedCostUsd, AuditSchema.CostScale, MidpointRounding.ToEven);
        if (Math.Abs(cost) > MaxCost)
        {
            throw new ArgumentOutOfRangeException(nameof(auditEvent), "EstimatedCostUsd does not fit decimal(18,8).");
        }

        if (!double.IsFinite(auditEvent.LatencyMs))
        {
            throw new ArgumentOutOfRangeException(nameof(auditEvent), "LatencyMs must be a finite number.");
        }

        return new AuditEntryRecord
        {
            TenantId = auditEvent.TenantId,
            OccurredAtUtc = ToUtc(auditEvent.OccurredAtUtc),
            SubjectId = auditEvent.SubjectId,
            Operation = auditEvent.Operation.ToString(),
            Outcome = auditEvent.Outcome.ToString(),
            Model = Fit(auditEvent.Model, AuditSchema.ModelMaxLength),
            ModelTier = Fit(auditEvent.ModelTier, AuditSchema.ModelTierMaxLength),
            PromptTokens = auditEvent.PromptTokens,
            CompletionTokens = auditEvent.CompletionTokens,
            EstimatedCostUsd = cost == 0m ? 0m : cost,
            RedactedPiiJson = WriteCounts(auditEvent.RedactedPii),
            GuardrailFindingsJson = WriteList(auditEvent.GuardrailFindings),
            SourcesJson = WriteList(auditEvent.Sources),
            PromptDigest = Fit(auditEvent.PromptDigest, AuditSchema.PromptDigestMaxLength),
            RedactedPrompt = storeRedactedPrompt ? WellFormed(auditEvent.RedactedPrompt) : null,
            ErrorCode = Fit(auditEvent.ErrorCode, AuditSchema.ErrorCodeMaxLength),

            // SQL Server stores -0.0 as 0.0; keep the in-memory value identical to what comes back.
            LatencyMs = auditEvent.LatencyMs == 0d ? 0d : auditEvent.LatencyMs,
            TraceId = Fit(auditEvent.TraceId, AuditSchema.TraceIdMaxLength),
            Subject = Fit(auditEvent.Subject, AuditSchema.SubjectMaxLength),
        };
    }

    /// <exception cref="InvalidDataException">A column holds a value the append path never writes (tampered row).</exception>
    public static AuditEntry ToEntry(AuditEntryRecord record) => new(
        record.Sequence,
        new AuditEvent
        {
            TenantId = record.TenantId,
            SubjectId = record.SubjectId,
            Operation = ParseName<AuditOperation>(record, record.Operation, nameof(AuditEntryRecord.Operation)),
            Outcome = ParseName<AuditOutcome>(record, record.Outcome, nameof(AuditEntryRecord.Outcome)),
            OccurredAtUtc = DateTime.SpecifyKind(record.OccurredAtUtc, DateTimeKind.Utc),
            Model = record.Model,
            ModelTier = record.ModelTier,
            PromptTokens = record.PromptTokens,
            CompletionTokens = record.CompletionTokens,
            EstimatedCostUsd = record.EstimatedCostUsd,
            RedactedPii = Read<Dictionary<string, int>>(record, record.RedactedPiiJson, nameof(AuditEntryRecord.RedactedPiiJson)),
            GuardrailFindings = Read<List<string>>(record, record.GuardrailFindingsJson, nameof(AuditEntryRecord.GuardrailFindingsJson)),
            Sources = Read<List<string>>(record, record.SourcesJson, nameof(AuditEntryRecord.SourcesJson)),
            PromptDigest = record.PromptDigest,
            RedactedPrompt = record.RedactedPrompt,
            ErrorCode = record.ErrorCode,
            LatencyMs = record.LatencyMs,
            TraceId = record.TraceId,
            Subject = record.Subject,
        },
        record.PreviousHash,
        record.Hash);

    /// <summary>Filters and timestamps are UTC instants; <see cref="DateTimeKind.Unspecified"/> is taken to already be UTC.</summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>Identifiers key chains and filters, so they are rejected rather than repaired when malformed.</summary>
    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= AuditSchema.IdentifierMaxLength
        && !HasUnpairedSurrogate(value);

    private static string? Fit(string? value, int maxLength)
    {
        value = WellFormed(value);
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        var keep = maxLength - TruncationMark.Length;
        if (char.IsHighSurrogate(value[keep - 1]))
        {
            keep--;
        }

        return string.Concat(value.AsSpan(0, keep), TruncationMark);
    }

    /// <summary>The value itself when it is well-formed UTF-16 (the common case), otherwise a copy with every unpaired surrogate replaced by U+FFFD.</summary>
    private static string? WellFormed(string? value)
    {
        if (value is null || !value.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF'))
        {
            return value;
        }

        char[]? repaired = null;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                repaired ??= value.ToCharArray();
                repaired[i] = '\uFFFD';
            }
        }

        return repaired is null ? value : new string(repaired);
    }

    private static bool HasUnpairedSurrogate(string value) => !ReferenceEquals(WellFormed(value), value);

    /// <summary>Keys sorted ordinally, so equal counts always serialise to the same text whatever the dictionary's order or the culture.</summary>
    private static string WriteCounts(IReadOnlyDictionary<string, int>? counts)
    {
        var sorted = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, count) in counts ?? new Dictionary<string, int>())
        {
            sorted[key] = count;
        }

        return JsonSerializer.Serialize(sorted);
    }

    private static string WriteList(IReadOnlyList<string>? values) => JsonSerializer.Serialize(values ?? []);

    /// <summary>Exact names only: <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> alone would also accept <c>"2"</c> or <c>" Blocked"</c>.</summary>
    private static TEnum ParseName<TEnum>(AuditEntryRecord record, string value, string column)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            return parsed;
        }

        throw Unreadable(record, column, inner: null);
    }

    private static T Read<T>(AuditEntryRecord record, string json, string column)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json) ?? throw Unreadable(record, column, inner: null);
        }
        catch (JsonException exception)
        {
            throw Unreadable(record, column, exception);
        }
    }

    private static InvalidDataException Unreadable(AuditEntryRecord record, string column, Exception? inner) => new(
        $"Audit entry {record.Sequence} of tenant '{record.TenantId}' has an unreadable {column}; run chain verification.",
        inner);
}
