using Microsoft.Extensions.Options;

namespace Sentinel.Guardrails.Pii;

/// <summary>
/// Post-processes model output for the caller. Order matters: new PII the model produced (from its weights, a
/// tool, or a document it paraphrased) is redacted first with origin <see cref="PiiOrigin.Context"/>, so it can
/// never be restored; only then are the caller's own placeholders put back. Restoring first would let a
/// restored value be mistaken for model-generated PII, and redacting after restoring would mask the caller's
/// own data in front of them.
/// </summary>
public sealed class OutputGuard(IPiiRedactor redactor, IOptions<PiiOptions> options)
{
    internal IPiiRedactor Redactor { get; } = redactor ?? throw new ArgumentNullException(nameof(redactor));

    public string Apply(string modelText, PiiVault vault)
    {
        ArgumentNullException.ThrowIfNull(modelText);
        ArgumentNullException.ThrowIfNull(vault);

        var redacted = Redactor.Redact(modelText, vault, PiiOrigin.Context).Text;
        return Restore(redacted, vault);
    }

    /// <summary>A guard for one streamed answer; it shares <paramref name="vault"/> with the request.</summary>
    public StreamingOutputGuard CreateStream(PiiVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        return new StreamingOutputGuard(this, vault);
    }

    internal string Restore(string redacted, PiiVault vault, string leadingContext = "") =>
        options.Value.RestoreCallerValuesInAnswers ? PiiRestorer.Restore(redacted, vault, includeContextValues: false, leadingContext) : redacted;
}
