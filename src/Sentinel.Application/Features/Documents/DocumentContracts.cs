using Sentinel.Application.Abstractions;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Application.Common;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Knowledge;

namespace Sentinel.Application.Features.Documents;

/// <summary>Creates or revises a document: chunk → scan for injection → embed → store, all-or-nothing.</summary>
public sealed record UpsertDocumentCommand(
    CallerIdentity Caller,
    string ExternalId,
    string Title,
    string Content,
    Classification Classification,
    IReadOnlyList<string> Principals,
    string? SourceUri) : ICommand<DocumentResponse>;

public sealed record DocumentResponse(
    Guid Id,
    string ExternalId,
    string Title,
    Classification Classification,
    int Version,
    int ChunkCount,
    int QuarantinedChunkCount,
    IReadOnlyList<string> Principals,
    bool Changed,
    DateTime UpdatedAtUtc);

public sealed record DeleteDocumentCommand(CallerIdentity Caller, string ExternalId) : ICommand;

/// <summary>Administrators see every document of the tenant; everyone else only what their ACL principals allow.</summary>
public sealed record ListDocumentsQuery(CallerIdentity Caller, int Page = 1, int PageSize = 50) : IQuery<PagedResponse<DocumentSummary>>;
