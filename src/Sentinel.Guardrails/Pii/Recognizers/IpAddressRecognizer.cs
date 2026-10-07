using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Sentinel.Guardrails.Pii.Recognizers;

/// <summary>
/// IPv4 (four octets 0-255, not a slice of a longer dotted number such as a version "1.2.3.4.5") and IPv6
/// (including compressed and IPv4-mapped forms). IPv6 candidates are loose on purpose and validated by
/// <see cref="IPAddress.TryParse(string?, out IPAddress?)"/>, so times ("12:30:45") and MAC addresses are rejected.
/// </summary>
internal sealed partial class IpAddressRecognizer : IPiiRecognizer
{
    private const double V4Confidence = 0.85;
    private const double V6Confidence = 0.9;
    private const string Octet = @"(?:25[0-5]|2[0-4][0-9]|[01]?[0-9]{1,2})";

    public PiiType Type => PiiType.IpAddress;

    public IEnumerable<PiiMatch> Recognize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var matches = new List<PiiMatch>();
        foreach (var match in V4().EnumerateMatches(text))
        {
            matches.Add(new PiiMatch(Type, match.Index, match.Length, V4Confidence));
        }

        foreach (var match in V6Candidate().EnumerateMatches(text))
        {
            var length = ValidV6Length(text.AsSpan(match.Index, match.Length));
            if (length > 0)
            {
                matches.Add(new PiiMatch(Type, match.Index, length, V6Confidence));
            }
        }

        return matches;
    }

    /// <summary>Length of the valid IPv6 address the candidate starts with, or 0.</summary>
    private static int ValidV6Length(ReadOnlySpan<char> candidate)
    {
        // "2001:db8::1:" at the end of a sentence: retry without a dangling single colon.
        if (!IsV6(candidate) && candidate.Length > 2 && candidate[^1] == ':' && candidate[^2] != ':')
        {
            candidate = candidate[..^1];
        }

        return IsV6(candidate) && LooksLikeAddress(candidate) ? candidate.Length : 0;
    }

    private static bool IsV6(ReadOnlySpan<char> candidate) =>
        IPAddress.TryParse(candidate, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>
    /// "a::b", "dead::beef" and "::" parse as IPv6 but are far more likely C++/Ruby scope operators or prose;
    /// require a decimal digit or at least three hex groups.
    /// </summary>
    private static bool LooksLikeAddress(ReadOnlySpan<char> candidate)
    {
        var groups = 0;
        var inGroup = false;
        foreach (var c in candidate)
        {
            if (c is >= '0' and <= '9')
            {
                return true;
            }

            if (c == ':' || c == '.')
            {
                inGroup = false;
            }
            else if (!inGroup)
            {
                inGroup = true;
                groups++;
            }
        }

        return groups >= 3;
    }

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}]|[\p{N}]\.)" + Octet + @"(?:\." + Octet + @"){3}(?![\p{L}\p{N}]|\.[\p{N}])",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex V4();

    // Two to seven "hex:" groups (empty groups give "::") and a final hex group or embedded IPv4.
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}:.])(?:[0-9A-Fa-f]{0,4}:){2,7}(?:(?:[0-9]{1,3}\.){3}[0-9]{1,3}|[0-9A-Fa-f]{0,4})(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex V6Candidate();
}
