using System.Text.RegularExpressions;

namespace Sentinel.Guardrails.Pii.Recognizers;

/// <summary>
/// E-mail addresses: a practical RFC 5322 subset (dot-atom local part, DNS labels, alphabetic or punycode TLD).
/// Unicode letters are accepted in both parts: "ahmet.yılmaz@örnek.com.tr" must not slip through because of one
/// dotless i. Every quantifier is bounded so a candidate can never exceed <see cref="MaxCandidateLength"/>.
/// </summary>
internal sealed partial class EmailRecognizer : IPiiRecognizer
{
    private const double Confidence = 0.95;

    /// <summary>64 (local) + 1 + 6 labels × 64 + 24 (TLD); the RFC limit of 254 is enforced on top.</summary>
    internal const int MaxCandidateLength = 473;

    private const int MaxAddressLength = 254;

    public PiiType Type => PiiType.Email;

    public IEnumerable<PiiMatch> Recognize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var matches = new List<PiiMatch>();
        foreach (var match in Candidate().EnumerateMatches(text))
        {
            if (match.Length <= MaxAddressLength)
            {
                matches.Add(new PiiMatch(Type, match.Index, match.Length, Confidence));
            }
        }

        return matches;
    }

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}._%+-])" +
        @"[\p{L}\p{N}](?:[\p{L}\p{N}._%+-]{0,62}[\p{L}\p{N}_%+-])?" +
        @"@" +
        @"(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,61}[\p{L}\p{N}])?\.){1,6}" +
        @"(?:xn--[\p{L}\p{N}-]{1,20}|\p{L}{2,24})" +
        @"(?![\p{L}\p{N}_-]|\.[\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Candidate();
}
