namespace Sentinel.Guardrails.Pii;

/// <summary>Personal data the gateway removes before any text reaches a model, the cache or the audit log.</summary>
public enum PiiType
{
    /// <summary>T.C. Kimlik No — 11 digits, two check digits.</summary>
    NationalId,

    /// <summary>Vergi Kimlik No — 10 digits, mod-10 check digit.</summary>
    TaxNumber,

    /// <summary>IBAN, any country, ISO 13616 mod-97 check.</summary>
    Iban,

    /// <summary>Payment card number (PAN), 13–19 digits, Luhn check.</summary>
    PaymentCard,

    /// <summary>Turkish mobile/landline numbers and E.164 international numbers.</summary>
    PhoneNumber,

    Email,

    IpAddress,
}

/// <summary>Where a piece of text came from. Only caller-supplied values are ever restored in answers.</summary>
public enum PiiOrigin
{
    /// <summary>Typed by the caller (prompt, chat history): the caller already knows these values.</summary>
    Caller,

    /// <summary>Found in retrieved documents or tool output: stays masked in answers by default.</summary>
    Context,
}

/// <summary>A recognised span in the original text. Values are never logged.</summary>
public readonly record struct PiiMatch(PiiType Type, int Start, int Length, double Confidence);

/// <summary>A redacted span: its type, where it was in the original text and the placeholder that replaced it.</summary>
public readonly record struct PiiFinding(PiiType Type, int Start, int Length, string Placeholder);

public interface IPiiRecognizer
{
    PiiType Type { get; }

    IEnumerable<PiiMatch> Recognize(string text);
}

public sealed record RedactionResult(string Text, IReadOnlyList<PiiFinding> Findings)
{
    public bool HasPii => Findings.Count > 0;
}

public interface IPiiRedactor
{
    /// <summary>
    /// Replaces every recognised entity with a typed placeholder such as <c>[EMAIL_1]</c>. Overlapping matches are
    /// resolved (longest, then most confident wins); the same value always maps to the same placeholder within one
    /// <paramref name="vault"/>, so the model can still refer to "the same IBAN" consistently.
    /// </summary>
    RedactionResult Redact(string text, PiiVault vault, PiiOrigin origin);
}

public sealed class PiiOptions
{
    public const string Section = "Guardrails:Pii";

    public bool Enabled { get; set; } = true;

    /// <summary>Entity types to redact; all by default.</summary>
    public HashSet<PiiType> Types { get; set; } = [.. Enum.GetValues<PiiType>()];

    /// <summary>Restore caller-supplied values in model answers (the caller typed them, so nothing leaks).</summary>
    public bool RestoreCallerValuesInAnswers { get; set; } = true;
}
