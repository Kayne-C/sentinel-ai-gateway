using Microsoft.Extensions.Options;
using Sentinel.Application.Common;

namespace Sentinel.Infrastructure.IntegrationTests.CostControls;

/// <summary>A fixed options value; the implementations under test read <see cref="CurrentValue"/> per call.</summary>
internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Deterministic embeddings with exactly controlled cosine similarity.</summary>
internal static class TestVectors
{
    public static float[] Random(int seed)
    {
        var random = new Random(seed);
        var vector = new float[EmbeddingDefaults.Dimensions];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2 - 1);
        }

        return Normalize(vector);
    }

    /// <summary>A unit vector whose cosine similarity to <paramref name="unit"/> is <paramref name="similarity"/>.</summary>
    public static float[] WithSimilarity(float[] unit, double similarity, int seed)
    {
        // Gram-Schmidt: a random direction orthogonal to the reference, then rotate by acos(similarity).
        var other = Random(seed);
        var projection = Dot(other, unit);
        var orthogonal = new float[unit.Length];
        for (var i = 0; i < unit.Length; i++)
        {
            orthogonal[i] = (float)(other[i] - projection * unit[i]);
        }

        orthogonal = Normalize(orthogonal);
        var sine = Math.Sqrt(1 - similarity * similarity);
        var result = new float[unit.Length];
        for (var i = 0; i < unit.Length; i++)
        {
            result[i] = (float)(similarity * unit[i] + sine * orthogonal[i]);
        }

        return result;
    }

    public static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }

        return sum;
    }

    private static float[] Normalize(float[] vector)
    {
        var norm = Math.Sqrt(Dot(vector, vector));
        return [.. vector.Select(v => (float)(v / norm))];
    }
}
