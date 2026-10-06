namespace Sentinel.Guardrails.Pii;

/// <summary>
/// Per-request, in-memory mapping between placeholders and original values. It is never persisted, cached or
/// logged; it lives exactly as long as one request so answers can be re-personalised for the caller.
/// </summary>
public sealed class PiiVault
{
    private readonly Dictionary<(PiiType Type, string Value), string> _placeholders = [];
    private readonly Dictionary<string, (string Value, PiiOrigin Origin)> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<PiiType, int> _counters = [];
    private readonly Dictionary<PiiType, int> _occurrences = [];

    /// <summary>Total redacted occurrences per type (not distinct values), for audit and metrics.</summary>
    public IReadOnlyDictionary<PiiType, int> Occurrences => _occurrences;

    public bool IsEmpty => _values.Count == 0;

    public int DistinctValues => _values.Count;

    /// <summary>Returns the placeholder for <paramref name="value"/>, creating one on first sight.</summary>
    public string Protect(PiiType type, string value, PiiOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(value);
        var key = (type, Canonicalize(type, value));
        _occurrences[type] = _occurrences.GetValueOrDefault(type) + 1;

        if (_placeholders.TryGetValue(key, out var existing))
        {
            // A value the caller typed stays restorable even if a document mentions it too.
            if (origin == PiiOrigin.Caller && _values[existing].Origin == PiiOrigin.Context)
            {
                _values[existing] = (_values[existing].Value, PiiOrigin.Caller);
            }

            return existing;
        }

        var next = _counters.GetValueOrDefault(type) + 1;
        _counters[type] = next;
        var placeholder = $"[{Label(type)}_{next}]";
        _placeholders[key] = placeholder;
        _values[placeholder] = (value, origin);
        return placeholder;
    }

    /// <summary>Resolves a placeholder; context-origin values only when <paramref name="includeContext"/> is set.</summary>
    public bool TryReveal(string placeholder, bool includeContext, out string value)
    {
        if (_values.TryGetValue(placeholder, out var entry) && (entry.Origin == PiiOrigin.Caller || includeContext))
        {
            value = entry.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public static string Label(PiiType type) => type switch
    {
        PiiType.NationalId => "TCKN",
        PiiType.TaxNumber => "VKN",
        PiiType.Iban => "IBAN",
        PiiType.PaymentCard => "CARD",
        PiiType.PhoneNumber => "PHONE",
        PiiType.Email => "EMAIL",
        PiiType.IpAddress => "IP",
        _ => "PII",
    };

    /// <summary>Formatting differences ("TR33 0006 1005…" vs "TR3300061005…") must not create two placeholders.</summary>
    private static string Canonicalize(PiiType type, string value) => type switch
    {
        PiiType.Email => value.Trim().ToLowerInvariant(),
        PiiType.IpAddress => value.Trim(),
        _ => new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()),
    };
}
