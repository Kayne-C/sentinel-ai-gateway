namespace Sentinel.Infrastructure.Audit;

/// <summary>
/// SQL Server DDL that creates <c>AuditEntries</c> as an append-only ledger table. In the SQL Server migration,
/// <c>migrationBuilder.Sql(AuditLedgerSql.CreateTable)</c> replaces the generated
/// <c>migrationBuilder.CreateTable(name: "AuditEntries", ...)</c> call; the generated <c>CreateIndex</c> calls stay as
/// they are (<see cref="CreateIndexes"/> is the identical DDL, for scripts outside migrations; use one or the other).
/// <para>
/// Why a ledger table: the hash chain alone only proves that nobody changed a row without also recomputing every hash
/// after it, and anyone with write access to the table can do exactly that. With <c>LEDGER = ON (APPEND_ONLY = ON)</c>
/// the engine itself refuses UPDATE and DELETE (error 37359) and TRUNCATE TABLE (error 13545), for <c>db_owner</c> and
/// <c>sysadmin</c> as well, and ledger can never be switched off for the table. Every insert is recorded, together
/// with the principal that made it, in the database ledger (<c>sys.database_ledger_transactions</c>), whose
/// cryptographic digest (<c>sys.sp_generate_database_ledger_digest</c>, or automatic digest storage in immutable Azure
/// Blob Storage) lets <c>sys.sp_verify_database_ledger</c> prove later that no row was altered, even by someone who can
/// recompute SHA-256. Dropping the table does not erase it either: SQL Server keeps it as a dropped ledger table.
/// </para>
/// <para>
/// The engine adds two hidden <c>GENERATED ALWAYS</c> columns (<c>ledger_start_transaction_id</c>,
/// <c>ledger_start_sequence_number</c>). EF Core never sees them: its INSERT statements always name their columns and
/// its queries select mapped columns only, so the EF model and this script remain column-for-column identical.
/// </para>
/// </summary>
public static class AuditLedgerSql
{
    public const string TableName = "AuditEntries";

    /// <summary>Exactly what EF Core generates for the model's <c>CreateTable</c>, plus the ledger option.</summary>
    public const string CreateTable = """
        CREATE TABLE [AuditEntries] (
            [Id] bigint NOT NULL IDENTITY,
            [TenantId] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Sequence] bigint NOT NULL,
            [OccurredAtUtc] datetime2 NOT NULL,
            [SubjectId] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Operation] nvarchar(32) NOT NULL,
            [Outcome] nvarchar(32) NOT NULL,
            [Model] nvarchar(128) NULL,
            [ModelTier] nvarchar(64) NULL,
            [PromptTokens] int NOT NULL,
            [CompletionTokens] int NOT NULL,
            [EstimatedCostUsd] decimal(18,8) NOT NULL,
            [RedactedPiiJson] nvarchar(max) NOT NULL,
            [GuardrailFindingsJson] nvarchar(max) NOT NULL,
            [SourcesJson] nvarchar(max) NOT NULL,
            [PromptDigest] nvarchar(128) NULL,
            [RedactedPrompt] nvarchar(max) NULL,
            [ErrorCode] nvarchar(128) NULL,
            [LatencyMs] float NOT NULL,
            [TraceId] nvarchar(128) NULL,
            [Subject] nvarchar(256) NULL,
            [PreviousHash] varchar(64) NULL,
            [Hash] varchar(64) NOT NULL,
            CONSTRAINT [PK_AuditEntries] PRIMARY KEY ([Id])
        ) WITH (LEDGER = ON (APPEND_ONLY = ON));
        """;

    /// <summary>Exactly what EF Core generates for the model's two <c>CreateIndex</c> operations.</summary>
    public const string CreateIndexes = """
        CREATE INDEX [IX_AuditEntries_TenantId_OccurredAtUtc] ON [AuditEntries] ([TenantId], [OccurredAtUtc]);
        CREATE UNIQUE INDEX [IX_AuditEntries_TenantId_Sequence] ON [AuditEntries] ([TenantId], [Sequence]);
        """;
}
