using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Application.Knowledge;
using Sentinel.Domain.Knowledge;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Knowledge;

/// <summary>
/// EF Core persistence of documents, ACLs and chunks. Every write runs in one transaction inside the context's
/// execution strategy (SQL Server retries transient faults; a user transaction is only allowed inside it), so a
/// reader sees either the old document with its old chunks or the new one with its new chunks.
/// </summary>
internal sealed partial class KnowledgeRepository(SentinelDbContext db, ILogger<KnowledgeRepository> logger)
    : IKnowledgeRepository, IDocumentChunkCounter
{
    private const int MaxPageSize = 200;

    public async Task<KnowledgeDocument?> FindAsync(string tenantId, string externalId, CancellationToken cancellationToken)
    {
        var record = await db.Set<DocumentRecord>()
            .AsNoTracking()
            .Include(d => d.Principals)
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.ExternalId == externalId, cancellationToken);

        return record is null
            ? null
            : KnowledgeDocument.Restore(
                record.Id, record.TenantId, record.ExternalId, record.Title, record.SourceUri, record.Classification,
                record.Version, record.ContentHash, record.Principals.Select(p => p.Principal), record.CreatedAtUtc, record.UpdatedAtUtc);
    }

    /// <remarks>
    /// Optimistic concurrency: the stored version must be older than <paramref name="document"/>'s, and the update
    /// is conditional on the version read in the same transaction. Two revisions based on the same version cannot
    /// both be saved; the loser gets <see cref="KnowledgeConcurrencyException"/> (it would otherwise silently undo
    /// the winner's ACL change).
    /// </remarks>
    public async Task SaveAsync(KnowledgeDocument document, IReadOnlyList<EmbeddedChunk>? chunks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (chunks is not null)
        {
            foreach (var chunk in chunks)
            {
                EnsureStorable(chunk);
            }
        }

        var inserted = false;
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(
                async ct =>
                {
                    // A retried attempt must not see entities the failed attempt left in the change tracker.
                    DetachKnowledgeEntities();
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);

                    var record = await db.Set<DocumentRecord>()
                        .Include(d => d.Principals)
                        .SingleOrDefaultAsync(d => d.Id == document.Id && d.TenantId == document.TenantId, ct);

                    inserted = record is null;
                    if (record is null)
                    {
                        record = new DocumentRecord { Id = document.Id, TenantId = document.TenantId, ExternalId = document.ExternalId };
                        Apply(document, record);
                        record.Principals = [.. document.Principals.Select(p => NewPrincipal(document, p))];
                        db.Add(record);
                    }
                    else
                    {
                        if (record.Version == document.Version && IsSameRevision(document, record))
                        {
                            // Already stored: a retry whose earlier commit succeeded but was reported as a transient
                            // failure, or an identical concurrent write. Nothing to do; failing would be a false conflict.
                            return;
                        }

                        if (record.Version >= document.Version)
                        {
                            throw new KnowledgeConcurrencyException(
                                $"Document {document.Id} is already at version {record.Version}; refusing to save version {document.Version}.");
                        }

                        Apply(document, record);
                        SynchronisePrincipals(document, record);
                    }

                    if (chunks is not null)
                    {
                        if (!inserted)
                        {
                            await db.Set<ChunkRecord>().Where(c => c.DocumentId == document.Id).ExecuteDeleteAsync(ct);
                        }

                        db.AddRange(chunks.Select(c => NewChunk(document, c)));
                    }

                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                },
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new KnowledgeConcurrencyException($"Document {document.Id} was modified concurrently.", exception);
        }
        catch (DbUpdateException exception) when (inserted && SentinelDbContext.IsConcurrencyFailure(exception))
        {
            // Two creates of the same external id raced; the unique (TenantId, ExternalId) index decided.
            throw new KnowledgeConcurrencyException($"Document '{document.ExternalId}' was created concurrently.", exception);
        }
        finally
        {
            DetachKnowledgeEntities();
        }

        LogDocumentSaved(logger, document.Id, document.Version, chunks?.Count);
    }

    public async Task<bool> DeleteAsync(string tenantId, string externalId, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(
                async ct =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    var documentId = await db.Set<DocumentRecord>()
                        .Where(d => d.TenantId == tenantId && d.ExternalId == externalId)
                        .Select(d => (Guid?)d.Id)
                        .SingleOrDefaultAsync(ct);

                    if (documentId is not { } id)
                    {
                        return false;
                    }

                    // Explicit child deletes (the schema also cascades) keep this correct on a provider or connection
                    // where foreign keys are not enforced.
                    await db.Set<ChunkRecord>().Where(c => c.DocumentId == id).ExecuteDeleteAsync(ct);
                    await db.Set<DocumentPrincipalRecord>().Where(p => p.DocumentId == id).ExecuteDeleteAsync(ct);
                    await db.Set<DocumentRecord>().Where(d => d.Id == id).ExecuteDeleteAsync(ct);
                    await transaction.CommitAsync(ct);
                    return true;
                },
                cancellationToken);
        }
        finally
        {
            DetachKnowledgeEntities();
        }
    }

    public async Task<PagedResponse<DocumentSummary>> ListAsync(
        string tenantId, IReadOnlyCollection<string>? principals, int page, int pageSize, CancellationToken cancellationToken)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Clamp(page, 1, int.MaxValue / pageSize);

        var documents = db.Set<DocumentRecord>().AsNoTracking().Where(d => d.TenantId == tenantId);
        if (principals is not null)
        {
            var granted = principals.ToArray();
            if (granted.Length == 0)
            {
                return new PagedResponse<DocumentSummary>([], page, pageSize, 0);
            }

            documents = documents.Where(d => db.Set<DocumentPrincipalRecord>()
                .Any(p => p.DocumentId == d.Id && p.TenantId == tenantId && granted.Contains(p.Principal)));
        }

        var total = await documents.LongCountAsync(cancellationToken);
        var rows = await documents
            .OrderByDescending(d => d.UpdatedAtUtc)
            .ThenBy(d => d.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                d.Id,
                d.ExternalId,
                d.Title,
                d.Classification,
                d.Version,
                d.UpdatedAtUtc,
                ChunkCount = d.Chunks.Count,
                QuarantinedChunkCount = d.Chunks.Count(c => c.Quarantined),
                Principals = d.Principals.Select(p => p.Principal).ToList(),
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new DocumentSummary(
                r.Id, r.ExternalId, r.Title, r.Classification, r.Version, r.ChunkCount, r.QuarantinedChunkCount,
                [.. r.Principals.Order(StringComparer.Ordinal)], r.UpdatedAtUtc))
            .ToList();

        return new PagedResponse<DocumentSummary>(items, page, pageSize, total);
    }

    public async Task<DocumentChunkCounts> CountChunksAsync(string tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        var chunks = db.Set<ChunkRecord>().AsNoTracking().Where(c => c.TenantId == tenantId && c.DocumentId == documentId);
        var total = await chunks.CountAsync(cancellationToken);
        var quarantined = await chunks.CountAsync(c => c.Quarantined, cancellationToken);
        return new DocumentChunkCounts(total, quarantined);
    }

    /// <summary>
    /// The database is the last line of defence for ranking integrity: a wrong-width, non-finite or all-zero vector
    /// would make cosine distance fail or be undefined for every query of the tenant, so it is never written.
    /// </summary>
    private static void EnsureStorable(EmbeddedChunk chunk)
    {
        var vector = chunk.Embedding.Span;
        if (vector.Length != EmbeddingDefaults.Dimensions)
        {
            throw new ArgumentException(
                $"Chunk {chunk.Chunk.Ordinal} has an embedding of length {vector.Length}; {EmbeddingDefaults.Dimensions} expected.",
                nameof(chunk));
        }

        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentException($"Chunk {chunk.Chunk.Ordinal} has a non-finite embedding value.", nameof(chunk));
            }
        }

        if (EmbeddingCodec.Norm(vector) == 0)
        {
            throw new ArgumentException($"Chunk {chunk.Chunk.Ordinal} has an all-zero embedding.", nameof(chunk));
        }
    }

    private static void Apply(KnowledgeDocument document, DocumentRecord record)
    {
        record.Title = document.Title;
        record.SourceUri = document.SourceUri;
        record.Classification = document.Classification;
        record.Version = document.Version;
        record.ContentHash = document.ContentHash;
        record.CreatedAtUtc = document.CreatedAtUtc;
        record.UpdatedAtUtc = document.UpdatedAtUtc;
    }

    private static bool IsSameRevision(KnowledgeDocument document, DocumentRecord record) =>
        record.ContentHash == document.ContentHash
        && record.Title == document.Title
        && record.SourceUri == document.SourceUri
        && record.Classification == document.Classification
        && document.Principals.SetEquals(record.Principals.Select(p => p.Principal));

    private void SynchronisePrincipals(KnowledgeDocument document, DocumentRecord record)
    {
        foreach (var stale in record.Principals.Where(p => !document.Principals.Contains(p.Principal)).ToList())
        {
            record.Principals.Remove(stale);
            db.Remove(stale);
        }

        var existing = record.Principals.Select(p => p.Principal).ToHashSet(StringComparer.Ordinal);
        foreach (var principal in document.Principals.Where(p => !existing.Contains(p)))
        {
            record.Principals.Add(NewPrincipal(document, principal));
        }
    }

    private static DocumentPrincipalRecord NewPrincipal(KnowledgeDocument document, string principal) =>
        new() { DocumentId = document.Id, TenantId = document.TenantId, Principal = principal };

    private ChunkRecord NewChunk(KnowledgeDocument document, EmbeddedChunk embedded)
    {
        var record = new ChunkRecord
        {
            DocumentId = document.Id,
            TenantId = document.TenantId,
            Ordinal = embedded.Chunk.Ordinal,
            Text = embedded.Chunk.Text,
            TokenCount = embedded.Chunk.TokenCount,
            Quarantined = embedded.Chunk.Quarantined,
            QuarantineReason = embedded.Chunk.QuarantineReason,
        };

        if (db.Provider == DatabaseProvider.SqlServer)
        {
            record.Vector = new SqlVector<float>(embedded.Embedding.ToArray());
        }
        else
        {
            record.EmbeddingBytes = EmbeddingCodec.Encode(embedded.Embedding.Span);
        }

        return record;
    }

    /// <summary>
    /// Forgets this module's entities only: the scoped context is shared with other modules whose tracked state is
    /// not ours to discard.
    /// </summary>
    private void DetachKnowledgeEntities()
    {
        var entries = db.ChangeTracker.Entries()
            .Where(e => e.Entity is DocumentRecord or DocumentPrincipalRecord or ChunkRecord)
            .ToList();

        foreach (var entry in entries)
        {
            entry.State = EntityState.Detached;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Saved document {DocumentId} version {Version} (chunks replaced: {ChunkCount})")]
    private static partial void LogDocumentSaved(ILogger logger, Guid documentId, int version, int? chunkCount);
}
