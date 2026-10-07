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
        AddBareMobile(matches, text);
        Add(matches, International(), text, InternationalConfidence);
        return matches;
    }

    /// <summary>
    /// "5xx xxx xx xx" is only a phone number when it looks like one: written with separators, or announced by a word
    /// such as "tel" or "gsm". Ten bare digits starting with 5 are just as often an order or customer number, and redacting
    /// those would make the text unusable without protecting anything.
    /// </summary>
    private void AddBareMobile(List<PiiMatch> matches, string text)
    {
        foreach (var match in TurkishBareMobile().EnumerateMatches(text))
        {
            var span = text.AsSpan(match.Index, match.Length);
            if (span.IndexOfAny(" .-") >= 0 || HasPhoneContext(text, match.Index, match.Length))
            {
                matches.Add(new PiiMatch(Type, match.Index, match.Length, BareMobileConfidence));
            }
        }
    }

    private static readonly string[] PhoneKeywords =
        ["tel", "gsm", "cep", "phone", "mobile", "mobil", "whatsapp", "arayın", "arayiniz", "arayabilir", "arasın", "contact", "call"];

    /// <summary>A phone word shortly before ("Tel: ...") or after ("... numarasından arayın") the digits.</summary>
    private static bool HasPhoneContext(string text, int start, int length)
    {
        const int Window = 24;
        var from = Math.Max(0, start - Window);
        var end = Math.Min(text.Length, start + length + Window);
        var window = string.Concat(text.AsSpan(from, start - from), " ", text.AsSpan(start + length, end - start - length));
        foreach (var keyword in PhoneKeywords)
        {
            if (window.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
