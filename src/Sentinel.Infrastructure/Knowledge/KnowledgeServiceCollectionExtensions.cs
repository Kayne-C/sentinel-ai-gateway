using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Knowledge;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Knowledge;

/// <summary>Knowledge base: EF Core records, vector search, repository.</summary>
public static class KnowledgeServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledgeStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ChunkingOptions>()
            .Bind(configuration.GetSection(ChunkingOptions.Section))
            .Validate(
                ChunkingOptions.IsValid,
                "Knowledge:Chunking requires TargetTokens > 0, 0 <= OverlapTokens < TargetTokens and MaxChunks > 0.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<TextChunker>();

        services.AddScoped<KnowledgeRepository>();
        services.AddScoped<IKnowledgeRepository>(sp => sp.GetRequiredService<KnowledgeRepository>());
        services.AddScoped<IDocumentChunkCounter>(sp => sp.GetRequiredService<KnowledgeRepository>());

        // The implementation follows the provider the scoped context was actually built with (configured from
        // DatabaseOptions.Provider), so search and storage can never disagree about how embeddings are stored.
        services.AddScoped<SqlServerVectorSearch>();
        services.AddScoped<InProcessVectorSearch>();
        services.AddScoped<IVectorSearch>(sp => sp.GetRequiredService<SentinelDbContext>().Provider == DatabaseProvider.SqlServer
            ? sp.GetRequiredService<SqlServerVectorSearch>()
            : sp.GetRequiredService<InProcessVectorSearch>());

        return services;
    }
}
