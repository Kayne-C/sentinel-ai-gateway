namespace Sentinel.Gateway.Proxy;

/// <summary>OpenAI-compatible proxy settings (section <c>Proxy</c>). The upstream itself comes from <c>Ai</c>.</summary>
public sealed class OpenAiProxyOptions
{
    public const string Section = "Proxy";

    /// <summary>Largest request body accepted (messages are text only, so this is generous).</summary>
    public int MaxRequestBytes { get; set; } = 1024 * 1024;

    /// <summary>Largest non-streamed upstream response the gateway buffers in order to guard it.</summary>
    public int MaxResponseBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Name of the output-limit parameter sent upstream: <c>max_tokens</c> (Ollama, vLLM, Azure/OpenAI chat models) or
    /// <c>max_completion_tokens</c> (OpenAI/Azure reasoning models, which reject <c>max_tokens</c>).
    /// </summary>
    public string MaxTokensParameter { get; set; } = "max_tokens";

    /// <summary>No bytes from the upstream for this long (a hung model) aborts the call.</summary>
    public TimeSpan UpstreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(120);
}
