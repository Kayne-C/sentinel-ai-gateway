using Sentinel.Guardrails.Injection;

namespace Sentinel.Infrastructure.AI.ContentSafety;

/// <summary>
/// Runs several detectors on the same text concurrently and keeps the most severe result: an attack if any detector
/// says so, the highest score, and every rule that fired (for the audit trail). Defence in depth: a local heuristic
/// and a remote classifier fail in different ways.
/// </summary>
internal sealed class CompositePromptInjectionDetector : IPromptInjectionDetector
{
    private readonly IPromptInjectionDetector[] _detectors;

    public CompositePromptInjectionDetector(IEnumerable<IPromptInjectionDetector> detectors)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        _detectors = [.. detectors];
        if (_detectors.Length == 0)
        {
            throw new ArgumentException("At least one detector is required.", nameof(detectors));
        }
    }

    public async ValueTask<InjectionVerdict> InspectAsync(string text, ContentOrigin origin, CancellationToken cancellationToken = default)
    {
        var verdicts = await Task.WhenAll(_detectors.Select(d => d.InspectAsync(text, origin, cancellationToken).AsTask()));
        return Combine(verdicts);
    }

    internal static InjectionVerdict Combine(IReadOnlyList<InjectionVerdict> verdicts)
    {
        var attacks = verdicts.Where(v => v.IsAttack).ToList();
        var deciding = attacks.Count > 0 ? attacks : verdicts;
        return new InjectionVerdict(
            attacks.Count > 0,
            verdicts.Count == 0 ? 0 : verdicts.Max(v => v.Score),
            [.. verdicts.SelectMany(v => v.Rules).Distinct(StringComparer.Ordinal)],
            string.Join('+', deciding.Select(v => v.Detector).Distinct(StringComparer.Ordinal)));
    }
}
