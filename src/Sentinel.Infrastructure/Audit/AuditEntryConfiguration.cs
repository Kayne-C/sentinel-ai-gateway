using Microsoft.EntityFrameworkCore;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Audit;

/// <summary>Column sizes shared by the EF model, the append-time normalisation and <see cref="AuditLedgerSql"/>.</summary>
internal static class AuditSchema
{
    public const string TableName = AuditLedgerSql.TableName;

    /// <summary>
    /// SQL Server's default collation is case-insensitive, which would make <c>Contoso</c> and <c>contoso</c> one chain
    /// (and one listing) in the database while they are two tenants everywhere in .NET. A binary collation keeps the
    /// database's notion of "same tenant / same subject" identical to ordinal string equality.
    /// </summary>
    public const string BinaryCollation = "Latin1_General_100_BIN2";

    public const int IdentifierMaxLength = 128;
    public const int EnumMaxLength = 32;
    public const int ModelMaxLength = 128;
    public const int ModelTierMaxLength = 64;
    public const int PromptDigestMaxLength = 128;
    public const int ErrorCodeMaxLength = 128;
    public const int TraceIdMaxLength = 128;

    /// <summary>Document external ids are at most 200 characters (<c>KnowledgeDocument.MaxExternalIdLength</c>).</summary>
    public const int SubjectMaxLength = 256;

    /// <summary>Lower-case hex SHA-256.</summary>
    public const int HashLength = 64;

    /// <summary><c>decimal(18,8)</c>: ten integer digits.</summary>
    public const int CostPrecision = 18;
    public const int CostScale = 8;
}

internal sealed class AuditEntryConfiguration : IModelConfiguration
{
    public void Configure(ModelBuilder modelBuilder, DatabaseProvider provider)
    {
        var entity = modelBuilder.Entity<AuditEntryRecord>();
        entity.ToTable(AuditSchema.TableName);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).ValueGeneratedOnAdd();

        entity.Property(e => e.TenantId).HasMaxLength(AuditSchema.IdentifierMaxLength);
        entity.Property(e => e.SubjectId).HasMaxLength(AuditSchema.IdentifierMaxLength);
        entity.Property(e => e.Operation).HasMaxLength(AuditSchema.EnumMaxLength);
        entity.Property(e => e.Outcome).HasMaxLength(AuditSchema.EnumMaxLength);
        entity.Property(e => e.Model).HasMaxLength(AuditSchema.ModelMaxLength);
        entity.Property(e => e.ModelTier).HasMaxLength(AuditSchema.ModelTierMaxLength);
        entity.Property(e => e.EstimatedCostUsd).HasPrecision(AuditSchema.CostPrecision, AuditSchema.CostScale);
        entity.Property(e => e.PromptDigest).HasMaxLength(AuditSchema.PromptDigestMaxLength);
        entity.Property(e => e.ErrorCode).HasMaxLength(AuditSchema.ErrorCodeMaxLength);
        entity.Property(e => e.TraceId).HasMaxLength(AuditSchema.TraceIdMaxLength);
        entity.Property(e => e.Subject).HasMaxLength(AuditSchema.SubjectMaxLength);
        entity.Property(e => e.PreviousHash).HasMaxLength(AuditSchema.HashLength).IsUnicode(false);
        entity.Property(e => e.Hash).HasMaxLength(AuditSchema.HashLength).IsUnicode(false);

        // The unique index is the cross-process half of the append protocol: two writers that read the same chain
        // head cannot both commit the next sequence.
        entity.HasIndex(e => new { e.TenantId, e.Sequence }).IsUnique();
        entity.HasIndex(e => new { e.TenantId, e.OccurredAtUtc });

        if (provider == DatabaseProvider.SqlServer)
        {
            entity.Property(e => e.TenantId).UseCollation(AuditSchema.BinaryCollation);
            entity.Property(e => e.SubjectId).UseCollation(AuditSchema.BinaryCollation);
        }
    }
}
