using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Application.Diagnostics;

namespace Sentinel.Infrastructure.AI;

/// <summary>
/// One telemetry-wrapped client per tier, built on first use and cached. OpenTelemetry is configured without
/// sensitive data and logging never at Trace (see <see cref="ContentFreeLoggerFactory"/>), so neither prompts nor
/// completions are recorded by the instrumentation. Unknown tiers resolve like <see cref="ModelCatalogOptions.GetTier"/>:
/// to the default tier.
/// </summary>
internal sealed class ChatClientProvider : IChatClientProvider, IDisposable
{
    private readonly IAiBackend _backend;
    private readonly ModelCatalogOptions _catalog;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, Lazy<IChatClient>> _clients = new(StringComparer.OrdinalIgnoreCase);

    public ChatClientProvider(IAiBackend backend, IOptions<ModelCatalogOptions> catalog, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _backend = backend;
        _catalog = catalog.Value.Tiers.Count > 0 ? catalog.Value : DefaultModelTiers.Create(catalog.Value.DefaultTier);
        _loggerFactory = new ContentFreeLoggerFactory(loggerFactory);
    }

    public IChatClient GetClient(string tier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tier);

        var (name, options) = Resolve(tier);
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException($"Model tier '{name}' has no model configured (Models:Tiers:{name}:Model).");
        }

        return _clients.GetOrAdd(name, _ => new Lazy<IChatClient>(() => Build(options.Model), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values.Where(c => c.IsValueCreated))
        {
            client.Value.Dispose();
        }

        _clients.Clear();
        _loggerFactory.Dispose();
    }

    private (string Name, ModelTierOptions Options) Resolve(string tier)
    {
        if (_catalog.Tiers.TryGetValue(tier, out var options))
        {
            return (tier, options);
        }

        if (_catalog.Tiers.TryGetValue(_catalog.DefaultTier, out options))
        {
            return (_catalog.DefaultTier, options);
        }

        throw new InvalidOperationException($"Model tier '{tier}' is not configured and the default tier '{_catalog.DefaultTier}' does not exist.");
    }

    private IChatClient Build(string model) =>
        new ChatClientBuilder(_backend.CreateChatClient(model))
            .UseOpenTelemetry(_loggerFactory, SentinelTelemetry.SourceName, configure: c => c.EnableSensitiveData = false)
            .UseLogging(_loggerFactory)
            .Build();
}
