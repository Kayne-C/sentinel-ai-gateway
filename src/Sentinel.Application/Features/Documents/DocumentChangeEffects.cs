using System.Diagnostics;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Audit;

namespace Sentinel.Application.Features.Documents;

/// <summary>Side effects of a committed knowledge-base change, shared by upsert and delete.</summary>
internal static class DocumentChangeEffects
{
    /// <summary>
    /// Drops cached answers citing the document, then records the change. Runs without the request's cancellation
    /// token because the change is already committed; the audit entry is written even when invalidation fails, and
    /// the invalidation failure still surfaces to the caller afterwards.
    /// </summary>
    public static async Task CompleteAsync(ISemanticCache semanticCache, IAuditLog auditLog, Guid documentId, AuditEvent auditEvent)
    {
        try
        {
            await semanticCache.InvalidateDocumentAsync(auditEvent.TenantId, documentId, CancellationToken.None);
        }
        finally
        {
            await auditLog.AppendAsync(auditEvent, CancellationToken.None);
        }
    }

    public static string? CurrentTraceId() => Activity.Current?.TraceId.ToString();
}
