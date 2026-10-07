using Sentinel.Application.Abstractions;

namespace Sentinel.Infrastructure.CostControls.Caching;

/// <summary>Scope validation shared by both cache implementations, so they accept and refuse exactly the same scopes.</summary>
internal static class CacheScopeRules
{
    /// <summary>
    /// Throws for missing parts (a caller bug). Returns false for parts that cannot be partitioned exactly (see
    /// <see cref="RedisTag.IsRepresentable"/>): such a scope is simply never cached, which fails closed.
    /// </summary>
    public static bool IsCacheable(SemanticCacheScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.TenantId, nameof(scope));
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.Namespace, nameof(scope));
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.ModelTier, nameof(scope));

        return RedisTag.IsRepresentable(scope.TenantId)
            && RedisTag.IsRepresentable(scope.Namespace)
            && RedisTag.IsRepresentable(scope.ModelTier);
    }
}
