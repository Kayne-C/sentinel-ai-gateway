using Sentinel.Application.Abstractions;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Domain.Common;

namespace Sentinel.Application.Features.Usage;

/// <summary>
/// The caller's own budget only: tenant and subject come from the validated identity, never from the request, so
/// nobody can read another tenant's or another user's consumption through this query.
/// </summary>
internal sealed class GetUsageQueryHandler(ITokenBudget budget) : IQueryHandler<GetUsageQuery, BudgetStatus>
{
    public async Task<Result<BudgetStatus>> Handle(GetUsageQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await budget.GetStatusAsync(request.Caller.TenantId, request.Caller.SubjectId, cancellationToken);
    }
}
