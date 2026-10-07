using Microsoft.Extensions.Options;

namespace Sentinel.Infrastructure.AI;

/// <summary>
/// Fails startup on configurations that would otherwise fail on the first request, or that would send credentials
/// somewhere they should not go (an API key over plain HTTP to a remote host).
/// </summary>
internal sealed class AiOptionsValidator : IValidateOptions<AiOptions>
{
    internal static readonly TimeSpan MaxAttemptTimeout = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan MaxTotalTimeout = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan MaxContentSafetyTimeout = TimeSpan.FromSeconds(30);

    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (options.AttemptTimeout <= TimeSpan.Zero || options.AttemptTimeout > MaxAttemptTimeout)
        {
            errors.Add($"Ai:AttemptTimeout must be greater than zero and at most {MaxAttemptTimeout}.");
        }

        if (options.TotalTimeout < options.AttemptTimeout || options.TotalTimeout > MaxTotalTimeout)
        {
            errors.Add($"Ai:TotalTimeout must be at least Ai:AttemptTimeout and at most {MaxTotalTimeout}.");
        }

        switch (options.Provider)
        {
            case AiProvider.Offline:
                break;

            case AiProvider.OpenAICompatible:
                if (options.Endpoint is null)
                {
                    if (string.IsNullOrWhiteSpace(options.ApiKey))
                    {
                        errors.Add("Ai:ApiKey is required when Ai:Endpoint is empty (api.openai.com).");
                    }
                }
                else if (!IsHttp(options.Endpoint))
                {
                    errors.Add("Ai:Endpoint must be an absolute http(s) URL, e.g. http://localhost:11434/v1.");
                }
                else if (options.Endpoint.Scheme == Uri.UriSchemeHttp && !string.IsNullOrWhiteSpace(options.ApiKey) && !options.Endpoint.IsLoopback)
                {
                    errors.Add("Ai:Endpoint must use https when an API key is configured for a non-local host.");
                }

                RequireEmbeddingModel(options, errors);
                break;

            case AiProvider.AzureOpenAI:
                if (options.Endpoint is null || !options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps)
                {
                    errors.Add("Ai:Endpoint must be the https endpoint of the Azure OpenAI resource, e.g. https://<resource>.openai.azure.com/openai/v1/.");
                }
                else if (!IsAzureV1Path(options.Endpoint))
                {
                    errors.Add("Ai:Endpoint must be the resource root or its v1 API URL (https://<resource>.openai.azure.com/openai/v1/); deployment-scoped URLs are not supported.");
                }

                if (string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    errors.Add("Ai:ApiKey is required for Azure OpenAI.");
                }

                RequireEmbeddingModel(options, errors);
                break;

            default:
                errors.Add($"Ai:Provider '{options.Provider}' is not supported.");
                break;
        }

        if (HasControlCharacters(options.ApiKey))
        {
            errors.Add("Ai:ApiKey contains control characters (a stray line break from a secret store?).");
        }

        var safety = options.ContentSafety;
        if (safety.Enabled)
        {
            if (HasControlCharacters(safety.ApiKey))
            {
                errors.Add("Ai:ContentSafety:ApiKey contains control characters (a stray line break from a secret store?).");
            }

            if (safety.Endpoint is null || !safety.Endpoint.IsAbsoluteUri || safety.Endpoint.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add("Ai:ContentSafety:Endpoint must be the https endpoint of the Content Safety resource.");
            }

            if (string.IsNullOrWhiteSpace(safety.ApiKey))
            {
                errors.Add("Ai:ContentSafety:ApiKey is required when Prompt Shields are enabled.");
            }

            if (safety.Timeout <= TimeSpan.Zero || safety.Timeout > MaxContentSafetyTimeout)
            {
                errors.Add($"Ai:ContentSafety:Timeout must be greater than zero and at most {MaxContentSafetyTimeout}.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>The resource root (we append <c>openai/v1/</c>) or the v1 API path itself.</summary>
    internal static bool IsAzureV1Path(Uri endpoint)
    {
        var path = endpoint.AbsolutePath.TrimEnd('/');
        return path.Length == 0 || path.Equals("/openai/v1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Header values cannot carry them; better to fail at startup than on the first request.</summary>
    private static bool HasControlCharacters(string? value) => value is not null && value.Any(char.IsControl);

    private static bool IsHttp(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static void RequireEmbeddingModel(AiOptions options, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(options.EmbeddingModel))
        {
            errors.Add("Ai:EmbeddingModel is required.");
        }
    }
}
