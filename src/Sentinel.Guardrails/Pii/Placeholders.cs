using System.Text.RegularExpressions;

namespace Sentinel.Guardrails.Pii;

/// <summary>
/// The exact token shape <see cref="PiiVault"/> produces: <c>[LABEL_n]</c> with a known label and n ≥ 1. Kept
/// strict on purpose: only tokens that cannot contain personal data are protected from redaction, so wrapping
/// a value in brackets ("[EMAIL_john@x.com]") does not smuggle it past the redactor.
/// </summary>
internal static partial class Placeholders
{
    /// <summary>Longest possible token ("[PHONE_123456789]" is 17); streaming holds back an open bracket this long.</summary>
    public const int MaxLength = 24;

    [GeneratedRegex(@"\[(?:TCKN|VKN|IBAN|CARD|PHONE|EMAIL|IP|PII)_[1-9][0-9]{0,8}\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    public static partial Regex Token();
}
