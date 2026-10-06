using Microsoft.EntityFrameworkCore;

namespace Sentinel.Infrastructure.Persistence;

/// <summary>
/// Provider-aware model configuration. Every implementation in this assembly is applied by
/// <see cref="SentinelDbContext"/>; modules add their tables without touching the context.
/// </summary>
internal interface IModelConfiguration
{
    void Configure(ModelBuilder modelBuilder, DatabaseProvider provider);
}
