namespace Sentinel.Guardrails.Pii.Recognizers;

/// <summary>
/// Check-digit algorithms. Recognizers only report numbers whose check digits are valid, which is what keeps
/// order numbers, invoice ids and timestamps from being masked as personal data.
/// </summary>
internal static class Checksums
{
    /// <summary>T.C. Kimlik No: 11 ASCII digits, non-zero first digit, two check digits (d10, d11).</summary>
    public static bool IsValidNationalId(ReadOnlySpan<char> digits)
    {
        if (digits.Length != 11 || !IsAsciiDigits(digits) || digits[0] == '0')
        {
            return false;
        }

        var odd = 0;
        var even = 0;
        for (var i = 0; i < 9; i++)
        {
            if (i % 2 == 0)
            {
                odd += digits[i] - '0';
            }
            else
            {
                even += digits[i] - '0';
            }
        }

        // (odd * 7 - even) can be negative; C#'s % keeps the dividend's sign, so normalise into 0..9.
        var tenth = (((odd * 7) - even) % 10 + 10) % 10;
        if (tenth != digits[9] - '0')
        {
            return false;
        }

        var sum = odd + even + tenth;
        return sum % 10 == digits[10] - '0';
    }

    /// <summary>Vergi Kimlik No: 10 ASCII digits, last one is the GİB check digit.</summary>
    public static bool IsValidTaxNumber(ReadOnlySpan<char> digits)
    {
        if (digits.Length != 10 || !IsAsciiDigits(digits))
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            // 1-based position p = i + 1: t = (d + 10 - p) % 10; v = t * 2^(10 - p) mod 9, except t = 9 stays 9.
            var t = (digits[i] - '0' + 9 - i) % 10;
            var v = t == 9 ? 9 : (t * (1 << (9 - i))) % 9;
            sum += v;
        }

        return (10 - (sum % 10)) % 10 == digits[9] - '0';
    }

    /// <summary>Luhn (mod 10) over ASCII digits.</summary>
    public static bool IsLuhnValid(ReadOnlySpan<char> digits)
    {
        if (digits.IsEmpty || !IsAsciiDigits(digits))
        {
            return false;
        }

        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    /// <summary>ISO 7064 MOD 97-10 over a compact, upper-case IBAN (country code moved to the end).</summary>
    public static bool IsIbanMod97Valid(ReadOnlySpan<char> compactUpper)
    {
        if (compactUpper.Length < 5)
        {
            return false;
        }

        var remainder = 0;
        for (var n = 0; n < compactUpper.Length; n++)
        {
            var c = compactUpper[(n + 4) % compactUpper.Length];
            if (c is >= '0' and <= '9')
            {
                remainder = ((remainder * 10) + (c - '0')) % 97;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                remainder = ((remainder * 100) + (c - 'A' + 10)) % 97;
            }
            else
            {
                return false;
            }
        }

        return remainder == 1;
    }

    public static bool IsAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Copies the ASCII digits of <paramref name="value"/> (dropping separators) into <paramref name="destination"/>.</summary>
    public static int CopyDigits(ReadOnlySpan<char> value, Span<char> destination)
    {
        var length = 0;
        foreach (var c in value)
        {
            if (c is >= '0' and <= '9')
            {
                if (length == destination.Length)
                {
                    return -1;
                }

                destination[length++] = c;
            }
        }

        return length;
    }
}
