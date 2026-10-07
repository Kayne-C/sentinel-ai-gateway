using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Guardrails.Injection;

namespace Sentinel.Infrastructure.AI.Learned;

/// <summary>Learned prompt-injection detector (section <c>Guardrails:Injection:Learned</c>).</summary>
public sealed class LearnedInjectionOptions
{
    public const string Section = "Guardrails:Injection:Learned";

    public bool Enabled { get; set; }

    /// <summary>Overrides the threshold the model was trained with (probability, 0-1). Null keeps the trained one.</summary>
    public double? Threshold { get; set; }

    /// <summary>Treat input as an attack when the embedding call fails. Off: the rule-based detector still runs.</summary>
    public bool FailClosed { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Second opinion next to the rule-based detector: it scores the embedding of the (already redacted) user text. It
/// catches paraphrased and indirect attacks that no pattern list can enumerate, at the price of one embedding call.
/// Only user-origin text is scored: the model was trained on prompts, and document chunks are long, mixed text on which
/// its error rates are unknown.
/// </summary>
internal sealed partial class LearnedInjectionDetector(
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    LearnedInjectionModel model,
    IOptions<LearnedInjectionOptions> options,
    ILogger<LearnedInjectionDetector> logger) : IPromptInjectionDetector
{
    internal const string DetectorName = "learned-embedding";
    internal const string Rule = "injection.learned";

    public async ValueTask<InjectionVerdict> InspectAsync(string text, ContentOrigin origin, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (origin != ContentOrigin.User || string.IsNullOrWhiteSpace(text))
        {
            return InjectionVerdict.Clean(DetectorName);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.Timeout);

            var input = text.Length > model.MaxCharacters ? text[..model.MaxCharacters] : text;
            var generated = await embeddings.GenerateAsync([input], cancellationToken: timeout.Token);
            var score = model.Score(generated.Single().Vector.Span);
            var attack = score >= (settings.Threshold ?? model.Threshold);
            return new InjectionVerdict(attack, score, attack ? [Rule] : [], DetectorName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogUnavailable(logger, exception.GetType().Name, settings.FailClosed);
            return settings.FailClosed
                ? new InjectionVerdict(true, 1, ["injection.learned.unavailable"], DetectorName)
                : InjectionVerdict.Clean(DetectorName);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The learned injection detector could not score the input ({ExceptionType}); failing closed: {FailClosed}")]
    private static partial void LogUnavailable(ILogger logger, string exceptionType, bool failClosed);
}
