using System.Text;
using Microsoft.Extensions.AI;
using Sentinel.Application.Common;

namespace Sentinel.Infrastructure.AI.Offline;

/// <summary>
/// Deterministic, network-free embeddings for development and tests: signed feature hashing (the "hashing trick")
/// of word unigrams, word bigrams and character trigrams into <see cref="EmbeddingDefaults.Dimensions"/> buckets,
/// L2-normalised so cosine similarity is a dot product. Lexical, not semantic: it ranks texts that share words (or
/// word stems, via trigrams, which matters for agglutinative Turkish) above unrelated ones, which is what RAG tests
/// need, and the same text always maps to the same vector on every machine.
/// </summary>
/// <remarks>
/// Hashing is FNV-1a 64-bit over UTF-8, followed by the MurmurHash3 finaliser: FNV's low bits are poorly mixed, and
/// the bucket index is taken from them. The top bit of the mixed hash picks the sign, so colliding features cancel
/// out on average instead of accumulating bias. Absolute cosine values are lower than those of neural models for a
/// short query against a long chunk; relative order is what is meaningful.
/// </remarks>
internal sealed class HashingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string ModelId = "sentinel-hashing-384";

    internal const float UnigramWeight = 1.0f;
    internal const float StopWordWeight = 0.15f;
    internal const float PrefixWeight = 0.8f;
    internal const float BigramWeight = 0.5f;
    internal const float TrigramWeight = 0.15f;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static readonly EmbeddingGeneratorMetadata Metadata = new("sentinel-offline", null, ModelId, EmbeddingDefaults.Dimensions);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (options?.Dimensions is { } dimensions && dimensions != EmbeddingDefaults.Dimensions)
        {
            throw new NotSupportedException($"The offline embedding generator only produces {EmbeddingDefaults.Dimensions}-dimensional vectors.");
        }

        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        long words = 0;
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            embeddings.Add(new Embedding<float>(Embed(value ?? string.Empty, out var count)) { ModelId = ModelId });
            words += count;
        }

        embeddings.Usage = new UsageDetails { InputTokenCount = words, TotalTokenCount = words };
        return Task.FromResult(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(EmbeddingGeneratorMetadata) ? Metadata : serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    internal static float[] Embed(string text, out int wordCount)
    {
        var words = OfflineText.Words(OfflineText.Fold(OfflineText.StripPlaceholders(text)));
        wordCount = words.Count;

        var features = new Dictionary<string, (float Weight, int Count)>(StringComparer.Ordinal);
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var stopWord = OfflineText.IsStopWord(word);
            AddFeature(features, "w:" + word, stopWord ? StopWordWeight : UnigramWeight);

            if (i > 0)
            {
                AddFeature(features, "b:" + words[i - 1] + " " + word, BigramWeight);
            }

            if (stopWord)
            {
                continue;
            }

            // Turkish (and, mostly, English) inflect by suffixing, so a shared 4- or 5-letter prefix is a cheap stem:
            // "izin", "izinler" and "izinlerimi" meet on "izin"; "sifre" and "sifrenizi" on "sifre".
            if (word.Length >= 4)
            {
                AddFeature(features, "p:" + word[..4], PrefixWeight);
            }

            if (word.Length >= 5)
            {
                AddFeature(features, "p:" + word[..5], PrefixWeight);
            }

            if (word.Length >= 2)
            {
                var padded = "^" + word + "$";
                for (var j = 0; j + 3 <= padded.Length; j++)
                {
                    AddFeature(features, "c:" + padded.Substring(j, 3), TrigramWeight);
                }
            }
        }

        if (features.Count == 0)
        {
            // Empty or symbol-only text still gets a unit vector, so cosine similarity is always defined.
            AddFeature(features, "e:", UnigramWeight);
        }

        var vector = new float[EmbeddingDefaults.Dimensions];
        foreach (var (feature, (weight, count)) in features)
        {
            var hash = Mix(Fnv1a64(feature));
            var index = (int)(hash % (ulong)vector.Length);
            var sign = (hash >> 63) == 0 ? 1f : -1f;

            // Sublinear term frequency: a word repeated ten times is more important, not ten times as important.
            // Square root rather than log: IEEE 754 requires sqrt to be correctly rounded, so vectors are bit-identical
            // on every platform (stored embeddings stay comparable), which libm's log does not guarantee.
            vector[index] += sign * weight * MathF.Sqrt(count);
        }

        Normalize(vector);
        return vector;
    }

    private static void AddFeature(Dictionary<string, (float Weight, int Count)> features, string feature, float weight)
    {
        features[feature] = features.TryGetValue(feature, out var existing) ? (existing.Weight, existing.Count + 1) : (weight, 1);
    }

    internal static ulong Fnv1a64(string value)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        Span<byte> bytes = maxBytes <= 512 ? stackalloc byte[maxBytes] : new byte[maxBytes];
        var length = Encoding.UTF8.GetBytes(value, bytes);

        var hash = FnvOffsetBasis;
        foreach (var b in bytes[..length])
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>MurmurHash3 fmix64.</summary>
    private static ulong Mix(ulong hash)
    {
        hash ^= hash >> 33;
        hash *= 0xff51afd7ed558ccdUL;
        hash ^= hash >> 33;
        hash *= 0xc4ceb9fe1a85ec53UL;
        hash ^= hash >> 33;
        return hash;
    }

    private static void Normalize(float[] vector)
    {
        var sumOfSquares = 0d;
        foreach (var value in vector)
        {
            sumOfSquares += value * (double)value;
        }

        if (sumOfSquares <= 0)
        {
            // All features cancelled out (vanishingly unlikely); fall back to a fixed unit vector.
            vector[0] = 1f;
            return;
        }

        var scale = (float)(1 / Math.Sqrt(sumOfSquares));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= scale;
        }
    }
}
