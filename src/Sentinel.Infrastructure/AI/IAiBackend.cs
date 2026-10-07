using Microsoft.Extensions.AI;

namespace Sentinel.Infrastructure.AI;

/// <summary>
/// Creates the raw provider clients. Telemetry, logging and dimension checks are layered on top by
/// <see cref="ChatClientProvider"/> and the embedding registration, so every backend gets them identically.
/// </summary>
internal interface IAiBackend
{
    string Name { get; }

    IChatClient CreateChatClient(string model);

    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator();
}
