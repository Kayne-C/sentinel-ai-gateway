namespace Sentinel.Guardrails.Injection;

/// <summary>
/// Runs several detectors (e.g. heuristics plus a model-based classifier) concurrently and blocks when any of
/// them does. Scores are not averaged: one confident detector must not be outvoted by others that missed.
/// </summary>
public sealed class CompositeInjectionDetector : IPromptInjectionDetector
{
    public const string FailureRule = "composite.detector_failed";

    private readonly IReadOnlyList<IPromptInjectionDetector> _detectors;

    public CompositeInjectionDetector(IReadOnlyList<IPromptInjectionDetector> detectors)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        if (detectors.Count == 0)
        {
            throw new ArgumentException("At least one detector is required.", nameof(detectors));
        }

        _detectors = detectors;
    }

    public async ValueTask<InjectionVerdict> InspectAsync(string text, ContentOrigin origin, CancellationToken cancellationToken = default)
    {
        var verdicts = await Task.WhenAll(_detectors.Select(d => InspectOneAsync(d, text, origin, cancellationToken))).ConfigureAwait(false);

        var rules = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var verdict in verdicts)
        {
            rules.UnionWith(verdict.Rules);
        }

        return new InjectionVerdict(
            verdicts.Any(v => v.IsAttack),
            verdicts.Max(v => v.Score),
            [.. rules],
            string.Join('+', verdicts.Select(v => v.Detector)));
    }

    /// <summary>
    /// The contract says detectors do not throw; one that does anyway is treated as a positive (fail closed),
    /// since an attacker who can crash a detector must not thereby skip it. Cancellation still propagates.
    /// </summary>
    private static async Task<InjectionVerdict> InspectOneAsync(
        IPromptInjectionDetector detector, string text, ContentOrigin origin, CancellationToken cancellationToken)
    {
        try
        {
            return await detector.InspectAsync(text, origin, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new InjectionVerdict(true, 1, [FailureRule], detector.GetType().Name);
        }
    }
}
