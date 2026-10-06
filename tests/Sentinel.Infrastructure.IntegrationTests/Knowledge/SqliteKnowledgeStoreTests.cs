using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Infrastructure.Knowledge;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.IntegrationTests.Knowledge;

/// <summary>In-memory SQLite (kept open for the test's lifetime) with the in-process vector search.</summary>
public sealed class SqliteKnowledgeStoreTests : KnowledgeStoreScenarios, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:;Foreign Keys=True");
    private DbContextOptions<SentinelDbContext>? _options;

    protected override string? SkipReason => null;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<SentinelDbContext>().UseSqlite(_connection).AddInterceptors(Capture).Options;
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private protected override SentinelDbContext NewContext() => new(_options ?? throw new InvalidOperationException("Not initialised."));

    private protected override IVectorSearch NewVectorSearch(SentinelDbContext db) => new InProcessVectorSearch(db);

    protected override void AssertSearchStatement(string sql)
    {
        Assert.Contains("\"Embedding\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY", sql, StringComparison.Ordinal);
    }
}
