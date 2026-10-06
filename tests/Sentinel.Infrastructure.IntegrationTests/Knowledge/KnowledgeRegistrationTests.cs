using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Knowledge;
using Sentinel.Infrastructure.Knowledge;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.IntegrationTests.Knowledge;

public sealed class KnowledgeRegistrationTests
{
    [Fact]
    public void Sqlite_contexts_get_the_in_process_vector_search_and_bound_chunking_options()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var provider = Build(new DbContextOptionsBuilder<SentinelDbContext>().UseSqlite(connection).Options, new() { ["Knowledge:Chunking:TargetTokens"] = "120" });
        using var scope = provider.CreateScope();

        Assert.IsType<InProcessVectorSearch>(scope.ServiceProvider.GetRequiredService<IVectorSearch>());
        Assert.Same(scope.ServiceProvider.GetRequiredService<IKnowledgeRepository>(), scope.ServiceProvider.GetRequiredService<IDocumentChunkCounter>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TextChunker>());
        var chunking = provider.GetRequiredService<IOptions<ChunkingOptions>>().Value;
        Assert.Equal((120, 40, 2000), (chunking.TargetTokens, chunking.OverlapTokens, chunking.MaxChunks));
    }

    [Fact]
    public void Sql_server_contexts_get_the_native_vector_search()
    {
        var options = new DbContextOptionsBuilder<SentinelDbContext>()
            .UseSqlServer("Server=unused;Database=unused;TrustServerCertificate=True", sql => sql.UseCompatibilityLevel(170))
            .Options;
        using var provider = Build(options, []);
        using var scope = provider.CreateScope();

        Assert.IsType<SqlServerVectorSearch>(scope.ServiceProvider.GetRequiredService<IVectorSearch>());
    }

    [Fact]
    public void Invalid_chunking_options_are_rejected()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var provider = Build(
            new DbContextOptionsBuilder<SentinelDbContext>().UseSqlite(connection).Options,
            new() { ["Knowledge:Chunking:TargetTokens"] = "40", ["Knowledge:Chunking:OverlapTokens"] = "40" });

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ChunkingOptions>>().Value);
    }

    [Fact]
    public void The_sql_server_model_maps_embeddings_to_a_native_384_dimension_vector_column()
    {
        var options = new DbContextOptionsBuilder<SentinelDbContext>()
            .UseSqlServer("Server=unused;Database=unused;TrustServerCertificate=True", sql => sql.UseCompatibilityLevel(170))
            .Options;
        using var db = new SentinelDbContext(options);

        var chunk = db.Model.FindEntityType(typeof(ChunkRecord))!;
        Assert.Equal("DocumentChunks", chunk.GetTableName());
        Assert.Equal("vector(384)", chunk.FindProperty(nameof(ChunkRecord.Vector))!.GetColumnType());
        Assert.Null(chunk.FindProperty(nameof(ChunkRecord.EmbeddingBytes)));

        var principals = db.Model.FindEntityType(typeof(DocumentPrincipalRecord))!;
        Assert.Contains(principals.GetIndexes(), i => i.Properties.Select(p => p.Name).SequenceEqual(["TenantId", "Principal", "DocumentId"]));
        Assert.Equal(["DocumentId", "Principal"], principals.FindPrimaryKey()!.Properties.Select(p => p.Name));

        var documents = db.Model.FindEntityType(typeof(DocumentRecord))!;
        Assert.Contains(documents.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["TenantId", "ExternalId"]));
    }

    [Fact]
    public void The_sqlite_model_stores_embeddings_as_bytes()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var db = new SentinelDbContext(new DbContextOptionsBuilder<SentinelDbContext>().UseSqlite(connection).Options);

        var chunk = db.Model.FindEntityType(typeof(ChunkRecord))!;
        Assert.Null(chunk.FindProperty(nameof(ChunkRecord.Vector)));
        Assert.Equal("Embedding", chunk.FindProperty(nameof(ChunkRecord.EmbeddingBytes))!.GetColumnName());
    }

    private static ServiceProvider Build(DbContextOptions<SentinelDbContext> options, Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new SentinelDbContext(options));
        services.AddSingleton<ITokenCounter, WordTokenCounter>();
        services.AddLogging();
        services.AddKnowledgeStore(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
