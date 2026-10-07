using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Application.Diagnostics;
using Sentinel.Guardrails.Injection;
using Sentinel.Infrastructure.AI.ContentSafety;
using Sentinel.Infrastructure.AI.Learned;
using Sentinel.Infrastructure.AI.Offline;

namespace Sentinel.Infrastructure.AI;

/// <summary>Chat and embedding providers.</summary>
public static class AIServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IChatClientProvider"/>, the <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>
    /// and, when <c>Ai:ContentSafety:Enabled</c>, wraps the already registered <see cref="IPromptInjectionDetector"/> in
    /// a composite with Azure Prompt Shields. Call it after the guardrails have been registered.
    /// </summary>
    public static IServiceCollection AddAiProviders(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(AiOptions.Section);
        services.AddOptions<AiOptions>().Bind(section).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AiOptions>, AiOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();
        services.PostConfigure<ModelCatalogOptions>(DefaultModelTiers.ApplyWhenEmpty);

        // Registration-time view of the configuration: decides what gets registered, not how it behaves.
        var configured = section.Get<AiOptions>() ?? new AiOptions();
        if (configured.Provider == AiProvider.Offline && configuration[$"{RagOptions.Section}:{nameof(RagOptions.MinSimilarity)}"] is null)
        {
            services.PostConfigure<RagOptions>(rag => rag.MinSimilarity = OfflineAiBackend.DefaultMinSimilarity);
        }

        AddProviderHttpClient(services);

        services.TryAddSingleton<IAiBackend>(sp => sp.GetRequiredService<IOptions<AiOptions>>().Value.Provider switch
        {
            AiProvider.Offline => new OfflineAiBackend(sp.GetRequiredService<TimeProvider>()),
            _ => ActivatorUtilities.CreateInstance<OpenAIAiBackend>(sp),
        });
        services.TryAddSingleton<IChatClientProvider, ChatClientProvider>();
        services.TryAddSingleton(BuildEmbeddingGenerator);

        if (configured.ContentSafety.Enabled)
        {
            AddPromptShields(services);
        }

        AddLearnedInjectionDetector(services, configuration, configured);
        return services;
    }

    /// <summary>
    /// Adds the embedding-based classifier on top of whatever detector is registered (rules, Prompt Shields), sharing the
    /// verdict combination of the shield composite: an attack if any detector says so. It is tied to the embedding
    /// model it was trained on, so enabling it with another provider is a startup error instead of silent nonsense.
    /// </summary>
    private static void AddLearnedInjectionDetector(IServiceCollection services, IConfiguration configuration, AiOptions ai)
    {
        var section = configuration.GetSection(LearnedInjectionOptions.Section);
        services.AddOptions<LearnedInjectionOptions>().Bind(section);
        if (!section.GetValue(nameof(LearnedInjectionOptions.Enabled), false))
        {
            return;
        }

        var model = LearnedInjectionModel.Load();
        if (ai.Provider == AiProvider.Offline || !string.Equals(ai.EmbeddingModel, model.EmbeddingModel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Guardrails:Injection:Learned is trained on '{model.EmbeddingModel}' embeddings and cannot be used with provider " +
                $"'{ai.Provider}' and embedding model '{ai.EmbeddingModel}'. Use an OpenAI-compatible provider serving that model, " +
                "retrain the classifier (eval/train) or disable it.");
        }

        var previous = services.LastOrDefault(d => d.ServiceType == typeof(IPromptInjectionDetector) && !d.IsKeyedService);
        if (previous is null)
        {
            return;
        }

        services.AddSingleton(model);
        services.Remove(previous);
        services.Add(new ServiceDescriptor(
            typeof(IPromptInjectionDetector),
            sp => new CompositePromptInjectionDetector(
            [
                new PreviousPromptInjectionDetector(sp, previous).Detector,
                ActivatorUtilities.CreateInstance<LearnedInjectionDetector>(sp),
            ]),
            ServiceLifetime.Transient));
    }

    /// <summary>
    /// One long-lived client for all provider traffic. HttpClient.Timeout is disabled because the resilience pipeline
    /// owns every limit (a 100 s default would cut off the 180 s total budget and long streams); connections are
    /// recycled by the socket handler instead of handler rotation, since the OpenAI client holds on to its HttpClient.
    /// </summary>
    private static void AddProviderHttpClient(IServiceCollection services)
    {
        var providerClient = services.AddHttpClient(OpenAIAiBackend.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .RedactLoggedHeaders(static _ => true);

        // Host-wide defaults (ConfigureHttpClientDefaults) typically carry a 10 s attempt timeout: wrong for LLMs.
        // The API is experimental, but it is the only way to opt a named client out of those defaults.
#pragma warning disable EXTEXP0001
        providerClient.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        providerClient.AddStandardResilienceHandler()
            .Configure((resilience, sp) => ConfigureResilience(resilience, sp.GetRequiredService<IOptions<AiOptions>>().Value));
    }

    internal static void ConfigureResilience(HttpStandardResilienceOptions resilience, AiOptions options)
    {
        resilience.AttemptTimeout.Timeout = options.AttemptTimeout;
        resilience.TotalRequestTimeout.Timeout = options.TotalTimeout;

        // Only transient failures (network errors, timeouts, 408, 429 honouring Retry-After, 5xx) are retried: a bad
        // request, a content-filter rejection or an auth error will not succeed on a second try. A retried timeout can
        // be billed twice by the provider; that is the price of the attempt timeout and why retries stay bounded.
        resilience.Retry.ShouldHandle = static args => ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(args.Outcome));

        // The breaker must observe at least two attempt timeouts to judge failure rates (also enforced by validation).
        var sampling = options.AttemptTimeout * 2;
        resilience.CircuitBreaker.SamplingDuration = sampling > resilience.CircuitBreaker.SamplingDuration ? sampling : resilience.CircuitBreaker.SamplingDuration;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> BuildEmbeddingGenerator(IServiceProvider services)
    {
        var backend = services.GetRequiredService<IAiBackend>();
        var options = services.GetRequiredService<IOptions<AiOptions>>().Value;
        var loggerFactory = new ContentFreeLoggerFactory(services.GetRequiredService<ILoggerFactory>());
        var model = options.Provider == AiProvider.Offline ? HashingEmbeddingGenerator.ModelId : options.EmbeddingModel;

        return new EmbeddingGeneratorBuilder<string, Embedding<float>>(
                new DimensionValidatingEmbeddingGenerator(backend.CreateEmbeddingGenerator(), model))
            .UseOpenTelemetry(loggerFactory, SentinelTelemetry.SourceName, configure: g => g.EnableSensitiveData = false)
            .UseLogging(loggerFactory)
            .Build(services);
    }

    /// <summary>
    /// Replaces the effective <see cref="IPromptInjectionDetector"/> registration with a composite of that detector
    /// and Prompt Shields. The previous registration is re-created from its own descriptor (instance, factory or type)
    /// with its own lifetime; the composite itself is transient because it holds a typed (transient) HttpClient.
    /// </summary>
    private static void AddPromptShields(IServiceCollection services)
    {
        var shieldClient = services.AddHttpClient<ContentSafetyPromptShield>((sp, client) =>
            {
                client.Timeout = sp.GetRequiredService<IOptions<AiOptions>>().Value.ContentSafety.Timeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .RedactLoggedHeaders(static _ => true);

        // The shield has its own short budget (Ai:ContentSafety:Timeout); host-wide retry defaults would exceed it.
#pragma warning disable EXTEXP0001
        shieldClient.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        if (services.Any(d => d.ServiceType == typeof(PreviousPromptInjectionDetector)))
        {
            return;
        }

        var previous = services.LastOrDefault(d => d.ServiceType == typeof(IPromptInjectionDetector) && !d.IsKeyedService);
        if (previous is null)
        {
            services.AddTransient<IPromptInjectionDetector>(sp => sp.GetRequiredService<ContentSafetyPromptShield>());
            return;
        }

        services.Remove(previous);
        services.Add(new ServiceDescriptor(
            typeof(PreviousPromptInjectionDetector), sp => new PreviousPromptInjectionDetector(sp, previous), previous.Lifetime));
        services.AddTransient<IPromptInjectionDetector>(sp => new CompositePromptInjectionDetector(
            [sp.GetRequiredService<PreviousPromptInjectionDetector>().Detector, sp.GetRequiredService<ContentSafetyPromptShield>()]));
    }
}
