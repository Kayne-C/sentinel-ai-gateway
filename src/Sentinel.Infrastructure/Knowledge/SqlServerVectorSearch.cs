using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Knowledge;

/// <summary>
/// Exact k-nearest-neighbour search with SQL Server 2025's native <c>vector</c> type: one statement that applies the
/// tenant, quarantine and ACL predicates and ranks only the surviving chunks by <c>VECTOR_DISTANCE('cosine', ...)</c>.
/// </summary>
/// <remarks>
/// We deliberately do not use an approximate (DiskANN) vector index and <c>VECTOR_SEARCH</c> here. Approximate
/// search finds the k nearest candidates first and applies filters afterwards: with a selective ACL it can return
/// fewer than k authorised chunks (or none) even though enough exist, and the ranking work over chunks the caller
/// may not read leaks information through result counts and latency. Exact search over the authorised set is
/// correct by construction - an unauthorised chunk is never ranked - and a per-tenant scan of a few hundred thousand
/// 384-dimensional vectors is fast enough. Revisit with a post-filter-aware ANN design only when a single tenant
/// outgrows that.
/// </remarks>
internal sealed class SqlServerVectorSearch(SentinelDbContext db) : IVectorSearch
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
        var queryVector = new SqlVector<float>(query.Embedding.ToArray());

        var rows = await (
                from chunk in db.Set<ChunkRecord>().AsNoTracking()
                join document in db.Set<DocumentRecord>().AsNoTracking() on chunk.DocumentId equals document.Id
                where chunk.TenantId == tenantId
                      && document.TenantId == tenantId
                      && !chunk.Quarantined
                      && db.Set<DocumentPrincipalRecord>().Any(p =>
                          p.DocumentId == chunk.DocumentId && p.TenantId == tenantId && principals.Contains(p.Principal))
                orderby EF.Functions.VectorDistance("cosine", chunk.Vector, queryVector), chunk.DocumentId, chunk.Ordinal
                select new
                {
                    document.Id,
                    document.ExternalId,
                    document.Version,
                    document.Title,
                    document.Classification,
                    chunk.Ordinal,
                    chunk.Text,
                    Distance = EF.Functions.VectorDistance("cosine", chunk.Vector, queryVector),
                })
            .Take(top)
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new RetrievedChunk(r.Id, r.ExternalId, r.Version, r.Title, r.Classification, r.Ordinal, r.Text, 1 - r.Distance))
            .Where(r => r.Similarity >= query.MinSimilarity)
            .ToList();
    }
}
