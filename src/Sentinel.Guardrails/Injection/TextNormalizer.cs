using System.Globalization;
using System.Text;

namespace Sentinel.Guardrails.Injection;

/// <summary>
/// Normalised forms of a text for injection matching. Detection runs on these, never on the raw string, so that
/// the many spellings of one instruction ("İGNORE", "ｉｇｎｏｒｅ", "ig\u200Bnore", "1gn0r3", "i g n o r e") collapse
/// to one.
/// </summary>
/// <param name="Visible">Invisible characters removed and NFKC applied; letter case preserved (for base64).</param>
/// <param name="Standard">Folded: NFKC, Turkish and diacritic folding, look-alike letters, lower case, collapsed whitespace (newlines kept as single "\n").</param>
/// <param name="Aggressive">Standard plus leetspeak folding and de-spacing of letter-by-letter obfuscation.</param>
/// <param name="HiddenText">ASCII smuggled in Unicode tag characters (U+E0020–U+E007E), decoded; empty when none.</param>
/// <param name="InvisibleCharacters">Zero-width, bidi-control and tag characters removed.</param>
/// <param name="BidiControls">Bidi embedding/override/isolate controls (U+202A–202E, U+2066–2069) among them.</param>
/// <param name="DespacedRuns">Letter-by-letter runs ("i.g.n.o.r.e") joined in <paramref name="Aggressive"/>.</param>
public sealed record NormalizedText(
    string Visible,
    string Standard,
    string Aggressive,
    string HiddenText,
    int InvisibleCharacters,
    int BidiControls,
    int DespacedRuns);

/// <summary>
/// Produces the <see cref="NormalizedText"/> forms. Pure and allocation-bounded (a few copies of the input); never
/// throws for any string, including malformed UTF-16.
/// </summary>
public static class TextNormalizer
{
    private const int TagBase = 0xE0000;
    private const int CancelTag = 0xE007F;
    private const int WavingBlackFlag = 0x1F3F4;

    public static NormalizedText Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var visible = StripInvisible(text, out var hidden, out var invisible, out var bidi);
        visible = visible.Normalize(NormalizationForm.FormKC);

        var folded = Fold(visible);
        var standard = CollapseWhitespace(folded);
        var despaced = Despace(Leet(folded), out var runs);
        var aggressive = CollapseWhitespace(despaced);

        return new NormalizedText(visible, standard, aggressive, hidden, invisible, bidi, runs);
    }

    /// <summary>
    /// Removes zero-width and bidi-control characters and Unicode tag characters, decoding the latter: tag
    /// characters render as nothing yet tokenise as ASCII, which makes them a hidden instruction channel. The one
    /// legitimate use, subdivision flags (🏴 + tag letters + cancel tag, e.g. Scotland), is not reported as hidden.
    /// Lone surrogates become U+FFFD so that normalisation can never throw.
    /// </summary>
    private static string StripInvisible(string text, out string hidden, out int invisible, out int bidi)
    {
        var builder = new StringBuilder(text.Length);
        var hiddenBuilder = new StringBuilder();
        var tagRun = new StringBuilder();
        var tagRunAfterFlag = false;
        var previous = 0;
        invisible = 0;
        bidi = 0;

        var i = 0;
        while (i < text.Length)
        {
            int codePoint;
            int consumed;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                consumed = 2;
            }
            else
            {
                codePoint = char.IsSurrogate(text[i]) ? 0xFFFD : text[i];
                consumed = 1;
            }

            i += consumed;

            if (codePoint is >= TagBase and <= CancelTag)
            {
                invisible++;
                if (tagRun.Length == 0)
                {
                    tagRunAfterFlag = previous == WavingBlackFlag;
                }

                if (codePoint == CancelTag)
                {
                    FlushTagRun(tagRun, hiddenBuilder, tagRunAfterFlag, terminated: true);
                }
                else if (codePoint is >= TagBase + 0x20 and <= TagBase + 0x7E)
                {
                    tagRun.Append((char)(codePoint - TagBase));
                }

                continue;
            }

            FlushTagRun(tagRun, hiddenBuilder, tagRunAfterFlag, terminated: false);

            if (IsBidiControl(codePoint))
            {
                invisible++;
                bidi++;
                continue;
            }

            if (IsZeroWidth(codePoint) || CharUnicodeInfo.GetUnicodeCategory(codePoint) == UnicodeCategory.Format)
            {
                // The listed zero-width characters plus any other format character (word joiners, deprecated
                // shaping controls...): all render as nothing and could split a keyword.
                invisible++;
                continue;
            }

            builder.Append(char.ConvertFromUtf32(codePoint));
            previous = codePoint;
        }

        FlushTagRun(tagRun, hiddenBuilder, tagRunAfterFlag, terminated: false);
        hidden = hiddenBuilder.ToString();
        return builder.ToString();
    }

    private static void FlushTagRun(StringBuilder run, StringBuilder hidden, bool afterFlag, bool terminated)
    {
        if (run.Length == 0)
        {
            return;
        }

        var isFlag = afterFlag && terminated && run.Length is >= 2 and <= 6 && run.ToString().All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));
        if (!isFlag)
        {
            if (hidden.Length > 0)
            {
                hidden.Append(' ');
            }

            hidden.Append(run);
        }

        run.Clear();
    }

    private static bool IsZeroWidth(int c) =>
        c is (>= 0x200B and <= 0x200F) or (>= 0x2060 and <= 0x2064) or 0xFEFF or 0x00AD or 0x034F or 0x180E or 0x061C;

    private static bool IsBidiControl(int c) => c is (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);

    /// <summary>
    /// Lower case without diacritics: Turkish letters fold to ASCII (ı/İ → i, ğ → g, ş → s, ç → c, ö → o, ü → u,
    /// â/î/û → a/i/u), so do other accented letters, and common Cyrillic/Greek look-alikes map to Latin.
    /// Whitespace is kept as-is here (de-spacing needs the original spacing).
    /// </summary>
    private static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var raw in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(raw);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            builder.Append(Confusable(char.ToLowerInvariant(raw)));
        }

        return builder.ToString();
    }

    private static char Confusable(char c) => c switch
    {
        // Cyrillic
        'а' => 'a', 'в' => 'b', 'е' => 'e', 'ѕ' => 's', 'і' => 'i', 'ј' => 'j', 'к' => 'k', 'м' => 'm', 'н' => 'h',
        'о' => 'o', 'р' => 'p', 'с' => 'c', 'т' => 't', 'у' => 'y', 'х' => 'x', 'ԁ' => 'd', 'һ' => 'h', 'ԛ' => 'q',
        'ԝ' => 'w',

        // Greek
        'α' => 'a', 'ε' => 'e', 'ι' => 'i', 'κ' => 'k', 'ν' => 'v', 'ο' => 'o', 'ρ' => 'p', 'τ' => 't', 'υ' => 'u',
        'χ' => 'x',

        // Latin variants NFKC leaves alone
        'ɡ' => 'g', 'ɑ' => 'a', 'ı' => 'i', 'ſ' => 's',
        _ => c,
    };

    private static char LeetChar(char c) => c switch
    {
        '0' => 'o',
        '1' => 'i',
        '3' => 'e',
        '4' => 'a',
        '5' => 's',
        '7' => 't',
        '@' => 'a',
        '$' => 's',
        _ => c,
    };

    private static string Leet(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            span[i] = LeetChar(source[i]);
        }
    });

    /// <summary>
    /// Joins runs of three or more single letters separated by exactly one separator ("i g n o r e",
    /// "i.g.n.o.r.e", "i-g-n-o-r-e"). Two or more separators end a run, so "i g n o r e  a l l" keeps its words.
    /// </summary>
    private static string Despace(string text, out int runs)
    {
        runs = 0;
        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (IsSingleLetterAt(text, i) && i + 2 < text.Length && IsRunSeparator(text[i + 1]) && IsSingleLetterAt(text, i + 2))
            {
                var j = i;
                var letters = 1;
                while (j + 2 < text.Length && IsRunSeparator(text[j + 1]) && IsSingleLetterAt(text, j + 2))
                {
                    j += 2;
                    letters++;
                }

                if (letters >= 3)
                {
                    for (var k = i; k <= j; k += 2)
                    {
                        builder.Append(text[k]);
                    }

                    runs++;
                    i = j + 1;
                    continue;
                }
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }

    private static bool IsSingleLetterAt(string text, int i) =>
        char.IsLetter(text[i]) && (i == 0 || !char.IsLetter(text[i - 1])) && (i + 1 >= text.Length || !char.IsLetter(text[i + 1]));

    private static bool IsRunSeparator(char c) => c is ' ' or '.' or '-' or '_' or '*' or '/' or '|' or '·' or '•' or '+' or '~';

    /// <summary>Horizontal whitespace runs become one space; runs containing a line break become one "\n".</summary>
    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                builder.Append(text[i++]);
                continue;
            }

            var newline = false;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                newline |= text[i] is '\n' or '\r' or '\u2028' or '\u2029' or '\u0085';
                i++;
            }

            if (builder.Length > 0 && i < text.Length)
            {
                builder.Append(newline ? '\n' : ' ');
            }
        }

        return builder.ToString();
    }
}
