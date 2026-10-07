using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Diagnostics;
using Sentinel.Guardrails.Injection;
using Sentinel.Guardrails.Pii;

namespace Sentinel.Application.Guardrails;

/// <summary>
/// Applies redaction and injection detection identically for every entry point. Two rules shape the design:
/// <list type="bullet">
/// <item>Detectors only ever see redacted text. A detector may be a remote service (Azure Prompt Shields), and raw PII
/// must not leave the gateway for a classifier any more than for a model.</item>
/// <item>Text is canonicalised before anything else (<see cref="UntrustedText"/>): invisible and look-alike characters
/// would otherwise let PII slip past the redactor and attacks past the detector.</item>
/// <item>Sanitised messages are rebuilt from scratch. Provider adapters forward <c>RawRepresentation</c> objects
/// verbatim when present, so a redacted copy that kept the original raw message would still leak the unredacted text.</item>
/// </list>
/// </summary>
internal sealed partial class PromptGuard(
    IPiiRedactor redactor,
    OutputGuard outputGuard,
    IPromptInjectionDetector detector,
    IOptions<PiiOptions> piiOptions,
    IOptions<InjectionOptions> injectionOptions) : IPromptGuard
{
    internal const string DetectorName = "prompt-guard";

    /// <summary>Bounds concurrent calls when the detector is remote; chunk inspection is otherwise sequential latency.</summary>
    private const int MaxConcurrentContextInspections = 4;

    public async Task<GuardedInput> GuardInputAsync(IReadOnlyList<ChatMessage> messages, PiiVault vault, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(vault);

        var before = Snapshot(vault);
        var sanitized = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            sanitized.Add(SanitizeMessage(message, vault));
        }

        var redacted = OccurrencesSince(before, vault);
        RecordRedactions(redacted, "caller");

        var verdict = await InspectInputAsync(sanitized, cancellationToken);
        return new GuardedInput(sanitized, verdict, redacted);
    }

    public async Task<GuardedContext> GuardContextAsync(IReadOnlyList<RetrievedChunk> chunks, PiiVault vault, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(vault);

        var injection = injectionOptions.Value;
        var quarantined = new bool[chunks.Count];
        var inspectionCopies = Array.Empty<RetrievedChunk>();

        if (injection.Enabled && injection.InspectRetrievedContent && chunks.Count > 0)
        {
            // Inspection copies are redacted into a throwaway vault: the detector must not see raw PII, but chunks that
            // end up quarantined must not add entries (or audit counts) to the request's real vault either.
            var scratch = new PiiVault();
            inspectionCopies = [.. chunks.Select(chunk => chunk with
            {
                Text = Clean(chunk.Text, scratch, PiiOrigin.Context),
                DocumentTitle = Clean(chunk.DocumentTitle, scratch, PiiOrigin.Context),
            })];

            await Parallel.ForEachAsync(
                Enumerable.Range(0, chunks.Count),
                new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentContextInspections, CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    var copy = inspectionCopies[index];
                    var verdict = await detector.InspectAsync(copy.DocumentTitle + "\n" + copy.Text, ContentOrigin.RetrievedDocument, token);
                    quarantined[index] = IsAttack(verdict, injection);
                });
        }

        var before = Snapshot(vault);
        var safe = new List<RetrievedChunk>(chunks.Count);
        var dropped = new List<RetrievedChunk>();
        for (var i = 0; i < chunks.Count; i++)
        {
            if (quarantined[i])
            {
                // Only the redacted copy is handed back: callers may log or audit quarantined chunks.
                dropped.Add(inspectionCopies[i]);
                SentinelTelemetry.InjectionDetections.Add(1, Tag("origin", "document"), Tag("action", "quarantined"));
                continue;
            }

            var chunk = chunks[i];
            safe.Add(chunk with
            {
                Text = Clean(chunk.Text, vault, PiiOrigin.Context),
                DocumentTitle = Clean(chunk.DocumentTitle, vault, PiiOrigin.Context),
            });
        }

        var redacted = OccurrencesSince(before, vault);
        RecordRedactions(redacted, "context");
        return new GuardedContext(safe, dropped, redacted);
    }

    public string GuardOutput(string modelText, PiiVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (string.IsNullOrEmpty(modelText))
        {
            return string.Empty;
        }

        // Canonicalised like any untrusted text: invisible characters in an answer can carry smuggled data out, and
        // they would hide PII from the redactor.
        var canonical = UntrustedText.Canonicalize(modelText);
        var pii = piiOptions.Value;
        if (!pii.Enabled)
        {
            return canonical;
        }

        // Order matters: mask what the model produced first, then restore caller values, so a restored value is never
        // mistaken for "new" PII and re-masked, and new values can never be revealed (they are context-origin).
        var before = Snapshot(vault);
        var masked = redactor.Redact(canonical, vault, PiiOrigin.Context).Text;
        RecordRedactions(OccurrencesSince(before, vault), "output");

        if (!pii.RestoreCallerValuesInAnswers || vault.IsEmpty)
        {
            return masked;
        }

        return Placeholder().Replace(masked, match =>
            vault.TryReveal(match.Value, includeContext: false, out var value) ? value : match.Value);
    }

    public IOutputStream CreateOutputStream(PiiVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        return new OutputStream(piiOptions.Value.Enabled ? outputGuard.CreateStream(vault) : null, vault);
    }

    /// <summary>Canonicalises each delta like <see cref="GuardOutput"/> does for a whole answer, then guards it.</summary>
    private sealed class OutputStream(StreamingOutputGuard? guard, PiiVault vault) : IOutputStream
    {
        private readonly Dictionary<PiiType, int> _before = Snapshot(vault);

        public string Push(string delta)
        {
            ArgumentNullException.ThrowIfNull(delta);
            var canonical = UntrustedText.Canonicalize(delta);
            return guard is null ? canonical : guard.Push(canonical);
        }

        public string Flush()
        {
            var rest = guard?.Flush() ?? string.Empty;
            RecordRedactions(OccurrencesSince(_before, vault), "output");
            return rest;
        }
    }

    private ChatMessage SanitizeMessage(ChatMessage message, PiiVault vault)
    {
        ArgumentNullException.ThrowIfNull(message);

        var contents = new List<AIContent>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            contents.Add(content switch
            {
                TextContent text => new TextContent(Clean(text.Text, vault, PiiOrigin.Caller)),
                TextReasoningContent reasoning => new TextReasoningContent(Clean(reasoning.Text, vault, PiiOrigin.Caller)),
                FunctionResultContent result => new FunctionResultContent(result.CallId, RedactToolResult(result.Result, vault)),
                FunctionCallContent call => new FunctionCallContent(call.CallId, call.Name, RedactArguments(call.Arguments, vault)),

                // Binary and hosted content (images, files) cannot be redacted as text; it is passed through unchanged.
                _ => content,
            });
        }

        // AdditionalProperties and RawRepresentation are deliberately not copied (see the type remarks).
        return new ChatMessage(message.Role, contents)
        {
            AuthorName = SafeAuthorName(message.AuthorName, vault),
            MessageId = message.MessageId,
            CreatedAt = message.CreatedAt,
        };
    }

    /// <summary>
    /// An author name carrying PII is dropped rather than replaced: providers restrict the name field to a strict
    /// character set, and a bracketed placeholder would get the whole request rejected.
    /// </summary>
    private string? SafeAuthorName(string? authorName, PiiVault vault)
    {
        if (string.IsNullOrEmpty(authorName) || !piiOptions.Value.Enabled)
        {
            return authorName;
        }

        return redactor.Redact(UntrustedText.Canonicalize(authorName), vault, PiiOrigin.Caller).HasPii ? null : authorName;
    }

    private object? RedactToolResult(object? result, PiiVault vault) => result switch
    {
        null => null,
        string text => Clean(text, vault, PiiOrigin.Caller),
        JsonElement element => Clean(element.GetRawText(), vault, PiiOrigin.Caller),

        // Serialised the way the provider adapter would serialise it, so the wire format does not change.
        _ => Clean(SerializeForRedaction(result), vault, PiiOrigin.Caller),
    };

    private Dictionary<string, object?>? RedactArguments(IDictionary<string, object?>? arguments, PiiVault vault)
    {
        if (arguments is null)
        {
            return null;
        }

        var redacted = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        foreach (var (name, value) in arguments)
        {
            redacted[name] = value switch
            {
                string text => Clean(text, vault, PiiOrigin.Caller),
                JsonElement { ValueKind: JsonValueKind.String } element => Clean(element.GetString() ?? string.Empty, vault, PiiOrigin.Caller),
                JsonElement element => RedactJson(element, vault),
                _ => value,
            };
        }

        return redacted;
    }

    private object RedactJson(JsonElement element, PiiVault vault)
    {
        var raw = element.GetRawText();
        var redacted = Clean(raw, vault, PiiOrigin.Caller);
        if (string.Equals(raw, redacted, StringComparison.Ordinal))
        {
            return element;
        }

        try
        {
            using var document = JsonDocument.Parse(redacted);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // A placeholder replaced an unquoted value (e.g. a phone number stored as a JSON number).
            return redacted;
        }
    }

    private static string SerializeForRedaction(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions);
        }
        catch (NotSupportedException)
        {
            return value.ToString() ?? string.Empty;
        }
        catch (JsonException)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    /// <summary>Canonicalise (invisible and look-alike characters), then redact.</summary>
    private string Clean(string text, PiiVault vault, PiiOrigin origin)
    {
        var canonical = UntrustedText.Canonicalize(text);
        return piiOptions.Value.Enabled && canonical.Length > 0 ? redactor.Redact(canonical, vault, origin).Text : canonical;
    }

    private async Task<InjectionVerdict> InspectInputAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var options = injectionOptions.Value;
        if (!options.Enabled)
        {
            return InjectionVerdict.Clean(DetectorName);
        }

        var verdicts = new List<InjectionVerdict>();
        foreach (var message in messages)
        {
            // System messages from the caller are the application's own instructions: trusted, not scanned.
            ContentOrigin origin;
            string originTag;
            if (message.Role == ChatRole.User)
            {
                (origin, originTag) = (ContentOrigin.User, "user");
            }
            else if (message.Role == ChatRole.Tool)
            {
                (origin, originTag) = (ContentOrigin.ToolResult, "tool");
            }
            else
            {
                continue;
            }

            var text = InspectableText(message);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var verdict = await detector.InspectAsync(text, origin, cancellationToken);
            verdicts.Add(verdict);
            if (IsAttack(verdict, options))
            {
                // One attack decides the request; scanning the rest would only add latency (and remote calls).
                SentinelTelemetry.InjectionDetections.Add(1, Tag("origin", originTag), Tag("action", "blocked"));
                break;
            }
        }

        return Combine(verdicts, options);
    }

    private static string InspectableText(ChatMessage message)
    {
        var builder = new StringBuilder();
        foreach (var content in message.Contents)
        {
            var text = content switch
            {
                TextContent t => t.Text,
                FunctionResultContent { Result: string s } => s,
                FunctionResultContent { Result: not null } r => r.Result.ToString(),
                _ => null,
            };

            if (!string.IsNullOrEmpty(text))
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(text);
            }
        }

        return builder.ToString();
    }

    private static InjectionVerdict Combine(List<InjectionVerdict> verdicts, InjectionOptions options)
    {
        if (verdicts.Count == 0)
        {
            return InjectionVerdict.Clean(DetectorName);
        }

        var detectors = string.Join('+', verdicts.Select(v => v.Detector).Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.Ordinal));
        return new InjectionVerdict(
            verdicts.Exists(v => IsAttack(v, options)),
            verdicts.Max(v => v.Score),
            [.. verdicts.SelectMany(v => v.Rules).Distinct(StringComparer.Ordinal)],
            detectors.Length == 0 ? DetectorName : detectors);
    }

    /// <summary>The detector's own decision, or the configured threshold, whichever is stricter.</summary>
    private static bool IsAttack(InjectionVerdict verdict, InjectionOptions options) =>
        verdict.IsAttack || verdict.Score >= options.BlockThreshold;

    private static Dictionary<PiiType, int> Snapshot(PiiVault vault) => new(vault.Occurrences);

    private static Dictionary<PiiType, int> OccurrencesSince(Dictionary<PiiType, int> before, PiiVault vault)
    {
        var delta = new Dictionary<PiiType, int>();
        foreach (var (type, count) in vault.Occurrences)
        {
            var added = count - before.GetValueOrDefault(type);
            if (added > 0)
            {
                delta[type] = added;
            }
        }

        return delta;
    }

    private static void RecordRedactions(Dictionary<PiiType, int> counts, string origin)
    {
        foreach (var (type, count) in counts)
        {
            SentinelTelemetry.PiiRedactions.Add(count, Tag("pii.type", type.ToString()), Tag("origin", origin));
        }
    }

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    /// <summary>Exact placeholder tokens as minted by <see cref="PiiVault"/>, e.g. <c>[EMAIL_1]</c>; nothing looser.</summary>
    [GeneratedRegex(@"\[[A-Z]{2,8}_[1-9][0-9]{0,6}\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Placeholder();
}
