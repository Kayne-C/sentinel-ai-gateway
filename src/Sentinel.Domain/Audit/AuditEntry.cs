namespace Sentinel.Domain.Audit;

/// <summary>A committed audit event: its position in the tenant's chain and the chain hashes.</summary>
public sealed record AuditEntry(long Sequence, AuditEvent Event, string? PreviousHash, string Hash);
