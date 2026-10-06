using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Knowledge;

/// <summary>
/// Vector search for providers without a usable native vector function (SQLite for development and API tests,
/// Oracle until its AI Vector Search is wired up). One SQL query streams the tenant's authorised, non-quarantined
/// chunks - the same tenant and ACL predicates as on SQL Server, so unauthorised rows never leave the database - and
/// cosine similarity is computed in process with a bounded top-k heap.
/// </summary>
/// <remarks>
/// Development scale only: every authorised chunk of the tenant is read on every query (O(n) I/O, O(k) memory).
/// </remarks>
internal sealed class InProcessVectorSearch(SentinelDbContext db) : IVectorSearch
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(VectorQuery query, CancellationToken cancellationToken)
    {
        var top = VectorQueryGuard.EffectiveTop(query);
        if (top == 0)
        {
            return [];
        }

        var tenantId = query.TenantId;
        var principals = query.Principals.ToArray();
        var queryVector = query.Embedding.ToArray();
        var queryNorm = EmbeddingCodec.Norm(queryVector);

        var candidates = (
                from chunk in db.Set<ChunkRecord>().AsNoTracking()
                join document in db.Set<DocumentRecord>().AsNoTracking() on chunk.DocumentId equals document.Id
                where chunk.TenantId == tenantId
                      && document.TenantId == tenantId
                      && !chunk.Quarantined
                      && db.Set<DocumentPrincipalRecord>().Any(p =>
                          p.DocumentId == chunk.DocumentId && p.TenantId == tenantId && principals.Contains(p.Principal))
                select new
                {
                    document.Id,
                    document.ExternalId,
                    document.Version,
                    document.Title,
                    document.Classification,
                    chunk.Ordinal,
                    chunk.Text,
                    chunk.EmbeddingBytes,
                })
            .AsAsyncEnumerable();

        // Min-heap whose root is the worst of the current best k.
        var best = new PriorityQueue<RetrievedChunk, RetrievedChunk>(top + 1, RankComparer.WorstFirst);
        await foreach (var candidate in candidates.WithCancellation(cancellationToken))
        {
            var similarity = EmbeddingCodec.CosineSimilarity(queryVector, queryNorm, candidate.EmbeddingBytes);
            if (double.IsNaN(similarity) || similarity < query.MinSimilarity)
            {
                continue;
            }

            var chunk = new RetrievedChunk(
                candidate.Id, candidate.ExternalId, candidate.Version, candidate.Title, candidate.Classification,
                candidate.Ordinal, candidate.Text, similarity);

            if (best.Count < top)
            {
                best.Enqueue(chunk, chunk);
            }
            else if (RankComparer.WorstFirst.Compare(chunk, best.Peek()) > 0)
            {
                best.DequeueEnqueue(chunk, chunk);
            }
        }

        var results = new List<RetrievedChunk>(best.Count);
        while (best.TryDequeue(out var chunk, out _))
        {
            results.Add(chunk);
        }

        results.Reverse();
        return results;
    }

    /// <summary>Lower similarity sorts first; ties are broken by (document, ordinal) so results are deterministic.</summary>
    private sealed class RankComparer : IComparer<RetrievedChunk>
    {
        public static readonly RankComparer WorstFirst = new();

        public int Compare(RetrievedChunk? x, RetrievedChunk? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);

            var bySimilarity = x.Similarity.CompareTo(y.Similarity);
            if (bySimilarity != 0)
            {
                return bySimilarity;
            }

            var byDocument = y.DocumentId.CompareTo(x.DocumentId);
            return byDocument != 0 ? byDocument : y.Ordinal.CompareTo(x.Ordinal);
        }
    }
}
