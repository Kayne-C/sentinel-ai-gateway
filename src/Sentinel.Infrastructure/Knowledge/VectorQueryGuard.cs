using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;

namespace Sentinel.Infrastructure.Knowledge;

internal static class VectorQueryGuard
{
    /// <summary>Upper bound for k, whatever the caller asks for: retrieval is context for one prompt, not export.</summary>
    public const int MaxTop = 100;

    /// <summary>
    /// Validates the query and returns the effective k; 0 means "nothing can match" (no principals, k &lt;= 0, or a
    /// query vector without direction), which both implementations answer without touching the database.
    /// </summary>
    public static int EffectiveTop(VectorQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.TenantId, nameof(query));

        var embedding = query.Embedding.Span;
        if (embedding.Length != EmbeddingDefaults.Dimensions)
        {
            throw new ArgumentException(
                $"Query embedding has {embedding.Length} dimensions; {EmbeddingDefaults.Dimensions} expected.", nameof(query));
        }

        foreach (var value in embedding)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentException("Query embedding contains non-finite values.", nameof(query));
            }
        }

        if (query.Principals.Count == 0 || query.Top <= 0 || EmbeddingCodec.Norm(embedding) == 0)
        {
            return 0;
        }

        return Math.Min(query.Top, MaxTop);
    }
}
