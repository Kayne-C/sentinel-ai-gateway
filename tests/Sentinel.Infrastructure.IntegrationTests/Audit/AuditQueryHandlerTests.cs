using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Application.Common;
using Sentinel.Application.Features.Audit;
using Sentinel.Domain.Audit;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;

namespace Sentinel.Infrastructure.IntegrationTests.Audit;

/// <summary>The audit use cases through the real mediator pipeline, with a recording fake behind the port.</summary>
public sealed class AuditQueryHandlerTests : IAsyncDisposable
{
    private static readonly CallerIdentity Admin = new("tenant-a", "admin-oid", "Ada Admin", [], [SentinelRoles.Admin], CallerKind.User);
    private static readonly CallerIdentity Member = new("tenant-a", "user-oid", "Uma User", [], [SentinelRoles.User], CallerKind.User);

    private readonly RecordingAuditLog _auditLog = new();
    private readonly ServiceProvider _services;

    public AuditQueryHandlerTests()
    {
        var services = new ServiceCollection().AddLogging().AddApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IAuditLog>(_auditLog);
        _services = services.BuildServiceProvider(validateScopes: true);
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    [Fact]
    public async Task Callers_without_the_admin_role_cannot_list_audit_entries()
    {
        var result = await SendAsync(new ListAuditEntriesQuery(Member));

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationErrors.AdminRequired, result.Error);
        Assert.Null(_auditLog.LastQuery);
    }

    [Fact]
    public async Task Callers_without_the_admin_role_cannot_verify_the_chain()
    {
        var result = await SendAsync(new VerifyAuditChainQuery(Member));

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationErrors.AdminRequired, result.Error);
        Assert.Null(_auditLog.LastVerifiedTenant);
    }

    [Fact]
    public async Task Authorisation_is_decided_before_input_is_validated()
    {
        var result = await SendAsync(new ListAuditEntriesQuery(Member, PageSize: 0));

        Assert.Equal(ApplicationErrors.AdminRequired, result.Error);
    }

    [Fact]
    public async Task The_listing_is_always_scoped_to_the_callers_own_tenant()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(7);

        var result = await SendAsync(new ListAuditEntriesQuery(Admin, from, to, " user-oid ", AuditOutcome.Blocked, 3, 25));

        Assert.True(result.IsSuccess);
        Assert.Equal(new AuditQuery("tenant-a", from, to, "user-oid", AuditOutcome.Blocked, 3, 25), _auditLog.LastQuery);
    }

    [Fact]
    public async Task Verification_is_always_scoped_to_the_callers_own_tenant()
    {
        var result = await SendAsync(new VerifyAuditChainQuery(Admin));

        Assert.True(result.IsSuccess);
        Assert.Equal("tenant-a", result.Value.TenantId);
        Assert.Equal("tenant-a", _auditLog.LastVerifiedTenant);
    }

    [Theory]
    [InlineData(0, 50, "Page")]
    [InlineData(-1, 50, "Page")]
    [InlineData(1, 0, "PageSize")]
    [InlineData(1, -5, "PageSize")]
    [InlineData(1, 201, "PageSize")]
    public async Task Paging_outside_its_limits_is_rejected(int page, int pageSize, string invalidField)
    {
        var result = await SendAsync(new ListAuditEntriesQuery(Admin, Page: page, PageSize: pageSize));

        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Equal([invalidField], error.Errors.Keys);
        Assert.Null(_auditLog.LastQuery);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 200)]
    [InlineData(1_000_000, 200)]
    public async Task Paging_at_its_limits_is_accepted(int page, int pageSize)
    {
        var result = await SendAsync(new ListAuditEntriesQuery(Admin, Page: page, PageSize: pageSize));

        Assert.True(result.IsSuccess);
        Assert.Equal(pageSize, _auditLog.LastQuery?.PageSize);
    }

    [Fact]
    public async Task A_time_window_that_ends_before_it_starts_is_rejected()
    {
        var from = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        var result = await SendAsync(new ListAuditEntriesQuery(Admin, FromUtc: from, ToUtc: from.AddTicks(-1)));

        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Contains(nameof(ListAuditEntriesQuery.FromUtc), error.Errors.Keys);
    }

    [Fact]
    public async Task An_undefined_outcome_or_an_oversized_subject_is_rejected()
    {
        var result = await SendAsync(new ListAuditEntriesQuery(Admin, SubjectId: new string('s', 257), Outcome: (AuditOutcome)42));

        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Contains(nameof(ListAuditEntriesQuery.Outcome), error.Errors.Keys);
        Assert.Contains(nameof(ListAuditEntriesQuery.SubjectId), error.Errors.Keys);
    }

    private async Task<Result<T>> SendAsync<T>(IQuery<T> query)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(query, CancellationToken.None);
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public AuditQuery? LastQuery { get; private set; }

        public string? LastVerifiedTenant { get; private set; }

        public Task<AuditEntry> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Queries never append.");

        public Task<AuditVerification> VerifyAsync(string tenantId, CancellationToken cancellationToken)
        {
            LastVerifiedTenant = tenantId;
            return Task.FromResult(new AuditVerification(tenantId, true, 0, null, null, null));
        }

        public Task<PagedResponse<AuditEntry>> ListAsync(AuditQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(new PagedResponse<AuditEntry>([], query.Page, query.PageSize, 0));
        }
    }
}
