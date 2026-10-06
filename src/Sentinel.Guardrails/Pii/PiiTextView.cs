using System.Globalization;
using System.Text;

namespace Sentinel.Guardrails.Pii;

/// <summary>
/// The text the recognizers actually scan. Models read "4111\u200B1111…" (zero-width space), "４１１１…" (full-width) or
/// "4111 1111…" (no-break spaces) exactly like the plain number, so an attacker could use them to slip PII past
/// ASCII patterns. The view removes invisible characters (format characters, variation selectors) and maps every decimal digit, full-width ASCII
/// character, space separator and dash to its ASCII form, while remembering where each character came from so
/// findings are reported (and replaced) at their original offsets.
/// </summary>
internal sealed class PiiTextView
{
    private readonly int[]? _originalStart;
    private readonly int[]? _originalEnd;

    private PiiTextView(string text, int[]? originalStart, int[]? originalEnd)
    {
        Text = text;
        _originalStart = originalStart;
        _originalEnd = originalEnd;
    }

    public string Text { get; }

    public static PiiTextView Create(string original)
    {
        // Fast path: pure ASCII needs no mapping (the common case for prompts and answers).
        if (Ascii.IsValid(original))
        {
            return new PiiTextView(original, null, null);
        }

        var builder = new StringBuilder(original.Length);
        var starts = new List<int>(original.Length);
        var ends = new List<int>(original.Length);
        var i = 0;
        while (i < original.Length)
        {
            var status = Rune.DecodeFromUtf16(original.AsSpan(i), out var rune, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                // Lone surrogate: keep it as an opaque character.
                Append(builder, starts, ends, original[i], i, 1);
                i++;
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (IsInvisible(rune.Value, category))
            {
                // Zero-width characters, bidi controls, soft hyphen, tag characters, variation selectors: dropped.
            }
            else if (category == UnicodeCategory.DecimalDigitNumber)
            {
                Append(builder, starts, ends, (char)('0' + (int)Rune.GetNumericValue(rune)), i, consumed);
            }
            else if (rune.Value is >= 0xFF01 and <= 0xFF5E)
            {
                Append(builder, starts, ends, (char)(rune.Value - 0xFEE0), i, consumed);
            }
            else if (category == UnicodeCategory.SpaceSeparator)
            {
                Append(builder, starts, ends, ' ', i, consumed);
            }
            else if (category == UnicodeCategory.DashPunctuation)
            {
                Append(builder, starts, ends, '-', i, consumed);
            }
            else
            {
                for (var k = 0; k < consumed; k++)
                {
                    // Both halves of a surrogate pair map back to the whole code point.
                    builder.Append(original[i + k]);
                    starts.Add(i);
                    ends.Add(i + consumed);
                }
            }

            i += consumed;
        }

        return new PiiTextView(builder.ToString(), [.. starts], [.. ends]);
    }

    /// <summary>Characters that render as nothing (or only modify the previous glyph) and are dropped.</summary>
    internal static bool IsInvisible(int codePoint, UnicodeCategory category) =>
        category == UnicodeCategory.Format
        || codePoint is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF) or 0x034F;

    /// <summary>Maps a span of <see cref="Text"/> back to the original string.</summary>
    public (int Start, int Length) ToOriginal(int start, int length)
    {
        if (_originalStart is null || _originalEnd is null)
        {
            return (start, length);
        }

        var originalStart = _originalStart[start];
        var originalEnd = _originalEnd[start + length - 1];
        return (originalStart, originalEnd - originalStart);
    }

    private static void Append(StringBuilder builder, List<int> starts, List<int> ends, char c, int index, int consumed)
    {
        builder.Append(c);
        starts.Add(index);
        ends.Add(index + consumed);
    }
}
