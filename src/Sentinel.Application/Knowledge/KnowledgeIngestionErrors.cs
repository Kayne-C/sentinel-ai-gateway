using Sentinel.Application.Features.Ask;
using Sentinel.Domain.Common;

namespace Sentinel.Application.Knowledge;

public static class KnowledgeIngestionErrors
{
    public static readonly Error EmbeddingUnavailable =
        Error.Unavailable(AskErrors.ModelUnavailable, "The embedding model is unavailable; the document was not changed.");

    /// <summary>
    /// A vector of the wrong width (or with non-finite / all-zero values) would either be rejected by the
    /// <c>vector(384)</c> column or silently poison similarity ranking, so the whole upsert is refused.
    /// </summary>
    public static readonly Error InvalidEmbedding =
        Error.Unavailable("Model.InvalidEmbedding", "The embedding model returned an unusable vector; the document was not changed.");

    public static Error TooManyChunks(int maxChunks) =>
        Error.BusinessRule("Knowledge.TooManyChunks", $"The document is too large: it would need more than {maxChunks} chunks.");

    public static Error ConcurrentModification(string externalId) =>
        Error.Conflict("Knowledge.Conflict", $"Document '{externalId}' was modified concurrently; reload and retry.");
}
