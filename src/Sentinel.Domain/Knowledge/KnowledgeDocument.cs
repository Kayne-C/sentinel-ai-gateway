using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;

namespace Sentinel.Domain.Knowledge;

public enum Classification
{
    Public,
    Internal,
    Confidential,
    Restricted,
}

/// <summary>
/// A document in a tenant's knowledge base together with its access control list. The ACL lives on the document
/// (not on chunks or in the vector index metadata) and is enforced inside the vector query itself.
/// </summary>
public sealed class KnowledgeDocument
{
    public const int MaxTitleLength = 300;
    public const int MaxExternalIdLength = 200;
    public const int MaxContentLength = 2_000_000;
    public const int MaxPrincipals = 256;

    private readonly HashSet<string> _principals;

    private KnowledgeDocument(
        Guid id, string tenantId, string externalId, string title, string? sourceUri, Classification classification,
        int version, string contentHash, HashSet<string> principals, DateTime createdAtUtc, DateTime updatedAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        ExternalId = externalId;
        Title = title;
        SourceUri = sourceUri;
        Classification = classification;
        Version = version;
        ContentHash = contentHash;
        _principals = principals;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid Id { get; }

    public string TenantId { get; }

    /// <summary>Identifier in the source system (SharePoint item id, file path...), unique per tenant.</summary>
    public string ExternalId { get; }

    public string Title { get; private set; }

    public string? SourceUri { get; private set; }

    public Classification Classification { get; private set; }

    /// <summary>Incremented whenever content or access changes; cached answers pin the versions they used.</summary>
    public int Version { get; private set; }

    /// <summary>SHA-256 of the normalised content; unchanged content is not re-chunked or re-embedded.</summary>
    public string ContentHash { get; private set; }

    public IReadOnlySet<string> Principals => _principals;

    public DateTime CreatedAtUtc { get; }

    public DateTime UpdatedAtUtc { get; private set; }

    public static Result<KnowledgeDocument> Create(
        string tenantId, string externalId, string title, string content, string? sourceUri,
        Classification classification, IEnumerable<string> principals, DateTime utcNow)
    {
        var principalSet = ParsePrincipals(principals);
        if (principalSet.IsFailure)
        {
            return principalSet.Error;
        }

        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > MaxExternalIdLength)
        {
            return KnowledgeErrors.InvalidExternalId;
        }

        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength)
        {
            return KnowledgeErrors.InvalidTitle;
        }

        if (string.IsNullOrWhiteSpace(content) || content.Length > MaxContentLength)
        {
            return KnowledgeErrors.InvalidContent;
        }

        return new KnowledgeDocument(
            Guid.CreateVersion7(utcNow), tenantId, externalId.Trim(), title.Trim(), sourceUri, classification, 1,
            ComputeContentHash(content), principalSet.Value, utcNow, utcNow);
    }

    /// <summary>Rehydrates a persisted document (persistence layer only).</summary>
    public static KnowledgeDocument Restore(
        Guid id, string tenantId, string externalId, string title, string? sourceUri, Classification classification,
        int version, string contentHash, IEnumerable<string> principals, DateTime createdAtUtc, DateTime updatedAtUtc) =>
        new(id, tenantId, externalId, title, sourceUri, classification, version, contentHash,
            principals.ToHashSet(StringComparer.Ordinal), createdAtUtc, updatedAtUtc);

    /// <summary>Applies a new revision. Returns whether anything changed (and the version was bumped).</summary>
    public Result<DocumentChange> Revise(
        string title, string content, string? sourceUri, Classification classification, IEnumerable<string> principals, DateTime utcNow)
    {
        var principalSet = ParsePrincipals(principals);
        if (principalSet.IsFailure)
        {
            return principalSet.Error;
        }

        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength)
        {
            return KnowledgeErrors.InvalidTitle;
        }

        if (string.IsNullOrWhiteSpace(content) || content.Length > MaxContentLength)
        {
            return KnowledgeErrors.InvalidContent;
        }

        var contentHash = ComputeContentHash(content);
        var contentChanged = contentHash != ContentHash;
        var accessChanged = !principalSet.Value.SetEquals(_principals) || classification != Classification;
        var metadataChanged = title.Trim() != Title || sourceUri != SourceUri;

        if (!contentChanged && !accessChanged && !metadataChanged)
        {
            return DocumentChange.None;
        }

        Title = title.Trim();
        SourceUri = sourceUri;
        Classification = classification;
        ContentHash = contentHash;
        _principals.Clear();
        _principals.UnionWith(principalSet.Value);
        Version++;
        UpdatedAtUtc = utcNow;

        return new DocumentChange(contentChanged, accessChanged, metadataChanged);
    }

    /// <summary>True iff the caller holds at least one principal on this document's ACL (same tenant only).</summary>
    public bool IsVisibleTo(CallerIdentity caller) =>
        caller.TenantId == TenantId && _principals.Overlaps(caller.Principals);

    public static string ComputeContentHash(string content) => HashChain.Sha256Hex(content.ReplaceLineEndings("\n").Trim());

    private static Result<HashSet<string>> ParsePrincipals(IEnumerable<string> principals)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in principals)
        {
            if (!AccessPrincipal.TryParse(raw, out var principal))
            {
                return KnowledgeErrors.InvalidPrincipal(raw);
            }

            set.Add(principal);
        }

        // An empty ACL would make a document invisible to everyone, including its owners: always a mistake.
        if (set.Count == 0)
        {
            return KnowledgeErrors.EmptyAcl;
        }

        return set.Count > MaxPrincipals ? KnowledgeErrors.TooManyPrincipals : set;
    }
}

public sealed record DocumentChange(bool ContentChanged, bool AccessChanged, bool MetadataChanged)
{
    public static readonly DocumentChange None = new(false, false, false);

    public bool Any => ContentChanged || AccessChanged || MetadataChanged;
}

/// <summary>A retrievable piece of a document. Embeddings are produced by the application layer.</summary>
public sealed record DocumentChunk(int Ordinal, string Text, int TokenCount, bool Quarantined, string? QuarantineReason);

public static class KnowledgeErrors
{
    public static readonly Error InvalidExternalId = Error.BusinessRule("Knowledge.InvalidExternalId", $"External id is required (max {KnowledgeDocument.MaxExternalIdLength} characters).");
    public static readonly Error InvalidTitle = Error.BusinessRule("Knowledge.InvalidTitle", $"Title is required (max {KnowledgeDocument.MaxTitleLength} characters).");
    public static readonly Error InvalidContent = Error.BusinessRule("Knowledge.InvalidContent", "Content is required and must not exceed the size limit.");
    public static readonly Error EmptyAcl = Error.BusinessRule("Knowledge.EmptyAcl", "A document needs at least one principal (everyone, user:{id} or group:{id}).");
    public static readonly Error TooManyPrincipals = Error.BusinessRule("Knowledge.TooManyPrincipals", $"A document can have at most {KnowledgeDocument.MaxPrincipals} principals.");

    public static Error InvalidPrincipal(string? value) =>
        Error.BusinessRule("Knowledge.InvalidPrincipal", $"'{value}' is not a valid principal (everyone, user:{{id}} or group:{{id}}).");

    public static Error NotFound(string externalId) => Error.NotFound("Knowledge.NotFound", $"Document '{externalId}' was not found.");
}
