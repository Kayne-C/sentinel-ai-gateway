using Sentinel.Application.Abstractions;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Application.Common;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;

namespace Sentinel.Application.Features.Documents;

/// <summary>
/// Administrators see the whole tenant with full ACLs. Everyone else sees only documents their principals may read,
/// and of each ACL only the principals they hold themselves: the list explains why a document is visible without
/// disclosing which other users and groups can read it.
/// </summary>
internal sealed class ListDocumentsQueryHandler(IKnowledgeRepository repository)
    : IQueryHandler<ListDocumentsQuery, PagedResponse<DocumentSummary>>
{
    public async Task<Result<PagedResponse<DocumentSummary>>> Handle(ListDocumentsQuery request, CancellationToken cancellationToken)
    {
        var caller = request.Caller;
        var isAdmin = caller.IsInRole(SentinelRoles.Admin);

        var page = await repository.ListAsync(
            caller.TenantId, isAdmin ? null : caller.Principals, request.Page, request.PageSize, cancellationToken);

        if (isAdmin)
        {
            return page;
        }

        return page with
        {
            Items = [.. page.Items.Select(d => d with { Principals = [.. d.Principals.Where(caller.Principals.Contains)] })],
        };
    }
}
