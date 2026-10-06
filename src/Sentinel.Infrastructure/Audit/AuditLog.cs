using System.Data.Common;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Domain.Audit;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Audit;

/// <summary>
/// Per-tenant SHA-256 hash chain over <c>AuditEntries</c>.
/// <para>
/// <b>Independent commit.</b> Every append runs on its own short-lived context from the pooled factory and outside
/// any ambient transaction: the record of a request must survive a rollback of the request's own unit of work (a
/// failed or refused operation is exactly what an investigator wants to see).
/// </para>
/// <para>
/// <b>Ordering.</b> Inside the process, appends of one tenant are serialised by a per-tenant semaphore, so they never
/// race each other. Across processes the unique <c>(TenantId, Sequence)</c> index decides: a writer that read a stale
/// chain head loses the insert, re-reads the head and tries again after a jittered backoff.
/// </para>
/// <para>
/// <b>Cancellation.</b> The caller's token deliberately does not abort a write. An audit record is evidence; a client
/// that disconnects at the right moment must not be able to suppress the record of what it just did. An internal
/// timeout bounds the write instead.
/// </para>
/// <para>
/// <b>Prompt policy.</b> With <see cref="AuditPolicyOptions.StoreRedactedPrompts"/> off, a redacted prompt is never
/// persisted, whatever the producer passed; its digest always is.
/// </para>
/// </summary>
internal sealed partial class AuditLog(
    IDbContextFactory<SentinelDbContext> contextFactory,
    IOptionsMonitor<AuditPolicyOptions> auditPolicy,
    TimeProvider timeProvider,
    ILogger<AuditLog> logger) : IAuditLog
{
    internal const int MaxAppendAttempts = 20;
    internal const int VerifyBatchSize = 500;
    internal static readonly TimeSpan AppendTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan BaseBackoff = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMilliseconds(250);

    private readonly KeyedAsyncLock _tenantLocks = new();

    /// <summary>Tenants with an append in progress or waiting in this process.</summary>
    internal int ActiveTenantLocks => _tenantLocks.ActiveKeys;

    public async Task<AuditEntry> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        // Not observed on purpose, see the type remarks.
        _ = cancellationToken;

        var template = AuditEntryMapper.ToTemplate(auditEvent, auditPolicy.CurrentValue.StoreRedactedPrompts);
        using var independent = SuppressAmbientTransaction();
        using var timeout = new CancellationTokenSource(AppendTimeout, timeProvider);
        try
        {
            using (await _tenantLocks.AcquireAsync(template.TenantId, timeout.Token))
            {
                var record = await AppendWithRetriesAsync(template, timeout.Token);
                return AuditEntryMapper.ToEntry(record);
            }
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            LogAppendTimedOut(logger, template.TenantId, AppendTimeout.TotalSeconds, exception);
            throw new TimeoutException(
                $"The audit entry of tenant '{template.TenantId}' could not be written within {AppendTimeout.TotalSeconds:0} s.", exception);
        }
    }

    public async Task<AuditVerification> VerifyAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        using var independent = SuppressAmbientTransaction();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Keyset batches rather than one query: with EnableRetryOnFailure EF buffers a whole result set in memory, and
        // a chain can be millions of rows. (Sequence, Id) instead of Sequence alone keeps a duplicated sequence (only
        // possible if someone dropped the unique index) from being skipped at a batch boundary.
        var verifier = new AuditChainVerifier();
        var (lastSequence, lastId) = (long.MinValue, long.MinValue);
        AuditChainBreak? broken = null;
        while (broken is null)
        {
            var afterSequence = lastSequence;
            var afterId = lastId;
            var batch = await db.Set<AuditEntryRecord>()
                .AsNoTracking()
                .Where(r => r.TenantId == tenantId
                    && r.Sequence >= afterSequence
                    && (r.Sequence > afterSequence || r.Id > afterId))
                .OrderBy(r => r.Sequence)
                .ThenBy(r => r.Id)
                .Take(VerifyBatchSize)
                .ToListAsync(cancellationToken);

            foreach (var record in batch)
            {
                broken = verifier.Check(record);
                if (broken is not null)
                {
                    break;
                }
            }

            if (batch.Count < VerifyBatchSize)
            {
                break;
            }

            (lastSequence, lastId) = (batch[^1].Sequence, batch[^1].Id);
        }

        var digest = await TryGenerateLedgerDigestAsync(db, cancellationToken);
        if (broken is null)
        {
            return new AuditVerification(tenantId, true, verifier.EntriesChecked, null, null, digest);
        }

        LogChainBroken(logger, tenantId, broken.Sequence, broken.Reason);
        return new AuditVerification(tenantId, false, verifier.EntriesChecked, broken.Sequence, broken.Reason, digest);
    }

    /// <remarks>
    /// Newest first by chain position (append order), which unlike <c>OccurredAtUtc</c> is unaffected by clock skew
    /// between gateway instances. Both time bounds are inclusive.
    /// </remarks>
    public async Task<PagedResponse<AuditEntry>> ListAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.TenantId);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.PageSize, 1);

        using var independent = SuppressAmbientTransaction();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var tenantId = query.TenantId;
        var entries = db.Set<AuditEntryRecord>().AsNoTracking().Where(r => r.TenantId == tenantId);
        if (query.FromUtc is { } fromValue)
        {
            var from = AuditEntryMapper.ToUtc(fromValue);
            entries = entries.Where(r => r.OccurredAtUtc >= from);
        }

        if (query.ToUtc is { } toValue)
        {
            var to = AuditEntryMapper.ToUtc(toValue);
            entries = entries.Where(r => r.OccurredAtUtc <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.SubjectId))
        {
            var subjectId = query.SubjectId;
            entries = entries.Where(r => r.SubjectId == subjectId);
        }

        if (query.Outcome is { } outcome)
        {
            var outcomeName = outcome.ToString();
            entries = entries.Where(r => r.Outcome == outcomeName);
        }

        var total = await entries.LongCountAsync(cancellationToken);
        var skip = (long)(query.Page - 1) * query.PageSize;
        if (skip >= total || skip > int.MaxValue)
        {
            return new PagedResponse<AuditEntry>([], query.Page, query.PageSize, total);
        }

        var records = await entries
            .OrderByDescending(r => r.Sequence)
            .ThenByDescending(r => r.Id)
            .Skip((int)skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<AuditEntry>(records.ConvertAll(AuditEntryMapper.ToEntry), query.Page, query.PageSize, total);
    }

    private async Task<AuditEntryRecord> AppendWithRetriesAsync(AuditEntryRecord template, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AppendOnceAsync(template, cancellationToken);
            }
            catch (Exception exception) when (IsSequenceConflict(exception))
            {
                if (attempt >= MaxAppendAttempts)
                {
                    LogAppendGaveUp(logger, template.TenantId, attempt, exception);
                    throw new InvalidOperationException(
                        $"The audit entry of tenant '{template.TenantId}' could not be appended after {attempt} attempts.", exception);
                }

                LogSequenceConflict(logger, template.TenantId, attempt);
                await Task.Delay(Backoff(attempt), timeProvider, cancellationToken);
            }
        }
    }

    private async Task<AuditEntryRecord> AppendOnceAsync(AuditEntryRecord template, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Read-head-then-insert runs as one unit of the execution strategy, so a transient failure re-reads the head.
        // When the failure hits the commit itself it is unknown whether the row was written; verifySucceeded looks for
        // exactly this row (same position, same hash) before retrying, so a lost acknowledgement never becomes a
        // second copy of the same event.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            new AppendAttempt(template), InsertNextAsync, VerifyInsertedAsync, cancellationToken);
    }

    private static async Task<AuditEntryRecord> InsertNextAsync(DbContext db, AppendAttempt attempt, CancellationToken cancellationToken)
    {
        // A retried operation starts from scratch: the failed insert must not linger in the change tracker.
        db.ChangeTracker.Clear();
        attempt.Pending = null;

        var tenantId = attempt.Template.TenantId;
        var head = await db.Set<AuditEntryRecord>()
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.Sequence)
            .Select(r => new ChainHead(r.Sequence, r.Hash))
            .FirstOrDefaultAsync(cancellationToken);

        var record = attempt.Template.ChainAt(head is null ? 1 : head.Sequence + 1, head?.Hash);
        attempt.Pending = record;
        db.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    private static async Task<ExecutionResult<AuditEntryRecord>> VerifyInsertedAsync(
        DbContext db, AppendAttempt attempt, CancellationToken cancellationToken)
    {
        if (attempt.Pending is not { } pending)
        {
            return new ExecutionResult<AuditEntryRecord>(false, null!);
        }

        var committed = await db.Set<AuditEntryRecord>()
            .AsNoTracking()
            .AnyAsync(
                r => r.TenantId == pending.TenantId && r.Sequence == pending.Sequence && r.Hash == pending.Hash,
                cancellationToken);
        return new ExecutionResult<AuditEntryRecord>(committed, pending);
    }

    /// <summary>
    /// SQL Server only: the database's own digest over every ledger table. Kept outside the database (automatic digest
    /// storage, or the caller's records), it lets <c>sys.sp_verify_database_ledger</c> prove later that no ledger row
    /// changed since, including removal of the newest entries, which no hash chain can detect on its own.
    /// </summary>
    private async Task<string?> TryGenerateLedgerDigestAsync(SentinelDbContext db, CancellationToken cancellationToken)
    {
        if (db.Provider != DatabaseProvider.SqlServer)
        {
            return null;
        }

        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(
                db,
                static async (context, token) =>
                {
                    await context.Database.OpenConnectionAsync(token);
                    try
                    {
                        await using var command = context.Database.GetDbConnection().CreateCommand();
                        command.CommandText = "EXEC sys.sp_generate_database_ledger_digest";
                        return await command.ExecuteScalarAsync(token) as string;
                    }
                    finally
                    {
                        await context.Database.CloseConnectionAsync();
                    }
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or RetryLimitExceededException)
        {
            // An engine without ledger support or a login without permission to generate digests: the hash chain
            // result stands on its own.
            LogLedgerDigestUnavailable(logger, exception.GetType().Name, exception);
            return null;
        }
    }

    private static bool IsSequenceConflict(Exception exception) => exception switch
    {
        DbUpdateException update => SentinelDbContext.IsConcurrencyFailure(update),
        RetryLimitExceededException { InnerException: DbUpdateException update } => SentinelDbContext.IsConcurrencyFailure(update),
        _ => false,
    };

    /// <summary>
    /// Exponential with "equal jitter": never zero (the winner needs a moment to commit) and never in lock-step with a
    /// competing writer that started at the same time.
    /// </summary>
    private static TimeSpan Backoff(int attempt)
    {
        var ceiling = Math.Min(MaxBackoff.TotalMilliseconds, BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds((ceiling / 2) + (Random.Shared.NextDouble() * ceiling / 2));
    }

    private static TransactionScope SuppressAmbientTransaction() =>
        new(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Audit chain head of tenant {TenantId} moved during append attempt {Attempt}; retrying")]
    private static partial void LogSequenceConflict(ILogger logger, string tenantId, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit entry of tenant {TenantId} could not be appended after {Attempts} attempts")]
    private static partial void LogAppendGaveUp(ILogger logger, string tenantId, int attempts, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit entry of tenant {TenantId} could not be written within {TimeoutSeconds} s")]
    private static partial void LogAppendTimedOut(ILogger logger, string tenantId, double timeoutSeconds, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit chain of tenant {TenantId} is broken at sequence {Sequence}: {Reason}")]
    private static partial void LogChainBroken(ILogger logger, string tenantId, long sequence, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database ledger digest unavailable ({ErrorType})")]
    private static partial void LogLedgerDigestUnavailable(ILogger logger, string errorType, Exception exception);

    private sealed record ChainHead(long Sequence, string Hash);

    /// <summary>State shared by one execution-strategy run and its verifySucceeded callback.</summary>
    private sealed class AppendAttempt(AuditEntryRecord template)
    {
        public AuditEntryRecord Template { get; } = template;

        /// <summary>The row the latest attempt tried to insert, if it got that far.</summary>
        public AuditEntryRecord? Pending { get; set; }
    }
}
