using Sentinel.Application.Abstractions;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Application.Common;
using Sentinel.Domain.Audit;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;

namespace Sentinel.Application.Features.Audit;

/// <summary>
/// The audit log records who asked what (prompt digests, redacted prompts), what it cost and which guardrails fired,
/// so the tenant is never taken from the request: it is always the caller's own. Authorisation is checked before
/// validation, so a caller without the role learns nothing about the endpoint, not even which inputs it accepts.
/// </summary>
internal sealed class ListAuditEntriesQueryHandler(IAuditLog auditLog)
    : IQueryHandler<ListAuditEntriesQuery, PagedResponse<AuditEntry>>
{
    internal const int MaxPageSize = 200;
    internal const int MaxSubjectIdLength = 256;

    public async Task<Result<PagedResponse<AuditEntry>>> Handle(ListAuditEntriesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Caller.IsInRole(SentinelRoles.Admin))
        {
            return ApplicationErrors.AdminRequired;
        }

        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return new ValidationError(errors);
        }

        var query = new AuditQuery(
            request.Caller.TenantId,
            request.FromUtc,
            request.ToUtc,
            string.IsNullOrWhiteSpace(request.SubjectId) ? null : request.SubjectId.Trim(),
            request.Outcome,
            request.Page,
            request.PageSize);

        return await auditLog.ListAsync(query, cancellationToken);
    }

    private static Dictionary<string, string[]> Validate(ListAuditEntriesQuery request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Page < 1)
        {
            errors[nameof(request.Page)] = ["Page must be at least 1."];
        }

        if (request.PageSize is < 1 or > MaxPageSize)
        {
            errors[nameof(request.PageSize)] = [$"PageSize must be between 1 and {MaxPageSize}."];
        }

        if (request is { FromUtc: { } from, ToUtc: { } to } && ToUtc(from) > ToUtc(to))
        {
            errors[nameof(request.FromUtc)] = ["FromUtc must not be later than ToUtc."];
        }

        if (request.SubjectId is { Length: > MaxSubjectIdLength })
        {
            errors[nameof(request.SubjectId)] = [$"SubjectId must not exceed {MaxSubjectIdLength} characters."];
        }

        if (request.Outcome is { } outcome && !Enum.IsDefined(outcome))
        {
            errors[nameof(request.Outcome)] = ["Outcome is not a known value."];
        }

        return errors;
    }

    /// <summary>Same convention as the audit store: an unspecified kind is already UTC.</summary>
    private static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
