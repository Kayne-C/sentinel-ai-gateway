using Sentinel.Application.Abstractions;
using Sentinel.Application.Features.Usage;
using Sentinel.Domain.Identity;

namespace Sentinel.Application.Tests.CostControls;

public sealed class GetUsageQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_budget_of_the_callers_own_tenant_and_subject()
    {
        var budget = new RecordingBudget();
        var handler = new GetUsageQueryHandler(budget);
        var caller = new CallerIdentity("tenant-1", "oid-42", "Ayşe", ["group-a"], [SentinelRoles.User], CallerKind.User);

        var result = await handler.Handle(new GetUsageQuery(caller), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(("tenant-1", "oid-42"), budget.LastQuery);
        Assert.Equal(budget.Status, result.Value);
    }

    [Fact]
    public async Task The_callers_cancellation_token_reaches_the_budget()
    {
        var budget = new RecordingBudget();
        using var cancellation = new CancellationTokenSource();
        var caller = new CallerIdentity("tenant-1", "oid-42", null, [], [], CallerKind.Application);

        await new GetUsageQueryHandler(budget).Handle(new GetUsageQuery(caller), cancellation.Token);

        Assert.Equal(cancellation.Token, budget.LastToken);
    }

    private sealed class RecordingBudget : ITokenBudget
    {
        public BudgetStatus Status { get; } = new(
            "tenant-1", 2_000_000, 1_234, new DateTime(2099, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            100_000, 567, new DateTime(2099, 3, 11, 0, 0, 0, DateTimeKind.Utc));

        public (string TenantId, string SubjectId)? LastQuery { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public Task<BudgetStatus> GetStatusAsync(string tenantId, string subjectId, CancellationToken cancellationToken)
        {
            LastQuery = (tenantId, subjectId);
            LastToken = cancellationToken;
            return Task.FromResult(Status);
        }

        public Task<BudgetLease> ReserveAsync(BudgetRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SettleAsync(BudgetLease lease, int actualTokens, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
