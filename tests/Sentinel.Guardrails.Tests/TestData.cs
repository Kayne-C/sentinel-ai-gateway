using System.Text;
using Microsoft.Extensions.Options;
using Sentinel.Guardrails.Injection;
using Sentinel.Guardrails.Pii;
using Sentinel.Guardrails.Pii.Recognizers;

namespace Sentinel.Guardrails.Tests;

/// <summary>
/// Generators for valid identifiers. The check digits are computed here independently of the production code,
/// so a bug in the production algorithm cannot make its own tests pass.
/// </summary>
internal static class TestData
{
    public static string NationalId(Random random)
    {
        var d = new int[11];
        d[0] = random.Next(1, 10);
        for (var i = 1; i < 9; i++)
        {
            d[i] = random.Next(0, 10);
        }

        var odd = d[0] + d[2] + d[4] + d[6] + d[8];
        var even = d[1] + d[3] + d[5] + d[7];
        d[9] = ((odd * 7 - even) % 10 + 10) % 10;
        d[10] = (odd + even + d[9]) % 10;
        return string.Concat(d);
    }

    public static string TaxNumber(Random random)
    {
        var prefix = string.Concat(Enumerable.Range(0, 9).Select(_ => random.Next(0, 10)));
        for (var check = 0; check < 10; check++)
        {
            var candidate = prefix + check;
            if (IsValidTaxNumber(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Every 9-digit prefix has exactly one VKN check digit.");
    }

    public static bool IsValidTaxNumber(string value)
    {
        var sum = 0;
        for (var position = 1; position <= 9; position++)
        {
            var t = (value[position - 1] - '0' + 10 - position) % 10;
            var v = t == 9 ? 9 : t * (int)Math.Pow(2, 10 - position) % 9;
            sum += v;
        }

        return (10 - sum % 10) % 10 == value[9] - '0';
    }

    /// <summary>An IBAN with a numeric BBAN of the registry length and correct check digits.</summary>
    public static string Iban(string country, int length, Random random)
    {
        var bban = string.Concat(Enumerable.Range(0, length - 4).Select(_ => random.Next(0, 10)));
        var numeric = new StringBuilder(bban);
        foreach (var c in country + "00")
        {
            numeric.Append(char.IsLetter(c) ? (c - 'A' + 10).ToString(System.Globalization.CultureInfo.InvariantCulture) : c.ToString());
        }

        var remainder = 0;
        foreach (var c in numeric.ToString())
        {
            remainder = (remainder * 10 + (c - '0')) % 97;
        }

        return $"{country}{98 - remainder:00}{bban}";
    }

    /// <summary>A Luhn-valid PAN with the given prefix and length.</summary>
    public static string Card(string prefix, int length, Random random)
    {
        var body = new StringBuilder(prefix);
        while (body.Length < length - 1)
        {
            body.Append(random.Next(0, 10));
        }

        for (var check = 0; check < 10; check++)
        {
            var candidate = body.ToString() + check;
            if (IsLuhnValid(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Every prefix has a Luhn check digit.");
    }

    public static bool IsLuhnValid(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d = d * 2 > 9 ? d * 2 - 9 : d * 2;
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    /// <summary>Groups of four separated by single spaces (IBAN print format, card format).</summary>
    public static string Grouped(string value) =>
        string.Join(' ', Enumerable.Range(0, (value.Length + 3) / 4).Select(i => value.Substring(i * 4, Math.Min(4, value.Length - i * 4))));

    /// <summary>Encodes ASCII as invisible Unicode tag characters (U+E0020-E007E).</summary>
    public static string Tags(string ascii) => string.Concat(ascii.Select(c => char.ConvertFromUtf32(0xE0000 + c)));
}

/// <summary>Production objects wired by hand (no container) for focused tests.</summary>
internal static class Guards
{
    public static IPiiRecognizer[] Recognizers() =>
    [
        new NationalIdRecognizer(),
        new TaxNumberRecognizer(),
        new IbanRecognizer(),
        new PaymentCardRecognizer(),
        new PhoneNumberRecognizer(),
        new EmailRecognizer(),
        new IpAddressRecognizer(),
    ];

    public static PiiRedactor Redactor(PiiOptions? options = null) => new(Recognizers(), Options.Create(options ?? new PiiOptions()));

    public static OutputGuard Output(PiiOptions? options = null)
    {
        options ??= new PiiOptions();
        return new OutputGuard(Redactor(options), Options.Create(options));
    }

    public static HeuristicInjectionDetector Detector(InjectionOptions? options = null) =>
        new(Options.Create(options ?? new InjectionOptions()));

    /// <summary>Values the recognizer found, as substrings of the input.</summary>
    public static string[] Found(this IPiiRecognizer recognizer, string text) =>
        [.. recognizer.Recognize(text).Select(m => text.Substring(m.Start, m.Length))];
}
