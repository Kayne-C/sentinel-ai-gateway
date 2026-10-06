using System.Text.RegularExpressions;

namespace Sentinel.Guardrails.Pii.Recognizers;

/// <summary>
/// Turkish mobile and landline numbers in the usual written forms, plus international E.164 numbers. The
/// boundaries reject digits glued to letters or digits, dates (2025-06-01, 01.06.2025), times and slices of
/// longer dotted or dashed numbers. A bare "5xx..." mobile has no prefix that marks it as a phone number, so it
/// must not start right after another digit group (it could be a slice of an IBAN or card).
/// </summary>
internal sealed partial class PhoneNumberRecognizer : IPiiRecognizer
{
    private const double PrefixedConfidence = 0.9;
    private const double BareMobileConfidence = 0.75;
    private const double InternationalConfidence = 0.8;

    public PiiType Type => PiiType.PhoneNumber;

    public IEnumerable<PiiMatch> Recognize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var matches = new List<PiiMatch>();
        Add(matches, TurkishPrefixed(), text, PrefixedConfidence);
        Add(matches, TurkishBareMobile(), text, BareMobileConfidence);
        Add(matches, International(), text, InternationalConfidence);
        return matches;
    }

    private void Add(List<PiiMatch> matches, Regex regex, string text, double confidence)
    {
        foreach (var match in regex.EnumerateMatches(text))
        {
            matches.Add(new PiiMatch(Type, match.Index, match.Length, confidence));
        }
    }

    // +90 / 0090 (optionally with the trunk 0 kept by mistake), trunk 0, or a parenthesised area code. Area codes
    // start with 2-5 (landline/mobile) or 8 (0850, 0800). Subscriber number: 3+2+2 digits with optional single
    // space/dot/dash separators.
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}+]|[0-9][.:/-])" +
        @"(?:(?:\+|00)90[ .-]?(?:\(0?[2-58][0-9]{2}\)|0?[2-58][0-9]{2})" +
        @"|0[ .-]?(?:\([2-58][0-9]{2}\)|[2-58][0-9]{2})" +
        @"|\(0?[2-58][0-9]{2}\))" +
        @"[ .-]?[0-9]{3}[ .-]?[0-9]{2}[ .-]?[0-9]{2}" +
        @"(?![\p{L}\p{N}]|[.-][0-9])",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex TurkishPrefixed();

    // 5xx xxx xx xx without any prefix.
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}+]|[0-9][ .:/-])5[0-9]{2}[ .-]?[0-9]{3}[ .-]?[0-9]{2}[ .-]?[0-9]{2}(?![\p{L}\p{N}]|[.-][0-9])",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex TurkishBareMobile();

    // E.164: "+", a non-zero country digit, 7-14 more digits; single separators or a parenthesised group.
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}+]|[0-9][.:/-])\+[1-9](?:(?:[ .-]|\) ?| ?\()?[0-9]){7,14}(?![\p{L}\p{N}]|[ .-][0-9])",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex International();
}
