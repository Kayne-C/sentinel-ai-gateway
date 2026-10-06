using Microsoft.Extensions.AI;

namespace Sentinel.Infrastructure.AI.Offline;

/// <summary>Everything in process: no endpoint, no key, no network, same answers on every run.</summary>
internal sealed class OfflineAiBackend(TimeProvider timeProvider) : IAiBackend
{
    /// <summary>
    /// Hashing cosines are on a lower scale than neural ones (a short query against a long chunk rarely exceeds 0.3),
    /// so with this backend the retrieval threshold defaults to this value unless <c>Rag:MinSimilarity</c> is set.
    /// </summary>
    public const double DefaultMinSimilarity = 0.1;

    public string Name => ExtractiveChatClient.ProviderName;

    public IChatClient CreateChatClient(string model) => new ExtractiveChatClient(model, timeProvider);

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator() => new HashingEmbeddingGenerator();
}
