using System.Text.RegularExpressions;

namespace Sentinel.Guardrails.Pii.Recognizers;

/// <summary>
/// Vergi Kimlik No. A bare ten-digit number is far more often a phone number without its leading zero, an order
/// number or a timestamp, and one in ten random numbers passes the check digit, so a VKN is only reported when a
/// tax keyword ("vergi", "VKN", "tax id"...) appears shortly before it.
/// </summary>
internal sealed partial class TaxNumberRecognizer : IPiiRecognizer
{
    private const double Confidence = 0.85;

    /// <summary>How far back (in characters) the keyword may be: "Vergi Kimlik Numarası (VKN): " fits comfortably.</summary>
    internal const int ContextWindow = 40;

    private static readonly string[] Keywords = ["vergi", "vkn", "tax id", "taxid", "tax number", "tax no", "tax identification"];

    public PiiType Type => PiiType.TaxNumber;

    public IEnumerable<PiiMatch> Recognize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var matches = new List<PiiMatch>();
        foreach (var match in Candidate().EnumerateMatches(text))
        {
            if (Checksums.IsValidTaxNumber(text.AsSpan(match.Index, match.Length)) && HasKeywordBefore(text, match.Index))
            {
                matches.Add(new PiiMatch(Type, match.Index, match.Length, Confidence));
            }
        }

        return matches;
    }

    private static bool HasKeywordBefore(string text, int start)
    {
        var from = Math.Max(0, start - ContextWindow);
        var window = FoldKeywordWindow(text.AsSpan(from, start - from));
        foreach (var keyword in Keywords)
        {
            if (window.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lower-cases, folds Turkish letters (VERGİ, vergı) and turns punctuation runs into one space.</summary>
    private static string FoldKeywordWindow(ReadOnlySpan<char> window)
    {
        Span<char> buffer = stackalloc char[window.Length];
        var length = 0;
        var lastWasSpace = false;
        foreach (var raw in window)
        {
            var c = raw switch
            {
                'İ' or 'I' or 'ı' => 'i',
                'Ğ' or 'ğ' => 'g',
                'Ş' or 'ş' => 's',
                'Ç' or 'ç' => 'c',
                'Ö' or 'ö' => 'o',
                'Ü' or 'ü' => 'u',
                _ => char.ToLowerInvariant(raw),
            };

            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = c;
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                buffer[length++] = ' ';
                lastWasSpace = true;
            }
        }

        return new string(buffer[..length]);
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}]|[0-9]\.)[0-9]{10}(?![\p{L}\p{N}]|\.[0-9])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Candidate();
}
