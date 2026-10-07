namespace Sentinel.Application.Knowledge;

/// <summary>
/// How documents are cut into retrievable chunks. Token counts come from the gateway's <c>ITokenCounter</c>, so the
/// budget arithmetic used for RAG context matches what is stored per chunk.
/// </summary>
public sealed class ChunkingOptions
{
    public const string Section = "Knowledge:Chunking";

    /// <summary>Soft upper bound per chunk; only a single word longer than this can exceed it (words are never split).</summary>
    public int TargetTokens { get; set; } = 300;

    /// <summary>Trailing sentences of the previous chunk repeated at the start of the next, up to this many tokens.</summary>
    public int OverlapTokens { get; set; } = 40;

    /// <summary>Documents that would need more chunks are rejected instead of silently truncated.</summary>
    public int MaxChunks { get; set; } = 2000;

    public static bool IsValid(ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.TargetTokens > 0
            && options.OverlapTokens >= 0
            && options.OverlapTokens < options.TargetTokens
            && options.MaxChunks > 0;
    }
}
