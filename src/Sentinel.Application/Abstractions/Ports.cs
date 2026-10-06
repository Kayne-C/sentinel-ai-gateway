using Microsoft.Extensions.AI;
using Sentinel.Application.Common;
using Sentinel.Domain.Audit;
using Sentinel.Domain.Knowledge;

namespace Sentinel.Application.Abstractions;

// ---------------------------------------------------------------------------------------------------------------
// Knowledge base
// ---------------------------------------------------------------------------------------------------------------

/// <summary>A chunk together with its embedding (length <see cref="EmbeddingDefaults.Dimensions"/>).</summary>
public sealed record EmbeddedChunk(DocumentChunk Chunk, ReadOnlyMemory<float> Embedding);

public sealed record DocumentSummary(
    Guid Id, string ExternalId, string Title, Classification Classification, int Version, int ChunkCount,
    int QuarantinedChunkCount, IReadOnlyList<string> Principals, DateTime UpdatedAtUtc);

public interface IKnowledgeRepository
{
    Task<KnowledgeDocument?> FindAsync(string tenantId, string externalId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or updates the document and its ACL. When <paramref name="chunks"/> is not null, all existing chunks
    /// are replaced by these in the same transaction, so a reader never sees a half-updated document.
    /// </summary>
    Task SaveAsync(KnowledgeDocument document, IReadOnlyList<EmbeddedChunk>? chunks, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string tenantId, string externalId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists documents of a tenant. With <paramref name="principals"/> only documents whose ACL shares a principal
    /// with the set are returned; <c>null</c> means "all documents of the tenant" (administrators).
    /// </summary>
    Task<PagedResponse<DocumentSummary>> ListAsync(
        string tenantId, IReadOnlyCollection<string>? principals, int page, int pageSize, CancellationToken cancellationToken);
}

public sealed record VectorQuery(
    string TenantId,
    IReadOnlyCollection<string> Principals,
    ReadOnlyMemory<float> Embedding,
    int Top,
    double MinSimilarity);

public sealed record RetrievedChunk(
    Guid DocumentId,
    string ExternalId,
    int DocumentVersion,
    string DocumentTitle,
    Classification Classification,
    int Ordinal,
    string Text,
    double Similarity);

public interface IVectorSearch
{
    /// <summary>
    /// Top-k chunks by cosine similarity among chunks the principals may read. The ACL and tenant predicates are
    /// part of the same query as the similarity ranking (pre-filtering): an unauthorised chunk is never read,
    /// ranked or returned. Quarantined chunks are excluded. Results are ordered by descending similarity.
    /// </summary>
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(VectorQuery query, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------------------------
// Cost controls: semantic cache, token budgets, routing, token counting
// ---------------------------------------------------------------------------------------------------------------

public sealed record SourceReference(Guid DocumentId, string ExternalId, int Version, string Title);

/// <summary>Cache partition: answers are only ever shared within one tenant, one feature and one model tier.</summary>
public sealed record SemanticCacheScope(string TenantId, string Namespace, string ModelTier);

public sealed record CachedAnswer(
    string Question,
    string Answer,
    IReadOnlyList<SourceReference> Sources,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    DateTime CreatedAtUtc);

public sealed record CacheCandidate(CachedAnswer Answer, double Similarity);

public interface ISemanticCache
{
    /// <summary>The most similar cached answer in the scope at or above the configured threshold, or null.</summary>
    Task<CacheCandidate?> FindAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken);

    Task StoreAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CachedAnswer answer, CancellationToken cancellationToken);

    /// <summary>Drops every cached answer of the tenant that cites the document (content or ACL changed).</summary>
    Task InvalidateDocumentAsync(string tenantId, Guid documentId, CancellationToken cancellationToken);
}

public sealed record BudgetRequest(string TenantId, string SubjectId, int EstimatedTokens);

public sealed record BudgetLease(
    bool Granted,
    string TenantId,
    string SubjectId,
    int ReservedTokens,
    long TenantRemaining,
    long SubjectRemaining,
    TimeSpan? RetryAfter,
    string? DeniedBy)
{
    public static BudgetLease Denied(BudgetRequest request, string deniedBy, long tenantRemaining, long subjectRemaining, TimeSpan retryAfter) =>
        new(false, request.TenantId, request.SubjectId, 0, tenantRemaining, subjectRemaining, retryAfter, deniedBy);
}

public sealed record BudgetStatus(
    string TenantId,
    long TenantLimit,
    long TenantUsed,
    DateTime TenantPeriodResetsAtUtc,
    long SubjectLimit,
    long SubjectUsed,
    DateTime SubjectPeriodResetsAtUtc);

public interface ITokenBudget
{
    /// <summary>
    /// Atomically reserves <see cref="BudgetRequest.EstimatedTokens"/> against the tenant's monthly and the
    /// subject's daily budget. Either both are charged or neither is.
    /// </summary>
    Task<BudgetLease> ReserveAsync(BudgetRequest request, CancellationToken cancellationToken);

    /// <summary>Replaces the reservation with the tokens actually billed (refunds or charges the difference).</summary>
    Task SettleAsync(BudgetLease lease, int actualTokens, CancellationToken cancellationToken);

    Task<BudgetStatus> GetStatusAsync(string tenantId, string subjectId, CancellationToken cancellationToken);
}

public interface ITokenCounter
{
    int CountTokens(string text);
}

public sealed record RoutingContext(
    string TenantId,
    string Prompt,
    int PromptTokens,
    int ContextTokens,
    string? RequestedModel);

public sealed record ModelRoute(string Tier, string Model, double ComplexityScore, IReadOnlyList<string> Reasons);

public interface IModelRouter
{
    ModelRoute Route(RoutingContext context);
}

/// <summary>Chat clients per tier (<c>fast</c>, <c>reasoning</c>...), already wrapped with telemetry and resilience.</summary>
public interface IChatClientProvider
{
    IChatClient GetClient(string tier);
}

// ---------------------------------------------------------------------------------------------------------------
// Audit
// ---------------------------------------------------------------------------------------------------------------

public sealed record AuditQuery(string TenantId, DateTime? FromUtc, DateTime? ToUtc, string? SubjectId, AuditOutcome? Outcome, int Page, int PageSize);

public sealed record AuditVerification(
    string TenantId,
    bool IsIntact,
    long EntriesChecked,
    long? FirstBrokenSequence,
    string? Reason,
    string? DatabaseLedgerDigest);

public interface IAuditLog
{
    /// <summary>Appends to the tenant's hash chain; concurrent appends are serialised per tenant.</summary>
    Task<AuditEntry> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken);

    /// <summary>Recomputes the tenant's chain from the first entry and reports the first break, if any.</summary>
    Task<AuditVerification> VerifyAsync(string tenantId, CancellationToken cancellationToken);

    Task<PagedResponse<AuditEntry>> ListAsync(AuditQuery query, CancellationToken cancellationToken);
}
