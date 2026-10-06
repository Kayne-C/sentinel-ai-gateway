namespace Sentinel.Application.Common;

public static class EmbeddingDefaults
{
    /// <summary>
    /// Fixed embedding width shared by the vector column, the semantic cache index and every provider:
    /// all-MiniLM-L6-v2 (Ollama <c>all-minilm</c>) natively, OpenAI/Azure <c>text-embedding-3-*</c> via the
    /// <c>dimensions</c> parameter, and the offline hashing generator.
    /// </summary>
    public const int Dimensions = 384;
}
