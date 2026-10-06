using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Common;
using Sentinel.Domain.Audit;
using Sentinel.Domain.Common;
using Sentinel.Infrastructure.Audit;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.IntegrationTests.Audit;

internal static class AuditTestData
{
    public static readonly DateTime BaseTime = new DateTime(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc).AddTicks(1234567);

    public static string NewTenant() => "tenant-" + Guid.NewGuid().ToString("N");

    /// <summary>A realistic, fully populated event; <paramref name="index"/> varies every field.</summary>
    public static AuditEvent Event(
        string tenantId,
        int index = 0,
        string subjectId = "3f2b1c9e-0000-4000-8000-00000000a11c",
        AuditOutcome outcome = AuditOutcome.Allowed,
        DateTime? occurredAtUtc = null) => new()
        {
            TenantId = tenantId,
            SubjectId = subjectId,
            Operation = index % 3 == 0 ? AuditOperation.Ask : AuditOperation.ChatCompletion,
            Outcome = outcome,
            OccurredAtUtc = occurredAtUtc ?? BaseTime.AddSeconds(index),
            Model = "qwen2.5:1.5b",
            ModelTier = ModelCatalogOptions.FastTier,
            PromptTokens = 100 + index,
            CompletionTokens = 20 + index,
            EstimatedCostUsd = 0.00012345m * (index + 1),
            RedactedPii = new Dictionary<string, int> { ["PhoneNumber"] = index % 3, ["Email"] = 1 },
            GuardrailFindings = index % 2 == 0 ? [] : ["injection.override"],
            Sources = [$"doc-{index}:1", "handbook:3"],
            PromptDigest = HashChain.Sha256Hex(string.Create(CultureInfo.InvariantCulture, $"prompt {index}")),
            RedactedPrompt = index % 4 == 0 ? string.Create(CultureInfo.InvariantCulture, $"Who approved [EMAIL_1] ticket {index}?") : null,
            ErrorCode = outcome == AuditOutcome.Failed ? "Model.Unavailable" : null,
            LatencyMs = 12.25 + index,
            TraceId = index.ToString("x32", CultureInfo.InvariantCulture),
            Subject = null,
        };

    public static long[] Sequences(PagedResponse<AuditEntry> page) => [.. page.Items.Select(e => e.Sequence)];

    public static long[] Range(long first, long last) =>
        first <= last
            ? [.. Enumerable.Range(0, (int)(last - first + 1)).Select(i => first + i)]
            : [.. Enumerable.Range(0, (int)(first - last + 1)).Select(i => first - i)];
}

internal static class AuditTestServices
{
    /// <summary>
    /// The production registration (<c>AddInfrastructure</c>: pooled factory with <c>EnableRetryOnFailure</c>) against
    /// SQL Server. Prompt storage is switched on so every column, the <c>nvarchar(max)</c> prompt included, is exercised.
    /// </summary>
    public static ServiceProvider ForSqlServer(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = nameof(DatabaseProvider.SqlServer),
                ["Database:ConnectionString"] = connectionString,
            })
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddInfrastructure(configuration)
            .Configure<AuditPolicyOptions>(policy => policy.StoreRedactedPrompts = true)
            .BuildServiceProvider(validateScopes: true);
    }

    /// <summary>
    /// SQLite in-memory: every context shares the one kept-open connection (the database lives exactly as long as
    /// that connection). A store without ledger protection, which is what the tamper tests need.
    /// </summary>
    public static ServiceProvider ForSqlite(SqliteConnection connection, IInterceptor? interceptor = null, bool storeRedactedPrompts = true) =>
        new ServiceCollection()
            .AddLogging()
            .AddPooledDbContextFactory<SentinelDbContext>(options =>
            {
                options.UseSqlite(connection);
                if (interceptor is not null)
                {
                    options.AddInterceptors(interceptor);
                }
            })
            .AddAuditLog(new ConfigurationBuilder().Build())
            .Configure<AuditPolicyOptions>(policy => policy.StoreRedactedPrompts = storeRedactedPrompts)
            .BuildServiceProvider(validateScopes: true);
}

/// <summary>Runs a block under another culture and always restores the previous ones.</summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    private CultureScope(CultureInfo culture)
    {
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>Skips the test when the culture does not exist (a host running with invariant globalization).</summary>
    public static CultureScope Use(string name)
    {
        try
        {
            return new(CultureInfo.GetCultureInfo(name, predefinedOnly: true));
        }
        catch (CultureNotFoundException)
        {
            Assert.Skip($"Culture '{name}' is not available on this host (invariant globalization).");
            throw;
        }
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }
}
