namespace Sentinel.Application.Knowledge;

public static class KnowledgeLimits
{
    /// <summary>Column width for the comma-joined guardrail rule ids that quarantined a chunk.</summary>
    public const int QuarantineReasonMaxLength = 512;

    public const int SourceUriMaxLength = 2048;

    /// <summary>Entra tenant ids are GUIDs; the slack allows other identity providers' tenant keys.</summary>
    public const int TenantIdMaxLength = 128;
}
