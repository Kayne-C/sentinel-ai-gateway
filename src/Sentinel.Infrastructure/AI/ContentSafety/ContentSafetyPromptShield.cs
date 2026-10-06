using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Guardrails.Injection;

namespace Sentinel.Infrastructure.AI.ContentSafety;

/// <summary>
/// Azure AI Content Safety Prompt Shields as an <see cref="IPromptInjectionDetector"/>: user text is checked as a
/// direct attack (<c>userPrompt</c>), retrieved documents and tool output as indirect attacks (<c>documents</c>).
/// It only ever receives text that the prompt guard has already redacted. When the service cannot answer, the verdict
/// follows <see cref="ContentSafetyOptions.FailClosed"/>; by default it fails open because the local detector keeps
/// running in the composite and an optional second opinion should not be a single point of failure.
/// </summary>
/// <remarks>
/// The API limits a prompt (and the documents of one call) to 10 000 characters, so longer text is inspected in
/// overlapping windows; text beyond <see cref="MaxWindows"/> windows is reported as uninspected under fail-closed.
/// System text is the application's own instruction set, which Prompt Shields does not classify; it is not sent.
/// </remarks>
internal sealed partial class ContentSafetyPromptShield(
    HttpClient httpClient,
    IOptions<AiOptions> options,
    ILogger<ContentSafetyPromptShield> logger) : IPromptInjectionDetector
{
    public const string DetectorName = "azure-prompt-shields";
    public const string SubscriptionKeyHeader = "Ocp-Apim-Subscription-Key";
    public const string UserAttackRule = "prompt_shields.user_attack";
    public const string DocumentAttackRule = "prompt_shields.document_attack";
    public const string UnavailableRule = "prompt_shields.unavailable";
    public const string UninspectedRule = "prompt_shields.uninspected";

    internal const string RequestPath = "contentsafety/text:shieldPrompt?api-version=2024-09-01";
    internal const int MaxCharactersPerCall = 10_000;
    internal const int WindowOverlap = 500;
    internal const int MaxWindows = 10;

    public async ValueTask<InjectionVerdict> InspectAsync(string text, ContentOrigin origin, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || origin == ContentOrigin.System)
        {
            return InjectionVerdict.Clean(DetectorName);
        }

        var settings = options.Value.ContentSafety;
        var asDocument = origin is ContentOrigin.RetrievedDocument or ContentOrigin.ToolResult;
        var windows = Windows(text, out var truncated);

        foreach (var window in windows)
        {
            var outcome = await ShieldAsync(settings, window, asDocument, cancellationToken);
            if (outcome.Failure is { } reason)
            {
                LogShieldFailed(logger, reason, settings.FailClosed ? "attack" : "clean");
                return settings.FailClosed ? Attack(UnavailableRule) : InjectionVerdict.Clean(DetectorName);
            }

            if (outcome.AttackDetected)
            {
                return Attack(asDocument ? DocumentAttackRule : UserAttackRule);
            }
        }

        if (truncated)
        {
            LogShieldFailed(logger, "text-too-long", settings.FailClosed ? "attack" : "clean");
            if (settings.FailClosed)
            {
                return Attack(UninspectedRule);
            }
        }

        return InjectionVerdict.Clean(DetectorName);
    }

    private async Task<ShieldOutcome> ShieldAsync(ContentSafetyOptions settings, string text, bool asDocument, CancellationToken cancellationToken)
    {
        var body = asDocument ? new ShieldPromptRequest(string.Empty, [text]) : new ShieldPromptRequest(text, []);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, RequestUri(settings))
            {
                Content = JsonContent.Create(body, ShieldPromptJsonContext.Default.ShieldPromptRequest),
            };
            request.Headers.Add(SubscriptionKeyHeader, settings.ApiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ShieldOutcome.Failed($"http-{(int)response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync(ShieldPromptJsonContext.Default.ShieldPromptResponse, cancellationToken);
            if (result is null)
            {
                return ShieldOutcome.Failed("empty-response");
            }

            var attack = result.UserPromptAnalysis?.AttackDetected == true
                || result.DocumentsAnalysis?.Any(analysis => analysis.AttackDetected) == true;
            return new ShieldOutcome(attack, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ShieldOutcome.Failed("timeout");
        }
        catch (HttpRequestException exception)
        {
            return ShieldOutcome.Failed(exception.StatusCode is { } status ? $"http-{(int)status}" : "transport");
        }
        catch (JsonException)
        {
            return ShieldOutcome.Failed("invalid-response");
        }
        catch (NotSupportedException)
        {
            return ShieldOutcome.Failed("invalid-response");
        }
    }

    private static Uri RequestUri(ContentSafetyOptions settings)
    {
        var endpoint = settings.Endpoint ?? throw new InvalidOperationException("Ai:ContentSafety:Endpoint is not configured.");
        var root = endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/");
        return new Uri(root, RequestPath);
    }

    /// <summary>Windows of at most <see cref="MaxCharactersPerCall"/>, overlapping so an attack cannot hide in a seam.</summary>
    internal static List<string> Windows(string text, out bool truncated)
    {
        truncated = false;
        if (text.Length <= MaxCharactersPerCall)
        {
            return [text];
        }

        var windows = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            if (windows.Count == MaxWindows)
            {
                truncated = true;
                break;
            }

            var length = Math.Min(MaxCharactersPerCall, text.Length - start);

            // Never split a surrogate pair: half a character is invalid UTF-16 and gets the request rejected.
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
            {
                length--;
            }

            windows.Add(text.Substring(start, length));
            if (start + length >= text.Length)
            {
                break;
            }

            start += length - WindowOverlap;
            if (char.IsLowSurrogate(text[start]))
            {
                start--;
            }
        }

        return windows;
    }

    private static InjectionVerdict Attack(string rule) => new(true, 1, [rule], DetectorName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prompt Shields inspection failed ({Reason}); the content is treated as {Verdict}")]
    private static partial void LogShieldFailed(ILogger logger, string reason, string verdict);

    private readonly record struct ShieldOutcome(bool AttackDetected, string? Failure)
    {
        public static ShieldOutcome Failed(string reason) => new(false, reason);
    }
}
