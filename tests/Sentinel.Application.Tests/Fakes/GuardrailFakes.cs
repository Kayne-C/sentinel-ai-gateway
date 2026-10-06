using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Sentinel.Guardrails.Injection;
using Sentinel.Guardrails.Pii;

namespace Sentinel.Application.Tests.Fakes;

/// <summary>Stands in for the real redactor: e-mail addresses and Turkish mobile numbers only.</summary>
internal sealed partial class RegexPiiRedactor : IPiiRedactor
{
    public RedactionResult Redact(string text, PiiVault vault, PiiOrigin origin)
    {
        var findings = new List<PiiFinding>();
        var redacted = Pii().Replace(text, match =>
        {
            var type = match.Value.Contains('@', StringComparison.Ordinal) ? PiiType.Email : PiiType.PhoneNumber;
            var placeholder = vault.Protect(type, match.Value, origin);
            findings.Add(new PiiFinding(type, match.Index, match.Length, placeholder));
            return placeholder;
        });

        return new RedactionResult(redacted, findings);
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}|\b05[0-9]{9}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pii();
}

/// <summary>Flags the classic override phrase; records everything it was shown.</summary>
internal sealed class KeywordInjectionDetector : IPromptInjectionDetector
{
    public const string Phrase = "ignore previous instructions";
    public const string Rule = "injection.override";

    public ConcurrentQueue<(string Text, ContentOrigin Origin)> Inspected { get; } = new();

    /// <summary>Score returned for clean text, to exercise the threshold logic.</summary>
    public double CleanScore { get; set; }

    public IReadOnlyList<string> CleanRules { get; set; } = [];

    public ValueTask<InjectionVerdict> InspectAsync(string text, ContentOrigin origin, CancellationToken cancellationToken = default)
    {
        Inspected.Enqueue((text, origin));
        return ValueTask.FromResult(text.Contains(Phrase, StringComparison.OrdinalIgnoreCase)
            ? new InjectionVerdict(true, 0.95, [Rule], "keyword")
            : new InjectionVerdict(false, CleanScore, CleanRules, "keyword"));
    }
}
