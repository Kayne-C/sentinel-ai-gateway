using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.Extensions.Options;

namespace Sentinel.Guardrails.Injection;

/// <summary>
/// Rule-based prompt-injection detector (Turkish and English). Every rule is matched against normalised variants
/// of the text (see <see cref="TextNormalizer"/>), hidden tag-character text and decoded base64 payloads; the
/// fired rules' weights are combined with noisy-OR, 1 - Π(1 - wᵢ), so independent weak cues add up while no
/// number of weak cues ever reaches 1.
/// </summary>
/// <remarks>
/// Heuristics are a first line of defence, not a proof: they are cheap, explainable (rule ids go to the audit
/// log) and catch the bulk of known attacks, and the composite detector can add a model-based classifier for
/// paraphrased attacks. Matching is linear in the input; a regex timeout fails closed.
/// </remarks>
internal sealed partial class HeuristicInjectionDetector : IPromptInjectionDetector
{
    public const string Name = "heuristic";

    private const int MaxDepth = 2;
    private const int MaxEncodedCandidates = 16;
    private const int MaxEncodedChars = 64 * 1024;

    private static readonly Lazy<bool> WarmedUp = new(static () =>
    {
        foreach (var rule in InjectionRules.Rules)
        {
            rule.Matches("warm-up: ignore previous instructions, önceki talimatları yok say");
        }

        return true;
    });

    private readonly IOptions<InjectionOptions> _options;

    /// <remarks>
    /// The non-backtracking automata are built on first use, which costs on the order of a second once per
    /// process; construction starts that work in the background so the first real request does not pay for it.
    /// </remarks>
    public HeuristicInjectionDetector(IOptions<InjectionOptions> options)
    {
        _options = options;
        if (!WarmedUp.IsValueCreated)
        {
            ThreadPool.QueueUserWorkItem(static _ =>
            {
                try
                {
                    WarmUp();
                }
                catch (InvalidOperationException)
                {
                    // Never crash the process from a background warm-up; a real call surfaces the problem.
                }
                catch (TypeInitializationException)
                {
                    // Same: the first InspectAsync rethrows it where it can be handled and logged.
                }
            });
        }
    }

    /// <summary>Builds every rule's automaton now (idempotent, thread-safe).</summary>
    internal static void WarmUp() => _ = WarmedUp.Value;

    public ValueTask<InjectionVerdict> InspectAsync(string text, ContentOrigin origin, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _options.Value;
        if (!settings.Enabled || string.IsNullOrEmpty(text))
        {
            return ValueTask.FromResult(InjectionVerdict.Clean(Name));
        }

        try
        {
            var fired = new Dictionary<string, double>(StringComparer.Ordinal);
            Evaluate(text, origin, depth: 0, fired);
            var score = NoisyOr(fired.Values);
            var rules = fired.Keys.Order(StringComparer.Ordinal).ToArray();
            return ValueTask.FromResult(new InjectionVerdict(score >= settings.BlockThreshold, score, rules, Name));
        }
        catch (RegexMatchTimeoutException)
        {
            // Input crafted to stall the matcher is itself suspicious; failing open would make it a bypass.
            return ValueTask.FromResult(new InjectionVerdict(true, 1, [InjectionRules.Timeout], Name));
        }
    }

    internal static double NoisyOr(IEnumerable<double> weights)
    {
        var clean = 1.0;
        foreach (var weight in weights)
        {
            clean *= 1 - Math.Clamp(weight, 0, 1);
        }

        return Math.Clamp(1 - clean, 0, 1);
    }

    private static void Evaluate(string text, ContentOrigin origin, int depth, Dictionary<string, double> fired)
    {
        var normalized = TextNormalizer.Normalize(text);
        var aggressiveDiffers = !string.Equals(normalized.Aggressive, normalized.Standard, StringComparison.Ordinal);
        var obfuscated = false;

        foreach (var rule in InjectionRules.Rules)
        {
            if (rule.Matches(normalized.Standard))
            {
                Fire(fired, rule.Id, rule.WeightFor(origin));
            }
            else if (aggressiveDiffers && rule.Matches(normalized.Aggressive))
            {
                Fire(fired, rule.Id, rule.WeightFor(origin));
                obfuscated = true;
            }
        }

        if (normalized.DespacedRuns > 0)
        {
            foreach (var id in InjectionRules.MatchSquashed(LettersOnly(normalized.Aggressive)))
            {
                if (!fired.ContainsKey(id))
                {
                    Fire(fired, id, InjectionRules.Get(id).WeightFor(origin));
                    obfuscated = true;
                }
            }
        }

        foreach (var rule in InjectionRules.Rules)
        {
            if (rule.SubsumedBy is { } stronger && fired.ContainsKey(stronger))
            {
                fired.Remove(rule.Id);
            }
        }

        if (obfuscated)
        {
            Fire(fired, InjectionRules.Obfuscation, InjectionRules.ObfuscationWeight);
        }

        if (normalized.BidiControls > 0)
        {
            Fire(fired, InjectionRules.BidiControls, InjectionRules.BidiWeight);
        }

        if (normalized.HiddenText.Length > 0)
        {
            // Text nobody can see but the model reads: strong on its own, and its content is inspected too.
            Fire(fired, InjectionRules.HiddenTags, InjectionRules.HiddenTagsWeight);
            if (depth < MaxDepth)
            {
                Evaluate(normalized.HiddenText, origin, depth + 1, fired);
            }
        }

        if (depth < MaxDepth)
        {
            InspectEncoded(normalized.Visible, origin, depth, fired);
        }
    }

    /// <summary>
    /// Base64 blobs that decode to readable text are inspected like plain text; "injection.encoded" fires when the
    /// decoded text trips other rules. Binary payloads (images, keys) do not decode to valid UTF-8 text and are
    /// ignored, which keeps attachments and data URIs from being flagged.
    /// </summary>
    private static void InspectEncoded(string visible, ContentOrigin origin, int depth, Dictionary<string, double> fired)
    {
        var inspected = 0;
        foreach (var match in Base64Candidate().EnumerateMatches(visible))
        {
            if (inspected++ >= MaxEncodedCandidates)
            {
                break;
            }

            var decoded = TryDecodeText(visible.AsSpan(match.Index, Math.Min(match.Length, MaxEncodedChars)));
            if (decoded is null)
            {
                continue;
            }

            var inner = new Dictionary<string, double>(StringComparer.Ordinal);
            Evaluate(decoded, origin, depth + 1, inner);
            if (inner.Count > 0)
            {
                var innerScore = NoisyOr(inner.Values);
                Fire(fired, InjectionRules.Encoded, innerScore >= 0.5 ? InjectionRules.EncodedStrongWeight : InjectionRules.EncodedWeakWeight);
            }
        }
    }

    private static string? TryDecodeText(ReadOnlySpan<char> candidate)
    {
        // URL-safe alphabet and missing padding are common in smuggled payloads.
        var trimmed = candidate.TrimEnd('=');
        var length = trimmed.Length;
        if (length % 4 == 1)
        {
            length--;
        }

        var padded = new char[(length + 3) / 4 * 4];
        for (var i = 0; i < padded.Length; i++)
        {
            padded[i] = i < length ? trimmed[i] switch { '-' => '+', '_' => '/', var c => c } : '=';
        }

        var bytes = new byte[padded.Length / 4 * 3];
        if (!Convert.TryFromBase64Chars(padded, bytes, out var written) || written == 0 || !Utf8.IsValid(bytes.AsSpan(0, written)))
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(bytes, 0, written);
        var printable = 0;
        foreach (var c in text)
        {
            if (!char.IsControl(c) || c is '\n' or '\r' or '\t')
            {
                printable++;
            }
        }

        // Mostly letters and spaces: real text, not random bytes that happen to be valid UTF-8.
        var letters = text.Count(c => char.IsLetter(c) || c == ' ');
        return printable == text.Length && letters * 10 >= text.Length * 7 ? text : null;
    }

    private static string LettersOnly(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static void Fire(Dictionary<string, double> fired, string id, double weight)
    {
        fired[id] = Math.Max(weight, fired.GetValueOrDefault(id));
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9+/=_-])[A-Za-z0-9+/_-]{24,}={0,2}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Base64Candidate();
}
