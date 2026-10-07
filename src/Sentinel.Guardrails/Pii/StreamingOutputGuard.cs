using System.Globalization;
using System.Text;

namespace Sentinel.Guardrails.Pii;

/// <summary>
/// <see cref="OutputGuard"/> for token streams. Text is emitted as soon as no later delta can change how it is
/// redacted or restored; a tail that could still grow into a placeholder or a PII entity is held back.
/// </summary>
/// <remarks>
/// <para>
/// Invariant: for every split of a text into deltas, the concatenation of all <see cref="Push"/> results plus
/// <see cref="Flush"/> equals <see cref="OutputGuard.Apply"/> of the whole text on a vault with the same
/// entries. Emitting a half-recognised IBAN would leak its first digits and leave a remainder that no longer
/// matches, so correctness here is a privacy property, not a nicety.
/// </para>
/// <para>
/// What is held back: the trailing whitespace-free token (an e-mail, IPv6 address or compact IBAN can only grow
/// at the end), plus earlier tokens joined to it by single spaces when they contain digits (grouped cards,
/// phone numbers and IBANs), bounded by <see cref="SpacedEntityWindow"/>; and an unclosed "[" that may still
/// become a placeholder. Each candidate cut is then checked against a real redaction pass over the pending text
/// plus a raw context tail (lookbehinds and the VKN keyword window reach backwards), and moved before any
/// entity that would straddle it, including recognizer candidates that currently lose an overlap to a longer,
/// still incomplete one (with the built-in redactor; a custom one only exposes its winning findings). The raw
/// context lives only in this object, which lives only as long as the request.
/// </para>
/// </remarks>
public sealed class StreamingOutputGuard
{
    /// <summary>Longest entity that can contain spaces (a spaced IBAN or E.164 number is at most 44) plus lookaround.</summary>
    internal const int SpacedEntityWindow = 48;

    /// <summary>Longest whitespace-free entity candidate (e-mail: 473) plus lookaround, in visible characters.</summary>
    internal const int TokenWindow = 512;

    private const int MaxTokenScan = 8192;
    private const int MaxContext = 256;
    private const int MinContext = 96;

    private readonly OutputGuard _guard;
    private readonly PiiVault _vault;
    private readonly StringBuilder _pending = new();
    private string _context = string.Empty;
    private string _redactedTail = string.Empty;

    internal StreamingOutputGuard(OutputGuard guard, PiiVault vault)
    {
        _guard = guard;
        _vault = vault;
    }

    /// <summary>Accepts the next delta and returns the text that is now safe to send (possibly empty).</summary>
    public string Push(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.Length == 0)
        {
            return string.Empty;
        }

        _pending.Append(delta);
        var pending = _pending.ToString();
        var safe = SafeLength(pending);
        return safe == 0 ? string.Empty : Emit(pending, safe);
    }

    /// <summary>Ends the stream: everything still held back is redacted, restored and returned.</summary>
    public string Flush()
    {
        if (_pending.Length == 0)
        {
            return string.Empty;
        }

        var pending = _pending.ToString();
        return Emit(pending, pending.Length);
    }

    private string Emit(string pending, int safe)
    {
        var scan = _context + pending;
        var offset = _context.Length;

        // Scratch vault: the scan covers the context again, which must not count twice or consume numbers.
        var scratch = new PiiVault();
        var candidates = _guard.Redactor is PiiRedactor ? new List<(int Start, int Length)>() : null;
        var result = _guard.Redactor is PiiRedactor own
            ? own.Redact(scan, scratch, PiiOrigin.Context, candidates)
            : _guard.Redactor.Redact(scan, scratch, PiiOrigin.Context);
        var findings = result.Findings
            .Where(f => f.Length > 0 && f.Start >= 0 && f.Start + f.Length <= scan.Length && f.Start + f.Length > offset)
            .OrderBy(f => f.Start)
            .ToList();

        // A finding reaching back into already-emitted text cannot happen when the cuts are right; should it ever,
        // its remainder is masked (fail closed) and the cut may not fall inside it.
        var floor = offset;
        foreach (var finding in findings)
        {
            if (finding.Start < offset)
            {
                floor = Math.Max(floor, finding.Start + finding.Length);
            }
        }

        // Never cut through an entity, whether it currently wins or only competes (see PiiRedactor): the cut moves
        // back to the start of anything straddling it.
        var spans = findings.Select(f => (f.Start, f.Length)).Concat(candidates ?? []).ToList();
        var cut = Math.Max(floor, offset + StepBackOutOfPlaceholder(pending, safe));
        bool moved;
        do
        {
            moved = false;
            foreach (var (start, length) in spans)
            {
                if (start >= floor && start < cut && start + length > cut)
                {
                    cut = start;
                    moved = true;
                }
            }
        }
        while (moved);

        if (cut <= offset)
        {
            return string.Empty;
        }

        var output = new StringBuilder(cut - offset + 16);
        var cursor = offset;
        foreach (var finding in findings)
        {
            if (finding.Start >= cut)
            {
                break;
            }

            if (finding.Start + finding.Length <= cursor)
            {
                continue; // overlaps an earlier finding (only a misbehaving custom redactor returns those)
            }

            var start = Math.Max(finding.Start, cursor);
            output.Append(scan, cursor, start - cursor);
            var value = scratch.TryReveal(finding.Placeholder, includeContext: true, out var revealed)
                ? revealed
                : scan.Substring(finding.Start, finding.Length);
            output.Append(_vault.Protect(finding.Type, value, PiiOrigin.Context));
            cursor = finding.Start + finding.Length;
        }

        output.Append(scan, cursor, cut - cursor);

        var committed = cut - offset;
        _context = TrimContext(_context + pending[..committed]);
        _pending.Remove(0, committed);

        // Restoring looks back for a URL around each placeholder, so it sees the redacted text emitted before.
        var redacted = output.ToString();
        var restored = _guard.Restore(redacted, _vault, _redactedTail);
        var tail = _redactedTail + redacted;
        _redactedTail = tail.Length > PiiRestorer.UrlLookBehind ? tail[^PiiRestorer.UrlLookBehind..] : tail;
        return restored;
    }

    /// <summary>Length of the prefix of <paramref name="pending"/> that no later delta can affect.</summary>
    internal static int SafeLength(string pending)
    {
        var end = pending.Length;

        // 1. The trailing whitespace-free token can still grow (bounded by visible length).
        var hold = end;
        var visible = 0;
        while (hold > 0 && !char.IsWhiteSpace(pending[hold - 1]) && visible < TokenWindow && end - hold < MaxTokenScan)
        {
            hold--;
            if (!IsInvisible(pending[hold]))
            {
                visible++;
            }
        }

        // 2. Earlier tokens joined by single spaces may be the start of a grouped number; only digit-bearing tokens
        //    can start one (every spaced pattern starts with a digit, "+d", "(d" or an IBAN's "LLdd"). The window
        //    counts visible characters: invisible ones vanish before recognition, so they cannot stretch an entity.
        var tokenEnd = hold;
        var scanned = end - hold;
        while (tokenEnd >= 2 && IsSoftSpace(pending[tokenEnd - 1]) && !char.IsWhiteSpace(pending[tokenEnd - 2]))
        {
            var start = tokenEnd - 1;
            visible++;
            var hasDigit = false;
            var truncated = false;
            while (start > 0 && !char.IsWhiteSpace(pending[start - 1]))
            {
                var invisible = IsInvisible(pending[start - 1]);
                if ((!invisible && visible + 1 > SpacedEntityWindow) || ++scanned > MaxTokenScan)
                {
                    truncated = true;
                    break;
                }

                start--;
                visible += invisible ? 0 : 1;
                hasDigit |= char.IsDigit(pending[start]);
            }

            if (hasDigit)
            {
                hold = Math.Min(hold, start);
            }

            if (truncated)
            {
                break;
            }

            tokenEnd = start;
        }

        // 3. An unclosed "[" near the end may still become a placeholder.
        var open = pending.LastIndexOf('[');
        if (open >= 0 && end - open <= Placeholders.MaxLength && pending.IndexOf(']', open) < 0)
        {
            hold = Math.Min(hold, open);
        }

        return hold;
    }

    /// <summary>Never cut inside a complete placeholder token: restoring halves would fail.</summary>
    private static int StepBackOutOfPlaceholder(string pending, int safe)
    {
        foreach (var token in Placeholders.Token().EnumerateMatches(pending))
        {
            if (token.Index < safe && token.Index + token.Length > safe)
            {
                return token.Index;
            }

            if (token.Index >= safe)
            {
                break;
            }
        }

        return safe;
    }

    /// <summary>
    /// Keeps enough emitted raw text for lookbehinds and the VKN keyword window, starting after whitespace that
    /// follows a non-digit so the rescanned context begins where a fresh scan would also begin.
    /// </summary>
    private static string TrimContext(string context)
    {
        if (context.Length <= MaxContext)
        {
            return context;
        }

        var earliest = context.Length - MaxContext;
        var latest = context.Length - MinContext;
        for (var q = earliest; q <= latest; q++)
        {
            if (char.IsWhiteSpace(context[q - 1]) && (q < 2 || !char.IsDigit(context[q - 2])))
            {
                return context[q..];
            }
        }

        return context[earliest..];
    }

    private static bool IsSoftSpace(char c) => CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary>
    /// Whether <paramref name="c"/> may vanish from the recognizers' view (see <see cref="PiiTextView"/>). Errs on
    /// the side of "invisible": counting too few visible characters only holds back more. A low surrogate never
    /// counts (its high surrogate does), and plane-14 high surrogates (tag characters, variation selectors) never do.
    /// </summary>
    private static bool IsInvisible(char c) =>
        char.IsLowSurrogate(c)
        || c == '\uDB40'
        || PiiTextView.IsInvisible(c, CharUnicodeInfo.GetUnicodeCategory(c));
}
