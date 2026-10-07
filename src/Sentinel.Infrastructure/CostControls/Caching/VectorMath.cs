using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sentinel.Application.Common;

namespace Sentinel.Infrastructure.CostControls.Caching;

internal static class VectorMath
{
    /// <summary>The wire format of a Redis FLOAT32 vector: little-endian IEEE 754, whatever the host's byte order.</summary>
    public static byte[] ToLittleEndianBytes(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(vector).CopyTo(bytes);
        }
        else
        {
            for (var i = 0; i < vector.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
            }
        }

        return bytes;
    }

    /// <summary>
    /// Unit-length copy, or false for vectors without a direction (all zeros) or with NaN/∞ components: cosine
    /// similarity is undefined for them, and a NaN similarity would slip past a "below threshold" comparison.
    /// </summary>
    public static bool TryNormalize(ReadOnlySpan<float> vector, out float[] normalized)
    {
        double sumOfSquares = 0;
        foreach (var component in vector)
        {
            if (!float.IsFinite(component))
            {
                normalized = [];
                return false;
            }

            sumOfSquares += (double)component * component;
        }

        var norm = Math.Sqrt(sumOfSquares);
        if (norm == 0 || !double.IsFinite(norm))
        {
            normalized = [];
            return false;
        }

        normalized = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            normalized[i] = (float)(vector[i] / norm);
        }

        return true;
    }

    /// <summary>Cosine similarity of two unit vectors, clamped to [-1, 1] against rounding.</summary>
    public static double CosineOfUnitVectors(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
        }

        return Math.Clamp(dot, -1, 1);
    }

    /// <summary>Every embedding in the system has the same width; anything else is a configuration error upstream.</summary>
    public static void EnsureDimensions(ReadOnlyMemory<float> embedding, string parameterName)
    {
        if (embedding.Length != EmbeddingDefaults.Dimensions)
        {
            throw new ArgumentException(
                $"Embeddings must have {EmbeddingDefaults.Dimensions} dimensions, got {embedding.Length}.", parameterName);
        }
    }
}
