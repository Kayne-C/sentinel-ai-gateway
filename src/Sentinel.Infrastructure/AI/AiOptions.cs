namespace Sentinel.Infrastructure.AI;

public enum AiProvider
{
    /// <summary>
    /// Deterministic, network-free models (feature-hashing embeddings, extractive answers) for development and tests.
    /// Nothing leaves the process.
    /// </summary>
    Offline,

    /// <summary>Any OpenAI-compatible endpoint: OpenAI itself, Ollama (<c>http://host:11434/v1</c>), vLLM, LiteLLM...</summary>
    OpenAICompatible,

    /// <summary>
    /// Azure OpenAI / Azure AI Foundry through the v1 API (<c>https://&lt;resource&gt;.openai.azure.com/openai/v1/</c>)
    /// with an API key; tier models are deployment names. Entra ID authentication for the provider is out of scope.
    /// </summary>
    AzureOpenAI,
}

/// <summary>Model provider configuration (section <c>Ai</c>). Secrets belong in a secret store, never in appsettings.</summary>
public sealed class AiOptions
{
    public const string Section = "Ai";
    public const string DefaultEmbeddingModel = "all-minilm:l6-v2";

    public AiProvider Provider { get; set; } = AiProvider.Offline;

    /// <summary>
    /// Provider base address. OpenAI-compatible: the URL that precedes <c>/chat/completions</c> (for Ollama
    /// <c>http://localhost:11434/v1</c>; empty means api.openai.com). Azure: the resource root or its <c>/openai/v1/</c> URL.
    /// </summary>
    public Uri? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    public string EmbeddingModel { get; set; } = DefaultEmbeddingModel;

    /// <summary>
    /// Send <c>dimensions = 384</c> with embedding requests: required for OpenAI/Azure <c>text-embedding-3-*</c> (which
    /// can shorten their vectors), wrong for models with a fixed width such as Ollama's <c>all-minilm</c>.
    /// </summary>
    public bool SendEmbeddingDimensions { get; set; }

    /// <summary>Timeout of a single provider attempt (completions are slow; the HTTP default of 10 s is not enough).</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Upper bound for a provider call including retries.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(180);

    public ContentSafetyOptions ContentSafety { get; set; } = new();
}

/// <summary>Azure AI Content Safety Prompt Shields, run in addition to the local injection detector.</summary>
public sealed class ContentSafetyOptions
{
    public bool Enabled { get; set; }

    /// <summary>The Content Safety resource, e.g. <c>https://&lt;name&gt;.cognitiveservices.azure.com/</c>.</summary>
    public Uri? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>
    /// Treat content as an attack when the shield cannot be reached. Off by default: the local detector still runs,
    /// and an outage of an optional second opinion should not take the gateway down.
    /// </summary>
    public bool FailClosed { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
}
