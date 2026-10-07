namespace Sentinel.Application.Knowledge;

public readonly record struct DocumentChunkCounts(int Total, int Quarantined);

/// <summary>
/// Chunk statistics of one stored document. Needed when an upsert keeps the existing chunks (ACL or metadata-only
/// change, or no change at all) but must still report how many chunks the document has.
/// </summary>
public interface IDocumentChunkCounter
{
    Task<DocumentChunkCounts> CountChunksAsync(string tenantId, Guid documentId, CancellationToken cancellationToken);
}
