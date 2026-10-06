using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sentinel.Application.Common;
using Sentinel.Application.Knowledge;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Knowledge;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Knowledge;

/// <summary>Tables of the knowledge base: Documents, DocumentPrincipals (the ACL) and DocumentChunks.</summary>
internal sealed class KnowledgeModelConfiguration : IModelConfiguration
{
    public const string DocumentsTable = "Documents";
    public const string PrincipalsTable = "DocumentPrincipals";
    public const string ChunksTable = "DocumentChunks";

    /// <summary>
    /// Tenant ids, principals and external ids are compared ordinally everywhere in the domain. SQL Server's default
    /// case- and accent-insensitive collations would make e.g. <c>group:fınance</c> (dotless ı) equal
    /// <c>group:finance</c>; a binary collation keeps the database's notion of "same principal" identical to ours.
    /// </summary>
    private const string SqlServerBinaryCollation = "Latin1_General_100_BIN2";

    private static readonly ValueConverter<DateTime, DateTime> UtcConverter =
        new(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    public void Configure(ModelBuilder modelBuilder, DatabaseProvider provider)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<DocumentRecord>(document =>
        {
            document.ToTable(DocumentsTable);
            document.HasKey(d => d.Id);
            document.Property(d => d.Id).ValueGeneratedNever();
            Identifier(document.Property(d => d.TenantId), KnowledgeLimits.TenantIdMaxLength, provider);
            Identifier(document.Property(d => d.ExternalId), KnowledgeDocument.MaxExternalIdLength, provider);
            document.Property(d => d.Title).HasMaxLength(KnowledgeDocument.MaxTitleLength).IsRequired();
            document.Property(d => d.SourceUri).HasMaxLength(KnowledgeLimits.SourceUriMaxLength);
            document.Property(d => d.Classification).HasConversion<string>().HasMaxLength(32).IsRequired();
            document.Property(d => d.Version).IsConcurrencyToken();
            document.Property(d => d.ContentHash).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
            document.Property(d => d.CreatedAtUtc).HasConversion(UtcConverter);
            document.Property(d => d.UpdatedAtUtc).HasConversion(UtcConverter);

            document.HasIndex(d => new { d.TenantId, d.ExternalId }).IsUnique();
            document.HasIndex(d => new { d.TenantId, d.UpdatedAtUtc });

            document.HasMany(d => d.Principals).WithOne().HasForeignKey(p => p.DocumentId).OnDelete(DeleteBehavior.Cascade);
            document.HasMany(d => d.Chunks).WithOne().HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentPrincipalRecord>(principal =>
        {
            principal.ToTable(PrincipalsTable);
            principal.HasKey(p => new { p.DocumentId, p.Principal });
            Identifier(principal.Property(p => p.TenantId), KnowledgeLimits.TenantIdMaxLength, provider);
            Identifier(principal.Property(p => p.Principal), AccessPrincipal.MaxLength, provider);

            // The ACL pre-filter: "does this tenant's principal P grant access to document D?" is an index seek.
            principal.HasIndex(p => new { p.TenantId, p.Principal, p.DocumentId });
        });

        modelBuilder.Entity<ChunkRecord>(chunk =>
        {
            chunk.ToTable(ChunksTable);
            var key = chunk.HasKey(c => c.Id);
            chunk.Property(c => c.Id).ValueGeneratedOnAdd();
            Identifier(chunk.Property(c => c.TenantId), KnowledgeLimits.TenantIdMaxLength, provider);
            chunk.Property(c => c.Text).IsRequired();
            chunk.Property(c => c.QuarantineReason).HasMaxLength(KnowledgeLimits.QuarantineReasonMaxLength);

            // DocumentId determines TenantId, so this is the (DocumentId, Ordinal) uniqueness rule; leading with the
            // tenant lets exact vector search read one tenant's chunks as a contiguous range.
            var tenantRange = chunk.HasIndex(c => new { c.TenantId, c.DocumentId, c.Ordinal }).IsUnique();

            if (provider == DatabaseProvider.SqlServer)
            {
                key.IsClustered(false);
                tenantRange.IsClustered();
                chunk.Ignore(c => c.EmbeddingBytes);
                chunk.Property(c => c.Vector)
                    .HasColumnName("Embedding")
                    .HasColumnType($"vector({EmbeddingDefaults.Dimensions})")
                    .IsRequired();
            }
            else
            {
                chunk.Ignore(c => c.Vector);
                chunk.Property(c => c.EmbeddingBytes).HasColumnName("Embedding").IsRequired();
            }
        });
    }

    private static void Identifier(PropertyBuilder<string> property, int maxLength, DatabaseProvider provider)
    {
        property.HasMaxLength(maxLength).IsRequired();
        if (provider == DatabaseProvider.SqlServer)
        {
            property.UseCollation(SqlServerBinaryCollation);
        }
    }
}
