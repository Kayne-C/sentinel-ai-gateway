namespace Sentinel.Infrastructure.Audit;

/// <summary>
/// Persisted form of one audit entry. Every property mirrors its column exactly: enums and collections stay the
/// strings that were hashed, so verification hashes what the database holds instead of a re-parsed interpretation of
/// it (a tampered <c>"2"</c> in <see cref="Outcome"/> must never parse back to <c>Blocked</c> and hash correctly).
/// </summary>
internal sealed class AuditEntryRecord
{
    /// <summary>
    /// Surrogate key assigned by the database on insert. It cannot be part of the evidence (it does not exist when the
    /// hash is computed); the position in the chain is <see cref="Sequence"/>.
    /// </summary>
    public long Id { get; set; }

    public required string TenantId { get; set; }

    /// <summary>Position in the tenant's chain: 1..n, no gaps, no duplicates.</summary>
    public long Sequence { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public required string SubjectId { get; set; }

    /// <summary><see cref="Domain.Audit.AuditOperation"/> name.</summary>
    public required string Operation { get; set; }

    /// <summary><see cref="Domain.Audit.AuditOutcome"/> name.</summary>
    public required string Outcome { get; set; }

    public string? Model { get; set; }

    public string? ModelTier { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public decimal EstimatedCostUsd { get; set; }

    /// <summary>
    /// JSON object, keys sorted ordinally. The JSON columns are plain text rather than SQL Server 2025's <c>json</c>
    /// type on purpose: the hash covers these exact characters, and a <c>json</c> column may hand back a normalised
    /// rendering of the document.
    /// </summary>
    public required string RedactedPiiJson { get; set; }

    /// <summary>JSON array, in event order.</summary>
    public required string GuardrailFindingsJson { get; set; }

    /// <summary>JSON array, in event order.</summary>
    public required string SourcesJson { get; set; }

    public string? PromptDigest { get; set; }

    public string? RedactedPrompt { get; set; }

    public string? ErrorCode { get; set; }

    public double LatencyMs { get; set; }

    public string? TraceId { get; set; }

    public string? Subject { get; set; }

    /// <summary>Hash of entry <c>Sequence - 1</c>; null only for the first entry of a tenant.</summary>
    public string? PreviousHash { get; set; }

    public string Hash { get; set; } = string.Empty;

    /// <summary>A copy of this (normalised, not yet chained) entry placed at <paramref name="sequence"/>, hashed.</summary>
    public AuditEntryRecord ChainAt(long sequence, string? previousHash)
    {
        // MemberwiseClone keeps every column without a hand-written copy list that a new column could be missing from.
        var record = (AuditEntryRecord)MemberwiseClone();
        record.Id = 0;
        record.Sequence = sequence;
        record.PreviousHash = previousHash;
        record.Hash = AuditCanonicalForm.ComputeHash(record);
        return record;
    }
}
