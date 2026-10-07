using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Infrastructure.Knowledge;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.IntegrationTests.Knowledge;

/// <summary>SQL Server 2025 with the native <c>vector(384)</c> column and exact KNN in one statement.</summary>
public sealed class SqlServerKnowledgeStoreTests(MsSqlKnowledgeFixture fixture)
    : KnowledgeStoreScenarios, IClassFixture<MsSqlKnowledgeFixture>
{
    private DbContextOptions<SentinelDbContext>? _options;

    protected override string? SkipReason => fixture.SkipReason;

    private protected override SentinelDbContext NewContext() => new(_options ??= fixture.Options(Capture));

    private protected override IVectorSearch NewVectorSearch(SentinelDbContext db) => new SqlServerVectorSearch(db);

    protected override void AssertSearchStatement(string sql)
    {
        Assert.Contains("VECTOR_DISTANCE('cosine'", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY VECTOR_DISTANCE", sql, StringComparison.Ordinal);
        Assert.Contains("TOP(", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(";", sql.TrimEnd().TrimEnd(';'), StringComparison.Ordinal);
    }
}
