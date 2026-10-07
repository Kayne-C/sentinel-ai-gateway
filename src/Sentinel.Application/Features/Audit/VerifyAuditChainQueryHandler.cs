using Sentinel.Application.Abstractions;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Application.Common;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;

namespace Sentinel.Application.Features.Audit;

/// <summary>Recomputes the caller's own tenant chain; there is no way to name another tenant.</summary>
internal sealed class VerifyAuditChainQueryHandler(IAuditLog auditLog)
    : IQueryHandler<VerifyAuditChainQuery, AuditVerification>
{
    public async Task<Result<AuditVerification>> Handle(VerifyAuditChainQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Caller.IsInRole(SentinelRoles.Admin))
        {
            return ApplicationErrors.AdminRequired;
        }

        return await auditLog.VerifyAsync(request.Caller.TenantId, cancellationToken);
    }
}
