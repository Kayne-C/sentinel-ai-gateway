using System.Text;
using System.Text.RegularExpressions;

namespace Sentinel.Infrastructure.AI.Offline;

/// <summary>
/// Lexical normalisation shared by the offline embedding generator and the extractive model, so that what one
/// considers "the same word" the other does too.
/// </summary>
internal static partial class OfflineText
{
    /// <summary>Longer runs are truncated: they are noise (hashes, base64) and would only cost time.</summary>
    public const int MaxWordLength = 64;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // Turkish (folded)
        "acaba", "ama", "ancak", "bana", "ben", "bir", "biz", "bu", "cok", "da", "daha", "de", "defa", "diye", "dir", "en",
        "gibi", "gore", "hakkinda", "hangi", "hem", "hep", "her", "hic", "icin", "ile", "ise", "kac", "kadar", "ki", "kim",
        "mi", "midir", "mu", "mudur", "nasil", "ne", "neden", "nedir", "nelerdir", "nerede", "nicin", "o", "olan", "olarak",
        "sen", "siz", "su", "sonra", "ve", "veya", "ya", "yani",

        // English
        "a", "about", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "in",
        "is", "it", "many", "me", "much", "my", "of", "on", "or", "our", "the", "this", "that", "to", "was", "were", "what",
        "when", "where", "which", "who", "why", "with", "you", "your",
    };

    private static readonly HashSet<string> TurkishMarkers = new(StringComparer.Ordinal)
    {
        "acaba", "bir", "bu", "gore", "hakkinda", "hangi", "icin", "ile", "kac", "kadar", "kim", "mi", "midir", "mu",
        "mudur", "nasil", "ne", "neden", "nedir", "nelerdir", "nerede", "nicin", "olan", "var", "ve", "veya", "yok",
    };

    private static readonly HashSet<string> EnglishMarkers = new(StringComparer.Ordinal)
    {
        "are", "can", "do", "does", "for", "how", "is", "of", "the", "what", "when", "where", "which", "who", "why",
    };

    /// <summary>
    /// Turkish-aware case and diacritic folding: <c>I</c>, <c>ı</c>, <c>İ</c> and <c>i</c> all become <c>i</c> (so
    /// "IBAN", "İzmir" and "izmir" match whichever way they were capitalised), and ç ğ ö ş ü â î û lose their marks,
    /// because users routinely type Turkish on keyboards without them ("sifre" for "şifre").
    /// </summary>
    public static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case 'I' or 'ı' or 'İ' or 'i' or 'î' or 'Î':
                    builder.Append('i');
                    break;
                case 'Ç' or 'ç':
                    builder.Append('c');
                    break;
                case 'Ğ' or 'ğ':
                    builder.Append('g');
                    break;
                case 'Ö' or 'ö':
                    builder.Append('o');
                    break;
                case 'Ş' or 'ş':
                    builder.Append('s');
                    break;
                case 'Ü' or 'ü' or 'Û' or 'û':
                    builder.Append('u');
                    break;
                case 'Â' or 'â':
                    builder.Append('a');
                    break;
                case '̇':
                    // Combining dot above, left behind by a decomposed "İ".
                    break;
                default:
                    builder.Append(char.ToLowerInvariant(c));
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Maximal runs of letters and digits in already folded text.</summary>
    public static List<string> Words(string folded)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= folded.Length; i++)
        {
            var inWord = i < folded.Length && char.IsLetterOrDigit(folded[i]);
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                words.Add(folded.Substring(start, Math.Min(i - start, MaxWordLength)));
                start = -1;
            }
        }

        return words;
    }

    public static bool IsStopWord(string foldedWord) => StopWords.Contains(foldedWord);

    /// <summary>Placeholders carry no meaning ("[EMAIL_1]" is not about e-mail); they would only add noise.</summary>
    public static string StripPlaceholders(string text) => Placeholder().Replace(text, " ");

    /// <summary>Turkish when it has Turkish letters or more Turkish than English function words; English otherwise.</summary>
    public static bool LooksTurkish(string text)
    {
        if (text.AsSpan().IndexOfAny("çğıöşüÇĞİÖŞÜ") >= 0)
        {
            return true;
        }

        var turkish = 0;
        var english = 0;
        foreach (var word in Words(Fold(text)))
        {
            if (TurkishMarkers.Contains(word))
            {
                turkish++;
            }
            else if (EnglishMarkers.Contains(word))
            {
                english++;
            }
        }

        return turkish > english;
    }

    [GeneratedRegex(@"\[[A-Z]{2,8}_[0-9]{1,7}\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Placeholder();
}
