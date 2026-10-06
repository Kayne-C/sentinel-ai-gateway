using System.Buffers.Binary;

namespace Sentinel.Infrastructure.Knowledge;

/// <summary>Portable float32 little-endian encoding for providers without a native vector type.</summary>
internal static class EmbeddingCodec
{
    public static byte[] Encode(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    public static float[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new ArgumentException("Embedding byte length is not a multiple of 4.", nameof(bytes));
        }

        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * sizeof(float))..]);
        }

        return vector;
    }

    /// <summary>
    /// Cosine similarity of <paramref name="query"/> (with precomputed norm) against an encoded vector, without
    /// allocating. NaN when the stored vector has the wrong width or no direction.
    /// </summary>
    public static double CosineSimilarity(ReadOnlySpan<float> query, double queryNorm, ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != query.Length * sizeof(float) || queryNorm == 0)
        {
            return double.NaN;
        }

        double dot = 0, sumOfSquares = 0;
        for (var i = 0; i < query.Length; i++)
        {
            double value = BinaryPrimitives.ReadSingleLittleEndian(encoded[(i * sizeof(float))..]);
            dot += query[i] * value;
            sumOfSquares += value * value;
        }

        return sumOfSquares == 0 ? double.NaN : dot / (Math.Sqrt(sumOfSquares) * queryNorm);
    }

    public static double Norm(ReadOnlySpan<float> vector)
    {
        var sumOfSquares = 0d;
        foreach (var value in vector)
        {
            sumOfSquares += (double)value * value;
        }

        return Math.Sqrt(sumOfSquares);
    }
}
