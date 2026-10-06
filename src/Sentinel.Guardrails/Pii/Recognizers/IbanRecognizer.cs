using System.Text.RegularExpressions;

namespace Sentinel.Guardrails.Pii.Recognizers;

/// <summary>
/// IBAN in electronic (compact) or print format (groups of four separated by single spaces), any letter case.
/// A candidate must have the registry length for its country and pass mod-97.
/// </summary>
internal sealed partial class IbanRecognizer : IPiiRecognizer
{
    private const double Confidence = 0.99;
    private const int MaxLength = 34;

    public PiiType Type => PiiType.Iban;

    public IEnumerable<PiiMatch> Recognize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var matches = new List<PiiMatch>();
        Span<char> compact = stackalloc char[MaxLength + 8];
        foreach (var match in Candidate().EnumerateMatches(text))
        {
            var candidate = text.AsSpan(match.Index, match.Length);
            Span<char> country = [char.ToUpperInvariant(candidate[0]), char.ToUpperInvariant(candidate[1])];
            if (!IbanRegistry.ByCountry.TryGetValue(country, out var expected))
            {
                continue;
            }

            // The print format may run into a following group-like word ("BE68 5390 0754 7034 test"); cut the
            // candidate at the group boundary where the country's length is reached.
            var length = 0;
            var end = 0;
            while (end < candidate.Length && length < expected)
            {
                var c = candidate[end++];
                if (c != ' ')
                {
                    compact[length++] = char.ToUpperInvariant(c);
                }
            }

            if (length != expected || (end < candidate.Length && candidate[end] != ' '))
            {
                continue;
            }

            if (Checksums.IsIbanMod97Valid(compact[..length]))
            {
                matches.Add(new PiiMatch(Type, match.Index, end, Confidence));
            }
        }

        return matches;
    }

    // Country code, check digits, then up to 30 BBAN characters as groups of four with optional single spaces.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])[A-Za-z]{2}[0-9]{2}(?: ?[A-Za-z0-9]{4}){2,7}(?: ?[A-Za-z0-9]{1,3})?(?![\p{L}\p{N}])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Candidate();
}
