namespace Sentinel.Infrastructure.AI;

/// <summary>Where the OpenAI-compatible proxy forwards to: the same endpoint the SDK-based clients use.</summary>
public static class OpenAiUpstream
{
    private static readonly Uri OpenAiDefault = new("https://api.openai.com/v1/");

    /// <summary>The <c>chat/completions</c> URL, or null when the provider has no HTTP upstream (offline mode).</summary>
    public static Uri? ChatCompletionsUri(AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Provider == AiProvider.Offline)
        {
            return null;
        }

        var baseUri = OpenAIAiBackend.ResolveEndpoint(options) ?? OpenAiDefault;
        var text = baseUri.AbsoluteUri;
        if (!text.EndsWith('/'))
        {
            text += "/";
        }

        return new Uri(new Uri(text, UriKind.Absolute), "chat/completions");
    }
}
