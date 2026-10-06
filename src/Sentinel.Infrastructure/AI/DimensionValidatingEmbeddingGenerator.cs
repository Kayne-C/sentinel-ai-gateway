using Microsoft.Extensions.AI;
using Sentinel.Application.Common;

namespace Sentinel.Infrastructure.AI;

/// <summary>
/// The vector column, the cache index and every stored chunk are <see cref="EmbeddingDefaults.Dimensions"/> wide. A
/// misconfigured model (1536-dimensional <c>text-embedding-3-small</c> without the dimensions parameter, a different
/// Ollama model) would otherwise surface as an opaque database error, or worse, as silently empty search results. The
/// check runs on every response (it is a length comparison), so the first use fails with an actionable message.
/// </summary>
internal sealed class DimensionValidatingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, string modelDescription)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var embeddings = await base.GenerateAsync(values, options, cancellationToken);
        foreach (var embedding in embeddings)
        {
            if (embedding.Vector.Length != EmbeddingDefaults.Dimensions)
            {
                throw new InvalidOperationException(
                    $"The embedding model '{modelDescription}' returned {embedding.Vector.Length}-dimensional vectors, but Sentinel " +
                    $"stores {EmbeddingDefaults.Dimensions}-dimensional vectors. Use a {EmbeddingDefaults.Dimensions}-dimensional model " +
                    "(Ollama: all-minilm) or, for OpenAI/Azure text-embedding-3-*, set Ai:SendEmbeddingDimensions=true.");
            }
        }

        return embeddings;
    }
}
