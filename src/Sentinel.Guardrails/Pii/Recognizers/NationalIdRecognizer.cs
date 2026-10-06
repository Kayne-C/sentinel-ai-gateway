using System.Text.RegularExpressions;

namespace Sentinel.Guardrails.Pii.Recognizers;

/// <summary>
/// T.C. Kimlik No. Only exactly eleven digits that stand alone are candidates: a digit run inside a longer number
/// (IBAN, card, phone with country code) or glued to letters (reference codes) is not a national id.
/// </summary>
internal sealed partial class NationalIdRecognizer : IPiiRecognizer
{
    private const double Confidence = 0.9;

    public PiiType Type => PiiType.NationalId;

    public IEnumerable<PiiMatch> Recognize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var matches = new List<PiiMatch>();
        foreach (var match in Candidate().EnumerateMatches(text))
        {
            if (Checksums.IsValidNationalId(text.AsSpan(match.Index, match.Length)))
            {
                matches.Add(new PiiMatch(Type, match.Index, match.Length, Confidence));
            }
        }

        return matches;
    }

    // Not preceded/followed by letters or digits, and not the integer or fractional part of a decimal number.
    [GeneratedRegex(@"(?<![\p{L}\p{N}]|[0-9]\.)[1-9][0-9]{10}(?![\p{L}\p{N}]|\.[0-9])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Candidate();
}
