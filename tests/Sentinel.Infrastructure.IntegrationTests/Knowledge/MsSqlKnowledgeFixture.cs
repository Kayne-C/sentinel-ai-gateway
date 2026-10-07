using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sentinel.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace Sentinel.Infrastructure.IntegrationTests.Knowledge;

/// <summary>
/// One SQL Server 2025 container per test class, with a dedicated database created by <c>EnsureCreated</c> (there
/// are no migrations yet). Tests isolate themselves by tenant id. When Docker or the image is unavailable the
/// tests skip; a schema that fails to create is a real failure and is not masked.
/// </summary>
public sealed class MsSqlKnowledgeFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/mssql/server:2025-latest";
    private const string Database = "SentinelKnowledgeTests";
    private const int StartAttempts = 2;

    private MsSqlContainer? _container;
    private string? _connectionString;

    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        // SQL Server can exit during start-up when the host is briefly short of memory (other containers starting
        // in parallel); one retry turns most of those into a real run instead of a skip.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _container = new MsSqlBuilder(Image).Build();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await _container.StartAsync(timeout.Token);
                break;
            }
            catch (Exception exception)
            {
                await DisposeContainerAsync();
                if (DockerAvailability.IsMissing(exception))
                {
                    SkipReason = $"Docker is not available ({exception.GetType().Name}: {exception.Message})";
                    return;
                }

                // Docker is there, so a container that will not start is a real failure, not a reason to skip.
                if (attempt == StartAttempts)
                {
                    throw;
                }
            }
        }

        _connectionString = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = Database }.ConnectionString;
        await using var db = new SentinelDbContext(Options(interceptor: null));
        await db.Database.EnsureCreatedAsync();
    }

    public ValueTask DisposeAsync() => DisposeContainerAsync();

    private async ValueTask DisposeContainerAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    /// <summary>Same provider settings as production: compatibility level 170 and retry on transient failures.</summary>
    internal DbContextOptions<SentinelDbContext> Options(IInterceptor? interceptor)
    {
        var builder = new DbContextOptionsBuilder<SentinelDbContext>().UseSqlServer(
            _connectionString ?? throw new InvalidOperationException(SkipReason ?? "Not initialised."),
            sql => sql.UseCompatibilityLevel(170).EnableRetryOnFailure(maxRetryCount: 3));

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder.Options;
    }
}
