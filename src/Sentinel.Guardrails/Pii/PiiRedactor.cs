using System.Text;
using Microsoft.Extensions.Options;

namespace Sentinel.Guardrails.Pii;

/// <summary>
/// Runs the enabled recognizers over a <see cref="PiiTextView"/> of the text and replaces the winning spans with
/// vault placeholders. Stateless and thread-safe; all per-request state lives in the caller's <see cref="PiiVault"/>.
/// </summary>
/// <remarks>
/// Overlaps are resolved greedily: the longest span wins (an IBAN beats the phone-shaped digits inside it), then
/// the higher confidence, then a fixed type priority so the outcome never depends on recognizer registration order.
/// Existing placeholders are never re-redacted, so redacting already-redacted text is a no-op.
/// </remarks>
internal sealed class PiiRedactor(IEnumerable<IPiiRecognizer> recognizers, IOptions<PiiOptions> options) : IPiiRedactor
{
    private readonly IPiiRecognizer[] _recognizers = [.. recognizers];

    public RedactionResult Redact(string text, PiiVault vault, PiiOrigin origin) => Redact(text, vault, origin, candidateSpans: null);

    /// <summary>
    /// As <see cref="Redact(string, PiiVault, PiiOrigin)"/>, and also reports every recognizer candidate (before
    /// overlap resolution, original offsets) into <paramref name="candidateSpans"/>. The streaming guard needs
    /// them: a candidate that loses to a longer, still-incomplete one can win again once that one disappears.
    /// </summary>
    internal RedactionResult Redact(string text, PiiVault vault, PiiOrigin origin, List<(int Start, int Length)>? candidateSpans)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(vault);

        var settings = options.Value;
        if (!settings.Enabled || text.Length == 0)
        {
            return new RedactionResult(text, []);
        }

        var view = PiiTextView.Create(text);
        var candidates = Recognize(view.Text, settings.Types);
        if (candidates.Count == 0)
        {
            return new RedactionResult(text, []);
        }

        if (candidateSpans is not null)
        {
            foreach (var candidate in candidates)
            {
                candidateSpans.Add(view.ToOriginal(candidate.Start, candidate.Length));
            }
        }

        var accepted = Resolve(view.Text, candidates);
        if (accepted.Count == 0)
        {
            return new RedactionResult(text, []);
        }

        var builder = new StringBuilder(text.Length);
        var findings = new List<PiiFinding>(accepted.Count);
        var cursor = 0;
        foreach (var match in accepted)
        {
            var (start, length) = view.ToOriginal(match.Start, match.Length);

            // The vault receives the normalised value, so "０５３２…" and "0532…" share one placeholder.
            var placeholder = vault.Protect(match.Type, view.Text.Substring(match.Start, match.Length), origin);
            builder.Append(text, cursor, start - cursor).Append(placeholder);
            findings.Add(new PiiFinding(match.Type, start, length, placeholder));
            cursor = start + length;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return new RedactionResult(builder.ToString(), findings);
    }

    private List<PiiMatch> Recognize(string text, HashSet<PiiType> types)
    {
        var candidates = new List<PiiMatch>();
        foreach (var recognizer in _recognizers)
        {
            if (!types.Contains(recognizer.Type))
            {
                continue;
            }

            foreach (var match in recognizer.Recognize(text))
            {
                // Defensive: a misbehaving (custom) recognizer must not corrupt the output.
                if (match.Length > 0 && match.Start >= 0 && match.Start <= text.Length - match.Length && types.Contains(match.Type))
                {
                    candidates.Add(match);
                }
            }
        }

        return candidates;
    }

    /// <summary>Greedy selection of non-overlapping matches; returns them ordered by start.</summary>
    private static List<PiiMatch> Resolve(string text, List<PiiMatch> candidates)
    {
        candidates.Sort(static (a, b) =>
        {
            var byLength = b.Length.CompareTo(a.Length);
            if (byLength != 0)
            {
                return byLength;
            }

            var byConfidence = b.Confidence.CompareTo(a.Confidence);
            if (byConfidence != 0)
            {
                return byConfidence;
            }

            var byPriority = Priority(a.Type).CompareTo(Priority(b.Type));
            return byPriority != 0 ? byPriority : a.Start.CompareTo(b.Start);
        });

        // Placeholders already in the text are occupied from the start and never emitted.
        var occupied = new IntervalSet();
        foreach (var placeholder in Placeholders.Token().EnumerateMatches(text))
        {
            occupied.TryAdd(placeholder.Index, placeholder.Index + placeholder.Length);
        }

        var accepted = new List<PiiMatch>();
        foreach (var candidate in candidates)
        {
            if (occupied.TryAdd(candidate.Start, candidate.Start + candidate.Length))
            {
                accepted.Add(candidate);
            }
        }

        accepted.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return accepted;
    }

    /// <summary>Tie-break when two spans are equally long and confident: checksum-backed types first.</summary>
    private static int Priority(PiiType type) => type switch
    {
        PiiType.Iban => 0,
        PiiType.PaymentCard => 1,
        PiiType.NationalId => 2,
        PiiType.TaxNumber => 3,
        PiiType.Email => 4,
        PiiType.IpAddress => 5,
        PiiType.PhoneNumber => 6,
        _ => 7,
    };

    /// <summary>Disjoint half-open intervals kept sorted by start; O(log n) overlap test per insertion.</summary>
    private sealed class IntervalSet
    {
        private readonly List<(int Start, int End)> _intervals = [];

        public bool TryAdd(int start, int end)
        {
            int low = 0, high = _intervals.Count;
            while (low < high)
            {
                var mid = (low + high) >>> 1;
                if (_intervals[mid].Start < start)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            if ((low < _intervals.Count && _intervals[low].Start < end) || (low > 0 && _intervals[low - 1].End > start))
            {
                return false;
            }

            _intervals.Insert(low, (start, end));
            return true;
        }
    }
}
