using System.Text.RegularExpressions;

namespace Sentinel.Guardrails.Pii.Recognizers;

/// <summary>
/// Payment card numbers (PAN). Luhn alone passes one random number in ten, so a candidate must also start with
/// a known issuer prefix (IIN) and have a length that issuer uses. Digits may be grouped with single spaces or
/// dashes. A card is never carved out of a glued digit run, but a grouped sequence is split at its group
/// boundaries: "4111 1111 1111 1111 12 27 123" (expiry, CVV) and "2024 4111 1111 1111 1111" still yield the card,
/// because a missed card is a leak while a mis-split one is merely masked.
/// </summary>
internal sealed partial class PaymentCardRecognizer : IPiiRecognizer
{
    private const double Confidence = 0.95;
    private const int MinDigits = 13;
    private const int MaxDigits = 19;

    public PiiType Type => PiiType.PaymentCard;

    public IEnumerable<PiiMatch> Recognize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var matches = new List<PiiMatch>();
        foreach (var run in Candidate().EnumerateMatches(text))
        {
            FindCards(text, run.Index, run.Length, matches);
        }

        return matches;
    }

    /// <summary>Left to right, at every group start, takes the longest valid card that ends at a group end.</summary>
    private void FindCards(string text, int runStart, int runLength, List<PiiMatch> matches)
    {
        var run = text.AsSpan(runStart, runLength);
        Span<char> pan = stackalloc char[MaxDigits];
        var position = 0;
        while (position < run.Length)
        {
            if (!IsDigit(run[position]) || (position > 0 && IsDigit(run[position - 1])))
            {
                position++;
                continue;
            }

            var found = 0;
            var count = 0;
            for (var i = position; i < run.Length && count < MaxDigits; i++)
            {
                if (!IsDigit(run[i]))
                {
                    continue;
                }

                pan[count++] = run[i];
                var groupEnd = i + 1 == run.Length || !IsDigit(run[i + 1]);
                if (count >= MinDigits && groupEnd && IsKnownIssuer(pan[..count]) && Checksums.IsLuhnValid(pan[..count]))
                {
                    found = i + 1 - position;
                }
            }

            if (found > 0)
            {
                matches.Add(new PiiMatch(Type, runStart + position, found, Confidence));
                position += found;
            }
            else
            {
                position++;
            }
        }
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    /// <summary>Issuer prefix and length: Visa, Mastercard, Amex, Discover, JCB, UnionPay, Troy.</summary>
    internal static bool IsKnownIssuer(ReadOnlySpan<char> pan)
    {
        var length = pan.Length;
        if (length < 13)
        {
            return false;
        }

        var p2 = Prefix(pan, 2);
        var p3 = Prefix(pan, 3);
        var p4 = Prefix(pan, 4);

        if (pan[0] == '4')
        {
            return length is 13 or 16 or 19;
        }

        if (p2 is >= 51 and <= 55 || p4 is >= 2221 and <= 2720)
        {
            return length == 16;
        }

        if (p2 is 34 or 37)
        {
            return length == 15;
        }

        if (p4 == 6011 || p3 is >= 644 and <= 649 || p2 == 65)
        {
            return length is >= 16 and <= 19;
        }

        if (p4 is >= 3528 and <= 3589)
        {
            return length is >= 16 and <= 19;
        }

        if (p2 == 62)
        {
            return length is >= 16 and <= 19;
        }

        if (p4 == 9792)
        {
            return length == 16;
        }

        return false;
    }

    private static int Prefix(ReadOnlySpan<char> digits, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++)
        {
            value = (value * 10) + (digits[i] - '0');
        }

        return value;
    }

    // A grouped digit run of at least 13 digits with optional single space/dash separators, not glued to letters
    // or digits and not part of a dotted number. Runs are matched greedily as a whole and split by FindCards.
    [GeneratedRegex(@"(?<![\p{L}\p{N}]|[0-9][.-])[0-9](?:[ -]?[0-9]){12,}(?![\p{L}\p{N}]|\.[0-9])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Candidate();
}
