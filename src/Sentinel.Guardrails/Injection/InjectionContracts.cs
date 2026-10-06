namespace Sentinel.Guardrails.Injection;

/// <summary>Where text came from; indirect injections hide in documents and tool output.</summary>
public enum ContentOrigin
{
    User,
    System,
    RetrievedDocument,
    ToolResult,
}

public sealed record InjectionVerdict(bool IsAttack, double Score, IReadOnlyList<string> Rules, string Detector)
{
    public static InjectionVerdict Clean(string detector) => new(false, 0, [], detector);
}

public interface IPromptInjectionDetector
{
    /// <summary>Inspects one piece of text. Must not throw for any input; must not call out unless configured to.</summary>
    ValueTask<InjectionVerdict> InspectAsync(string text, ContentOrigin origin, CancellationToken cancellationToken = default);
}

public sealed class InjectionOptions
{
    public const string Section = "Guardrails:Injection";

    public bool Enabled { get; set; } = true;

    /// <summary>Score (0–1) at or above which input is treated as an attack.</summary>
    public double BlockThreshold { get; set; } = 0.7;

    /// <summary>Scan retrieved chunks (indirect injection) and drop flagged ones from the context.</summary>
    public bool InspectRetrievedContent { get; set; } = true;
}
