using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;

namespace Sentinel.Infrastructure.IntegrationTests.AI;

/// <summary>
/// Optional end-to-end check against a local Ollama (OpenAI-compatible API) through the production wiring. Skips when
/// nothing listens on localhost:11434 or the models are missing, so CI without Ollama stays green.
/// </summary>
public sealed class OllamaSmokeTests
{
    private const string Endpoint = "http://localhost:11434";

    [Fact]
    public async Task Chat_and_384_dimensional_embeddings_work_against_a_local_ollama()
    {
        await SkipUnlessOllamaIsRunningAsync();

        using var services = AiServiceRegistrationTests.Build(new()
        {
            ["Ai:Provider"] = "OpenAICompatible",
            ["Ai:Endpoint"] = Endpoint + "/v1",
            ["Ai:EmbeddingModel"] = "all-minilm:l6-v2",
        });

        var client = services.GetRequiredService<IChatClientProvider>().GetClient(ModelCatalogOptions.FastTier);
        var response = await client.GetResponseAsync(
            "Reply with the single word: pong",
            new ChatOptions { MaxOutputTokens = 16, Temperature = 0f },
            TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.True(response.Usage?.InputTokenCount > 0);

        var embedding = await services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()
            .GenerateVectorAsync("Yıllık izin kaç gün?", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(EmbeddingDefaults.Dimensions, embedding.Length);
    }

    private static async Task SkipUnlessOllamaIsRunningAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        string tags;
        try
        {
            tags = await http.GetStringAsync(new Uri(Endpoint + "/api/tags"), TestContext.Current.CancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Assert.Skip($"Ollama is not reachable at {Endpoint} ({exception.GetType().Name}).");
            return;
        }

        if (!tags.Contains("qwen2.5:0.5b", StringComparison.Ordinal) || !tags.Contains("all-minilm", StringComparison.Ordinal))
        {
            Assert.Skip("Ollama is running but qwen2.5:0.5b or all-minilm is not pulled.");
        }
    }
}
