using Microsoft.Extensions.Logging;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Application.Common;
using Sentinel.Domain.Audit;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Knowledge;

namespace Sentinel.Application.Features.Documents;

/// <summary>Removes a document with its ACL and chunks; cached answers citing it are dropped before and after.</summary>
internal sealed partial class DeleteDocumentCommandHandler(
    IKnowledgeRepository repository,
    ISemanticCache semanticCache,
    IAuditLog auditLog,
    TimeProvider timeProvider,
    ILogger<DeleteDocumentCommandHandler> logger)
    : ICommandHandler<DeleteDocumentCommand>
{
    public async Task<Result> Handle(DeleteDocumentCommand request, CancellationToken cancellationToken)
    {
        var caller = request.Caller;
        if (!caller.IsInRole(SentinelRoles.Admin))
        {
            return ApplicationErrors.AdminRequired;
        }

        var startedAt = timeProvider.GetTimestamp();
        var tenantId = caller.TenantId;
        var externalId = request.ExternalId.Trim();

        var document = await repository.FindAsync(tenantId, externalId, cancellationToken);
        if (document is null)
        {
            return KnowledgeErrors.NotFound(externalId);
        }

        // Refuse to delete while the cache is unreachable: a retry then still knows which document to invalidate.
        await semanticCache.InvalidateDocumentAsync(tenantId, document.Id, cancellationToken);

        if (!await repository.DeleteAsync(tenantId, externalId, cancellationToken))
        {
            return KnowledgeErrors.NotFound(externalId);
        }

        LogDocumentDeleted(logger, document.Id);

        await DocumentChangeEffects.CompleteAsync(semanticCache, auditLog, document.Id, new AuditEvent
        {
            TenantId = tenantId,
            SubjectId = caller.SubjectId,
            Operation = AuditOperation.DocumentDeleted,
            Outcome = AuditOutcome.Allowed,
            OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            Sources = [$"{document.ExternalId}:{document.Version}"],
            LatencyMs = timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
            TraceId = DocumentChangeEffects.CurrentTraceId(),
            Subject = document.ExternalId,
        });

        return Result.Success();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Document {DocumentId} deleted")]
    private static partial void LogDocumentDeleted(ILogger logger, Guid documentId);
}
