using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using Sentinel.Application.Common;

namespace Sentinel.Infrastructure.AI;

/// <summary>
/// OpenAI-compatible endpoints and Azure OpenAI (v1 API) through the OpenAI SDK. The SDK runs on the
/// <c>llm-provider</c> HttpClient, so timeouts, retries and the circuit breaker live in one place (the standard
/// resilience handler); the SDK's own retry policy is switched off to avoid retry storms (SDK retries x handler retries).
/// </summary>
internal sealed class OpenAIAiBackend : IAiBackend
{
    public const string HttpClientName = "llm-provider";

    /// <summary>Keyless local servers (Ollama) still need a non-empty credential for the SDK.</summary>
    internal const string KeylessCredential = "no-key";

    private readonly OpenAIClient _client;
    private readonly string _embeddingModel;
    private readonly int? _embeddingDimensions;

    public OpenAIAiBackend(IHttpClientFactory httpClientFactory, IOptions<AiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);

        var ai = options.Value;
        Name = ai.Provider == AiProvider.AzureOpenAI ? "azure-openai" : "openai-compatible";
        _embeddingModel = ai.EmbeddingModel;
        _embeddingDimensions = ai.SendEmbeddingDimensions ? EmbeddingDefaults.Dimensions : null;

        var clientOptions = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(httpClientFactory.CreateClient(HttpClientName)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),

            // The resilience handler enforces the real limits; this only has to stay out of their way.
            NetworkTimeout = ai.TotalTimeout + TimeSpan.FromSeconds(10),
            ClientLoggingOptions = new ClientLoggingOptions { EnableMessageContentLogging = false },
            UserAgentApplicationId = "sentinel-gateway",
        };

        var endpoint = ResolveEndpoint(ai);
        if (endpoint is not null)
        {
            clientOptions.Endpoint = endpoint;
        }

        var apiKey = string.IsNullOrWhiteSpace(ai.ApiKey) ? KeylessCredential : ai.ApiKey;
        _client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
    }

    public string Name { get; }

    public IChatClient CreateChatClient(string model) => _client.GetChatClient(model).AsIChatClient();

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator() =>
        _client.GetEmbeddingClient(_embeddingModel).AsIEmbeddingGenerator(_embeddingDimensions);

    /// <summary>Azure accepts the resource root as a convenience; the SDK needs the v1 API base URL.</summary>
    internal static Uri? ResolveEndpoint(AiOptions options)
    {
        if (options.Endpoint is null || options.Provider != AiProvider.AzureOpenAI)
        {
            return options.Endpoint;
        }

        return new Uri(options.Endpoint.GetLeftPart(UriPartial.Authority) + "/openai/v1/");
    }
}
