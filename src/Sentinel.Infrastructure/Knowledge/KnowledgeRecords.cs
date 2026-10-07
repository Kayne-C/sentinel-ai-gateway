using Microsoft.Data.SqlTypes;
using Sentinel.Domain.Knowledge;

namespace Sentinel.Infrastructure.Knowledge;

internal sealed class DocumentRecord
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string ExternalId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? SourceUri { get; set; }

    public Classification Classification { get; set; }

    /// <summary>Optimistic concurrency token: a save must not overwrite a version it has not seen.</summary>
    public int Version { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public List<DocumentPrincipalRecord> Principals { get; set; } = [];

    public List<ChunkRecord> Chunks { get; set; } = [];
}

/// <summary>
/// One ACL entry. The tenant id is denormalised so the ACL pre-filter can be answered from the
/// (TenantId, Principal, DocumentId) index alone.
/// </summary>
internal sealed class DocumentPrincipalRecord
{
    public Guid DocumentId { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Principal { get; set; } = string.Empty;
}

/// <summary>
/// A stored chunk. Exactly one of the two embedding properties is mapped, depending on the provider: SQL Server
/// 2025 stores a native <c>vector(384)</c>; SQLite stores little-endian float32 bytes.
/// </summary>
internal sealed class ChunkRecord
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public int Ordinal { get; set; }

    public string Text { get; set; } = string.Empty;

    public int TokenCount { get; set; }

    public bool Quarantined { get; set; }

    public string? QuarantineReason { get; set; }

    /// <summary>SQL Server only.</summary>
    public SqlVector<float> Vector { get; set; }

    /// <summary>SQLite only.</summary>
    public byte[]? EmbeddingBytes { get; set; }
}
