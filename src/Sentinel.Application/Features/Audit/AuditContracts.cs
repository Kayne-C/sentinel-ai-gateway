using Sentinel.Application.Abstractions;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Application.Common;
using Sentinel.Domain.Audit;
using Sentinel.Domain.Identity;

namespace Sentinel.Application.Features.Audit;

/// <summary>Administrators only; always scoped to the caller's own tenant.</summary>
public sealed record ListAuditEntriesQuery(
    CallerIdentity Caller,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    string? SubjectId = null,
    AuditOutcome? Outcome = null,
    int Page = 1,
    int PageSize = 50) : IQuery<PagedResponse<AuditEntry>>;

public sealed record VerifyAuditChainQuery(CallerIdentity Caller) : IQuery<AuditVerification>;
