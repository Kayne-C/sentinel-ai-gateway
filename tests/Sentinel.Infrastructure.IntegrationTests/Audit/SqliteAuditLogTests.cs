using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Audit;
using Sentinel.Infrastructure.Audit;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.IntegrationTests.Audit;

/// <summary>
/// The hash chain on a store without ledger protection (SQLite in memory, schema from <c>EnsureCreated</c>): whatever
/// someone with write access edits directly in the table, verification must point at the entry where it happened.
/// </summary>
public sealed class SqliteAuditLogTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CompetingWriter _competingWriter = new();
    private readonly string _tenant = AuditTestData.NewTenant();
    private ServiceProvider _services = null!;

    private IAuditLog Log => _services.GetRequiredService<IAuditLog>();

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        _services = AuditTestServices.ForSqlite(_connection, _competingWriter);
        await using var db = await CreateContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Entries_are_chained_from_sequence_one_and_verify()
    {
        var entries = await AppendAsync(4);

        Assert.Equal(AuditTestData.Range(1, 4), entries.Select(e => e.Sequence));
        Assert.Null(entries[0].PreviousHash);
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].Hash, entries[i].PreviousHash);
        }

        Assert.All(entries, e => Assert.True(e.Hash.Length == 64 && e.Hash.All(char.IsAsciiHexDigitLower), e.Hash));
        var verification = await Log.VerifyAsync(_tenant, CancellationToken.None);
        Assert.True(verification.IsIntact, verification.Reason);
        Assert.Equal(4, verification.EntriesChecked);
        Assert.Null(verification.FirstBrokenSequence);
        Assert.Null(verification.Reason);
        Assert.Null(verification.DatabaseLedgerDigest);
    }

    [Fact]
    public async Task A_tenant_without_entries_has_an_intact_empty_chain()
    {
        var verification = await Log.VerifyAsync(_tenant, CancellationToken.None);

        Assert.True(verification.IsIntact);
        Assert.Equal(0, verification.EntriesChecked);
    }

    [Theory]
    [InlineData("UPDATE AuditEntries SET Outcome = 'Blocked' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET Outcome = '0' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET Operation = 'Ask' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET SubjectId = 'someone-else' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET Model = 'gpt-4.1' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET ModelTier = NULL WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET PromptTokens = PromptTokens - 100 WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET CompletionTokens = 0 WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET EstimatedCostUsd = '0.0' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET OccurredAtUtc = '2020-01-01 00:00:00' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET LatencyMs = LatencyMs + 0.001 WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET RedactedPiiJson = '{}' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET GuardrailFindingsJson = '[\"pii.output\"]' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET SourcesJson = '[]' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET PromptDigest = NULL WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET RedactedPrompt = 'rewritten' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET ErrorCode = 'Model.Unavailable' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET TraceId = NULL WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET Subject = '' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET Hash = PreviousHash WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET PreviousHash = Hash WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET TenantId = 'another-tenant' WHERE Sequence = 3")]
    [InlineData("UPDATE AuditEntries SET Sequence = 99 WHERE Sequence = 3")]
    public async Task Changing_any_column_of_one_entry_is_reported_at_exactly_that_sequence(string tamper)
    {
        await AppendAsync(5);

        Assert.Equal(1, await ExecuteAsync(tamper));

        var verification = await Log.VerifyAsync(_tenant, CancellationToken.None);
        Assert.False(verification.IsIntact);
        Assert.Equal(3, verification.FirstBrokenSequence);
        Assert.False(string.IsNullOrWhiteSpace(verification.Reason));
    }

    [Fact]
    public async Task Deleting_a_middle_entry_is_reported_as_a_gap_at_its_sequence()
    {
        await AppendAsync(5);

        await ExecuteAsync("DELETE FROM AuditEntries WHERE Sequence = 3");

        var verification = await Log.VerifyAsync(_tenant, CancellationToken.None);
        Assert.False(verification.IsIntact);
        Assert.Equal(3, verification.FirstBrokenSequence);
        Assert.Equal("Sequence 3 is missing; the next entry has sequence 4.", verification.Reason);
    }

    [Fact]
    public async Task Deleting_the_first_entry_is_reported_at_sequence_one()
    {
        await AppendAsync(3);

        await ExecuteAsync("DELETE FROM AuditEntries WHERE Sequence = 1");

        var verification = await Log.VerifyAsync(_tenant, CancellationToken.None);
        Assert.False(verification.IsIntact);
        Assert.Equal(1, verification.FirstBrokenSequence);
    }

    [Fact]
    public async Task A_duplicated_sequence_is_reported_even_after_the_unique_index_was_dropped()
    {
        await AppendAsync(5);

        await ExecuteAsync("DROP INDEX IX_AuditEntries_TenantId_Sequence");
        await ExecuteAsync(
            """
            INSERT INTO AuditEntries (TenantId, Sequence, OccurredAtUtc, SubjectId, Operation, Outcome, Model, ModelTier,
                PromptTokens, CompletionTokens, EstimatedCostUsd, RedactedPiiJson, GuardrailFindingsJson, SourcesJson,
                PromptDigest, RedactedPrompt, ErrorCode, LatencyMs, TraceId, Subject, PreviousHash, Hash)
            SELECT TenantId, Sequence, OccurredAtUtc, SubjectId, Operation, Outcome, Model, ModelTier,
                PromptTokens, CompletionTokens, EstimatedCostUsd, RedactedPiiJson, GuardrailFindingsJson, SourcesJson,
                PromptDigest, RedactedPrompt, ErrorCode, LatencyMs, TraceId, Subject, PreviousHash, Hash
            FROM AuditEntries WHERE Sequence = 3
            """);

        var verification = await Log.VerifyAsync(_tenant, CancellationToken.None);
        Assert.False(verification.IsIntact);
        Assert.Equal(3, verification.FirstBrokenSequence);
        Assert.Equal("Sequence 3 appears more than once.", verification.Reason);
    }

    [Fact]
    public async Task Rewriting_an_entry_and_recomputing_its_hash_breaks_the_link_from_its_successor()
    {
        await AppendAsync(5);

        await using (var db = await CreateContextAsync())
        {
            var record = await db.Set<AuditEntryRecord>().SingleAsync(r => r.Sequence == 3);
            record.Outcome = nameof(AuditOutcome.Blocked);
            record.Hash = AuditCanonicalForm.ComputeHash(record);
            await db.SaveChangesAsync();
        }

        var verification = await Log.VerifyAsync(_tenant, CancellationToken.None);
        Assert.False(verification.IsIntact);
        Assert.Equal(4, verification.FirstBrokenSequence);
        Assert.Equal("Entry 4 does not reference the hash of entry 3.", verification.Reason);
    }

    [Fact]
    public async Task Removing_the_newest_entries_leaves_a_shorter_intact_chain_that_only_the_count_reveals()
    {
        // The limit of any hash chain: truncating its end is invisible without an external anchor, which is what
        // EntriesChecked and, on SQL Server, the database ledger digest are for.
        await AppendAsync(5);

        await ExecuteAsync("DELETE FROM AuditEntries WHERE Sequence >= 4");

        var verification = await Log.VerifyAsync(_tenant, CancellationToken.None);
        Assert.True(verification.IsIntact);
        Assert.Equal(3, verification.EntriesChecked);
    }

    [Fact]
    public async Task Verification_walks_chains_longer_than_one_batch_and_finds_a_break_beyond_the_first()
    {
        var count = (AuditLog.VerifyBatchSize * 2) + 3;
        await AppendAsync(count);
        var intact = await Log.VerifyAsync(_tenant, CancellationToken.None);
        Assert.True(intact.IsIntact, intact.Reason);
        Assert.Equal(count, intact.EntriesChecked);

        await ExecuteAsync("UPDATE AuditEntries SET Model = 'x' WHERE Sequence = $sequence", ("$sequence", count - 1));

        var broken = await Log.VerifyAsync(_tenant, CancellationToken.None);
        Assert.False(broken.IsIntact);
        Assert.Equal(count - 1, broken.FirstBrokenSequence);
        Assert.Equal(count - 1, broken.EntriesChecked);
    }

    [Fact]
    public async Task An_append_that_loses_the_race_for_a_sequence_is_retried_and_linked_to_the_winner()
    {
        await AppendAsync(2);
        _competingWriter.Arm(1, i => AuditTestData.Event(_tenant, 100 + i, subjectId: "other-gateway-instance"));

        var ours = await Log.AppendAsync(AuditTestData.Event(_tenant, 3), CancellationToken.None);

        var winner = Assert.Single(_competingWriter.Committed);
        Assert.Equal(3, winner.Sequence);
        Assert.Equal(4, ours.Sequence);
        Assert.Equal(winner.Hash, ours.PreviousHash);
        var verification = await Log.VerifyAsync(_tenant, CancellationToken.None);
        Assert.True(verification.IsIntact, verification.Reason);
        Assert.Equal(4, verification.EntriesChecked);
    }

    [Fact]
    public async Task An_append_that_keeps_losing_the_race_gives_up_after_a_bounded_number_of_attempts()
    {
        await AppendAsync(1);
        _competingWriter.Arm(AuditLog.MaxAppendAttempts, i => AuditTestData.Event(_tenant, 100 + i, subjectId: "other-gateway-instance"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Log.AppendAsync(AuditTestData.Event(_tenant, 1), CancellationToken.None));

        Assert.IsAssignableFrom<DbUpdateException>(error.InnerException);
        Assert.Equal(AuditLog.MaxAppendAttempts, _competingWriter.Committed.Count);
        var verification = await Log.VerifyAsync(_tenant, CancellationToken.None);
        Assert.True(verification.IsIntact, verification.Reason);
        Assert.Equal(1 + AuditLog.MaxAppendAttempts, verification.EntriesChecked);
        Assert.Equal(0, ((AuditLog)Log).ActiveTenantLocks);
    }

    [Fact]
    public async Task Redacted_prompts_are_kept_only_when_the_audit_policy_allows_storing_them()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var forbidding = AuditTestServices.ForSqlite(connection, storeRedactedPrompts: false);
        await using (var db = await forbidding.GetRequiredService<IDbContextFactory<SentinelDbContext>>().CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var withPrompt = AuditTestData.Event(_tenant) with { RedactedPrompt = "Who approved [EMAIL_1]?" };
        var forbidden = await forbidding.GetRequiredService<IAuditLog>().AppendAsync(withPrompt, CancellationToken.None);
        var allowed = await Log.AppendAsync(withPrompt, CancellationToken.None);

        Assert.Null(forbidden.Event.RedactedPrompt);
        Assert.Equal(withPrompt.PromptDigest, forbidden.Event.PromptDigest);
        Assert.Equal(withPrompt.RedactedPrompt, allowed.Event.RedactedPrompt);
    }

    [Fact]
    public async Task Entries_are_stored_in_their_canonical_form_and_read_back_exactly_as_appended()
    {
        var local = new DateTime(2026, 5, 1, 12, 30, 0, DateTimeKind.Local);
        var appended = await Log.AppendAsync(
            AuditTestData.Event(_tenant) with
            {
                OccurredAtUtc = local,
                EstimatedCostUsd = 0.123456785m,
                LatencyMs = -0d,
                RedactedPii = new Dictionary<string, int> { ["PhoneNumber"] = 1, ["Email"] = 2 },
            },
            CancellationToken.None);

        Assert.Equal(DateTimeKind.Utc, appended.Event.OccurredAtUtc.Kind);
        Assert.Equal(local.ToUniversalTime(), appended.Event.OccurredAtUtc);
        Assert.Equal(0.12345678m, appended.Event.EstimatedCostUsd);
        Assert.Equal(["Email", "PhoneNumber"], appended.Event.RedactedPii.Keys);

        var listed = Assert.Single((await Log.ListAsync(new AuditQuery(_tenant, null, null, null, null, 1, 10), CancellationToken.None)).Items);
        Assert.Equivalent(appended, listed, strict: true);
        Assert.Equal(DateTimeKind.Utc, listed.Event.OccurredAtUtc.Kind);
        Assert.True((await Log.VerifyAsync(_tenant, CancellationToken.None)).IsIntact);
    }

    [Fact]
    public async Task Oversized_free_text_is_truncated_to_its_column_instead_of_losing_the_entry()
    {
        var appended = await Log.AppendAsync(
            AuditTestData.Event(_tenant) with
            {
                Model = new string('m', 1000),
                Subject = new string('s', 254) + "\U0001F600" + new string('t', 10),
            },
            CancellationToken.None);

        Assert.Equal(new string('m', 127) + "\u2026", appended.Event.Model);

        // The cut never splits a surrogate pair.
        Assert.Equal(new string('s', 254) + "\u2026", appended.Event.Subject);
        Assert.True((await Log.VerifyAsync(_tenant, CancellationToken.None)).IsIntact);
    }

    [Fact]
    public async Task Unpaired_surrogates_from_callers_are_stored_as_replacement_characters_and_the_chain_still_verifies()
    {
        // SQLite (and the JSON serializer) would silently store U+FFFD anyway; hashing anything else would make an
        // honest entry look tampered with.
        var appended = await Log.AppendAsync(
            AuditTestData.Event(_tenant) with { Model = "gpt\uD800-4", Subject = "doc\uDC00", Sources = ["doc\uD800:1"] },
            CancellationToken.None);

        Assert.Equal("gpt\uFFFD-4", appended.Event.Model);
        Assert.Equal("doc\uFFFD", appended.Event.Subject);
        Assert.Equal(["doc\uFFFD:1"], appended.Event.Sources);
        var verification = await Log.VerifyAsync(_tenant, CancellationToken.None);
        Assert.True(verification.IsIntact, verification.Reason);
        Assert.Equivalent(appended, Assert.Single((await Log.ListAsync(new AuditQuery(_tenant, null, null, null, null, 1, 10), CancellationToken.None)).Items), strict: true);
    }

    [Fact]
    public async Task Events_that_cannot_be_recorded_faithfully_are_rejected_before_anything_is_written()
    {
        var valid = AuditTestData.Event(_tenant);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Log.AppendAsync(valid with { TenantId = " " }, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Log.AppendAsync(valid with { TenantId = _tenant + "\uD800" }, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Log.AppendAsync(valid with { SubjectId = new string('x', 129) }, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Log.AppendAsync(valid with { Outcome = (AuditOutcome)42 }, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Log.AppendAsync(valid with { LatencyMs = double.NaN }, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Log.AppendAsync(valid with { EstimatedCostUsd = 10_000_000_000m }, CancellationToken.None));

        Assert.Equal(0, (await Log.VerifyAsync(_tenant, CancellationToken.None)).EntriesChecked);
    }

    [Fact]
    public async Task A_caller_that_has_already_gone_away_cannot_suppress_its_entry()
    {
        var entry = await Log.AppendAsync(AuditTestData.Event(_tenant), new CancellationToken(canceled: true));

        Assert.Equal(1, entry.Sequence);
        Assert.Equal(1, (await Log.VerifyAsync(_tenant, CancellationToken.None)).EntriesChecked);
    }

    [Fact]
    public async Task Listing_never_reinterprets_a_tampered_value()
    {
        await AppendAsync(2);
        await ExecuteAsync("UPDATE AuditEntries SET Outcome = '2' WHERE Sequence = 2");

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => Log.ListAsync(new AuditQuery(_tenant, null, null, null, null, 1, 10), CancellationToken.None));

        Assert.Contains("entry 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_filters_by_time_subject_and_outcome_and_pages_newest_first() =>
        await AuditLogScenarios.ListingFiltersAndPagesNewestFirstAsync(Log);

    private async Task<List<AuditEntry>> AppendAsync(int count)
    {
        var entries = new List<AuditEntry>(count);
        for (var i = 0; i < count; i++)
        {
            entries.Add(await Log.AppendAsync(AuditTestData.Event(_tenant, i), CancellationToken.None));
        }

        return entries;
    }

    private async Task<SentinelDbContext> CreateContextAsync() =>
        await _services.GetRequiredService<IDbContextFactory<SentinelDbContext>>().CreateDbContextAsync();

    private async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Plays a second gateway instance: right before our INSERT reaches the database it commits the very sequence we
    /// are about to claim, so our insert violates the unique (TenantId, Sequence) index. Wins as many races as armed.
    /// </summary>
    private sealed class CompetingWriter : DbCommandInterceptor
    {
        private Func<int, AuditEvent>? _competitor;
        private int _remaining;

        public List<AuditEntryRecord> Committed { get; } = [];

        public void Arm(int races, Func<int, AuditEvent> competitor)
        {
            _remaining = races;
            _competitor = competitor;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            CommitCompetingEntry(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            CommitCompetingEntry(command);
            return ValueTask.FromResult(result);
        }

        private void CommitCompetingEntry(DbCommand command)
        {
            if (_remaining <= 0 || _competitor is null || !command.CommandText.Contains("INSERT INTO \"AuditEntries\"", StringComparison.Ordinal))
            {
                return;
            }

            _remaining--;
            var auditEvent = _competitor(Committed.Count);
            using var other = new SentinelDbContext(new DbContextOptionsBuilder<SentinelDbContext>().UseSqlite(command.Connection!).Options);
            var head = other.Set<AuditEntryRecord>()
                .Where(r => r.TenantId == auditEvent.TenantId)
                .OrderByDescending(r => r.Sequence)
                .First();
            var winner = AuditEntryMapper.ToTemplate(auditEvent, storeRedactedPrompt: true).ChainAt(head.Sequence + 1, head.Hash);
            other.Add(winner);
            other.SaveChanges();
            Committed.Add(winner);
        }
    }
}
