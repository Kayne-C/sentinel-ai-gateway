using System.Globalization;
using System.Text;

namespace Sentinel.Application.Guardrails;

/// <summary>
/// Text from callers, documents and tools is canonicalised before redaction, detection and prompt building, because
/// invisible or look-alike characters defeat all three: <c>ali&#x200B;@corp.com</c> slips past an e-mail pattern,
/// <c>ign&#x200B;ore previous instructions</c> past a keyword rule, and <c>＜/document＞</c> past a tag filter, while
/// a model reads each of them exactly like the plain version.
/// </summary>
internal static class UntrustedText
{
    /// <summary>
    /// Deterministic, ICU-independent canonical form of untrusted text: full-width ASCII and the usual look-alikes of
    /// <c>&lt; &gt; / |</c> are folded onto ASCII so pattern-based guardrails see them; invisible format characters (zero-width
    /// characters, bidi controls and the Unicode "tag" block used to smuggle hidden instructions) are removed rather
    /// than decoded; lone surrogates become U+FFFD.
    /// </summary>
    public static string Canonicalize(string text)
    {
        if (string.IsNullOrEmpty(text) || !NeedsCanonicalization(text))
        {
            return text ?? string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(text, i) != UnicodeCategory.Format)
                {
                    builder.Append(c).Append(text[i + 1]);
                }

                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                builder.Append('\uFFFD');
                continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                continue;
            }

            builder.Append(Fold(c));
        }

        return builder.ToString();
    }

    private static bool NeedsCanonicalization(string text)
    {
        foreach (var c in text)
        {
            // Below U+00AD (the first format character) there is nothing to fold or strip.
            if (c >= '\u00AD' && (char.IsSurrogate(c) || Fold(c) != c || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
            {
                return true;
            }
        }

        return false;
    }

    private static char Fold(char c) => c switch
    {
        >= '\uFF01' and <= '\uFF5E' => (char)(c - 0xFEE0),
        '\u3000' => ' ',
        '\uFE64' or '\u2039' or '\u3008' or '\u27E8' or '\u2329' or '\u02C2' or '\u1438' => '<',
        '\uFE65' or '\u203A' or '\u3009' or '\u27E9' or '\u232A' or '\u02C3' or '\u1433' => '>',
        '\u2215' or '\u2044' or '\u29F8' => '/',
        '\u2223' or '\u01C0' => '|',
        _ => c,
    };
}
