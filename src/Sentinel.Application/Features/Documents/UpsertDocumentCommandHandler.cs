using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Application.Common;
using Sentinel.Application.Diagnostics;
using Sentinel.Application.Knowledge;
using Sentinel.Domain.Audit;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Knowledge;
using Sentinel.Guardrails.Injection;

namespace Sentinel.Application.Features.Documents;

/// <summary>
/// Creates or revises a document. New or changed content is chunked, every chunk is scanned for indirect prompt
/// injection (flagged chunks are stored but quarantined, so they can be reviewed and never reach a model), embedded
/// and stored together with the ACL in one transaction. An ACL-only change keeps the existing chunks and vectors.
/// Any change drops cached answers citing the document: an answer computed for yesterday's ACL must not be served to
/// someone the ACL no longer admits.
/// </summary>
internal sealed partial class UpsertDocumentCommandHandler(
    IKnowledgeRepository repository,
    IDocumentChunkCounter chunkCounter,
    TextChunker chunker,
    IPromptInjectionDetector injectionDetector,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ISemanticCache semanticCache,
    IAuditLog auditLog,
    TimeProvider timeProvider,
    ILogger<UpsertDocumentCommandHandler> logger)
    : ICommandHandler<UpsertDocumentCommand, DocumentResponse>
{
    internal const int EmbeddingBatchSize = 32;

    public async Task<Result<DocumentResponse>> Handle(UpsertDocumentCommand request, CancellationToken cancellationToken)
    {
        var caller = request.Caller;
        if (!caller.IsInRole(SentinelRoles.Admin))
        {
            return ApplicationErrors.AdminRequired;
        }

        var startedAt = timeProvider.GetTimestamp();
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var tenantId = caller.TenantId;
        var externalId = request.ExternalId.Trim();

        var document = await repository.FindAsync(tenantId, externalId, cancellationToken);
        bool reindex;
        if (document is null)
        {
            var created = KnowledgeDocument.Create(
                tenantId, externalId, request.Title, request.Content, request.SourceUri, request.Classification, request.Principals, utcNow);
            if (created.IsFailure)
            {
                return created.Error;
            }

            document = created.Value;
            reindex = true;
        }
        else
        {
            var previousTitle = document.Title;
            var revision = document.Revise(request.Title, request.Content, request.SourceUri, request.Classification, request.Principals, utcNow);
            if (revision.IsFailure)
            {
                return revision.Error;
            }

            if (!revision.Value.Any)
            {
                var unchanged = await chunkCounter.CountChunksAsync(tenantId, document.Id, cancellationToken);
                return ToResponse(document, unchanged, changed: false);
            }

            // Vectors are computed over "{title}\n\n{chunk}", so a new title needs new vectors and a new injection scan.
            reindex = revision.Value.ContentChanged || document.Title != previousTitle;

            // Invalidate before committing as well as after: if the cache cannot be reached the change is refused
            // (and can simply be retried) instead of going live while answers cached under the old ACL survive.
            await semanticCache.InvalidateDocumentAsync(tenantId, document.Id, cancellationToken);
        }

        IReadOnlyList<EmbeddedChunk>? embeddedChunks = null;
        IReadOnlyList<string> findings = [];
        if (reindex)
        {
            var chunked = chunker.Chunk(request.Content);
            if (chunked.IsFailure)
            {
                return chunked.Error;
            }

            var scanned = await ScanAsync(document, chunked.Value, cancellationToken);
            var embedded = await EmbedAsync(document, scanned.Chunks, cancellationToken);
            if (embedded.IsFailure)
            {
                return embedded.Error;
            }

            embeddedChunks = embedded.Value;
            findings = scanned.Findings;
        }

        try
        {
            await repository.SaveAsync(document, embeddedChunks, cancellationToken);
        }
        catch (KnowledgeConcurrencyException)
        {
            LogConcurrentModification(logger, document.Id);
            return KnowledgeIngestionErrors.ConcurrentModification(document.ExternalId);
        }

        // Committed: finish the side effects even if the caller disconnects now.
        var counts = embeddedChunks is null
            ? await chunkCounter.CountChunksAsync(tenantId, document.Id, CancellationToken.None)
            : new DocumentChunkCounts(embeddedChunks.Count, embeddedChunks.Count(c => c.Chunk.Quarantined));

        LogDocumentStored(logger, document.Id, document.Version, counts.Total, reindex);

        await DocumentChangeEffects.CompleteAsync(semanticCache, auditLog, document.Id, new AuditEvent
        {
            TenantId = tenantId,
            SubjectId = caller.SubjectId,
            Operation = AuditOperation.DocumentUpserted,
            Outcome = AuditOutcome.Allowed,
            OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            Model = reindex ? embeddingGenerator.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId : null,
            GuardrailFindings = findings,
            Sources = [$"{document.ExternalId}:{document.Version}"],
            LatencyMs = timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
            TraceId = DocumentChangeEffects.CurrentTraceId(),
            Subject = document.ExternalId,
        });

        return ToResponse(document, counts, changed: true);
    }

    /// <summary>The exact text that is embedded for a chunk; the title gives short chunks their topic.</summary>
    internal static string EmbeddingText(string title, string chunkText) => $"{title}\n\n{chunkText}";

    private async Task<ScanResult> ScanAsync(KnowledgeDocument document, IReadOnlyList<DocumentChunk> chunks, CancellationToken cancellationToken)
    {
        // The title travels with every chunk (embedding text, citations, prompt context): an injection in the title
        // taints all of them.
        var titleVerdict = await injectionDetector.InspectAsync(document.Title, ContentOrigin.RetrievedDocument, cancellationToken);
        var titleReasons = Reasons(titleVerdict);

        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var result = new List<DocumentChunk>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var verdict = await injectionDetector.InspectAsync(chunk.Text, ContentOrigin.RetrievedDocument, cancellationToken);
            var reasons = titleReasons.Concat(Reasons(verdict)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (reasons.Count == 0)
            {
                result.Add(chunk);
                continue;
            }

            findings.UnionWith(reasons);
            result.Add(chunk with { Quarantined = true, QuarantineReason = FormatReason(reasons) });
        }

        var quarantined = result.Count(c => c.Quarantined);
        if (quarantined > 0)
        {
            SentinelTelemetry.InjectionDetections.Add(
                quarantined,
                new KeyValuePair<string, object?>("origin", "document"),
                new KeyValuePair<string, object?>("action", "quarantined"));
            LogChunksQuarantined(logger, document.Id, quarantined, chunks.Count);
        }

        return new ScanResult(result, [.. findings]);
    }

    private async Task<Result<IReadOnlyList<EmbeddedChunk>>> EmbedAsync(
        KnowledgeDocument document, IReadOnlyList<DocumentChunk> chunks, CancellationToken cancellationToken)
    {
        var embedded = new List<EmbeddedChunk>(chunks.Count);
        for (var offset = 0; offset < chunks.Count; offset += EmbeddingBatchSize)
        {
            var batch = chunks.Skip(offset).Take(EmbeddingBatchSize).ToList();
            var inputs = batch.Select(c => EmbeddingText(document.Title, c.Text)).ToList();

            GeneratedEmbeddings<Embedding<float>> generated;
            try
            {
                generated = await embeddingGenerator.GenerateAsync(inputs, cancellationToken: cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Only the exception type: provider errors can echo request content.
                LogEmbeddingFailed(logger, document.Id, exception.GetType().Name);
                return KnowledgeIngestionErrors.EmbeddingUnavailable;
            }

            if (generated.Count != batch.Count)
            {
                LogEmbeddingCountMismatch(logger, document.Id, batch.Count, generated.Count);
                return KnowledgeIngestionErrors.EmbeddingUnavailable;
            }

            for (var i = 0; i < batch.Count; i++)
            {
                var vector = generated[i].Vector;
                if (!IsUsable(vector.Span))
                {
                    LogInvalidEmbedding(logger, document.Id, vector.Length, EmbeddingDefaults.Dimensions);
                    return KnowledgeIngestionErrors.InvalidEmbedding;
                }

                // Copy: the provider may reuse its buffers.
                embedded.Add(new EmbeddedChunk(batch[i], vector.ToArray()));
            }
        }

        return Result.Success<IReadOnlyList<EmbeddedChunk>>(embedded);
    }

    /// <summary>Right width, finite, not all zeros (cosine distance to a zero vector is undefined).</summary>
    private static bool IsUsable(ReadOnlySpan<float> vector)
    {
        if (vector.Length != EmbeddingDefaults.Dimensions)
        {
            return false;
        }

        var sumOfSquares = 0d;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                return false;
            }

            sumOfSquares += (double)value * value;
        }

        return sumOfSquares > 0;
    }

    private static IEnumerable<string> Reasons(InjectionVerdict verdict)
    {
        if (!verdict.IsAttack)
        {
            return [];
        }

        var rules = verdict.Rules.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim().Replace(',', '_')).ToList();
        return rules.Count > 0 ? rules : [$"detector:{verdict.Detector}"];
    }

    private static string FormatReason(IReadOnlyList<string> reasons)
    {
        var joined = string.Join(',', reasons);
        if (joined.Length <= KnowledgeLimits.QuarantineReasonMaxLength)
        {
            return joined;
        }

        var cut = joined.LastIndexOf(',', KnowledgeLimits.QuarantineReasonMaxLength - 1);
        return cut > 0 ? joined[..cut] : joined[..KnowledgeLimits.QuarantineReasonMaxLength];
    }

    private static DocumentResponse ToResponse(KnowledgeDocument document, DocumentChunkCounts counts, bool changed) => new(
        document.Id,
        document.ExternalId,
        document.Title,
        document.Classification,
        document.Version,
        counts.Total,
        counts.Quarantined,
        [.. document.Principals.Order(StringComparer.Ordinal)],
        changed,
        document.UpdatedAtUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Document {DocumentId} stored as version {Version} with {ChunkCount} chunks (re-indexed: {Reindexed})")]
    private static partial void LogDocumentStored(ILogger logger, Guid documentId, int version, int chunkCount, bool reindexed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Quarantined {QuarantinedCount} of {ChunkCount} chunks of document {DocumentId} as prompt injection")]
    private static partial void LogChunksQuarantined(ILogger logger, Guid documentId, int quarantinedCount, int chunkCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding provider failed for document {DocumentId} ({ExceptionType})")]
    private static partial void LogEmbeddingFailed(ILogger logger, Guid documentId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding provider returned {Actual} vectors for {Expected} inputs (document {DocumentId})")]
    private static partial void LogEmbeddingCountMismatch(ILogger logger, Guid documentId, int expected, int actual);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding provider returned an unusable vector of length {Length} (expected {Expected}) for document {DocumentId}")]
    private static partial void LogInvalidEmbedding(ILogger logger, Guid documentId, int length, int expected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} was modified concurrently; upsert rejected")]
    private static partial void LogConcurrentModification(ILogger logger, Guid documentId);

    private sealed record ScanResult(IReadOnlyList<DocumentChunk> Chunks, IReadOnlyList<string> Findings);
}
