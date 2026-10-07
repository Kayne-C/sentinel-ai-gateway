using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Sentinel.Domain.Identity;

namespace Sentinel.SemanticKernel;

/// <summary>Builds one guarded <see cref="Kernel"/> per request.</summary>
public sealed class SentinelKernelFactory(IServiceProvider services)
{
    /// <summary>
    /// A kernel whose chat model is <paramref name="chatClient"/> (any <c>Microsoft.Extensions.AI</c> client), with the
    /// prompt and tool-result filters installed and the permission-aware knowledge plugin registered. The caller is
    /// attached; read <see cref="SentinelRequestContext.Vault"/> afterwards to post-process the final answer.
    /// </summary>
    public Kernel Create(CallerIdentity caller, IChatClient chatClient, bool includeKnowledgePlugin = true)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(chatClient);

        // Function calling runs through Semantic Kernel (so its auto-function filters see every tool call and result).
        var kernelClient = new ChatClientBuilder(chatClient).UseKernelFunctionInvocation().Build(services);
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(kernelClient.AsChatCompletionService(services));
        var kernel = builder.Build();

        kernel.UseSentinel(caller);
        kernel.PromptRenderFilters.Add(ActivatorUtilities.CreateInstance<SentinelPromptRenderFilter>(services));
        kernel.AutoFunctionInvocationFilters.Add(ActivatorUtilities.CreateInstance<SentinelAutoFunctionFilter>(services));
        if (includeKnowledgePlugin)
        {
            kernel.Plugins.Add(ActivatorUtilities.CreateInstance<KnowledgePlugin>(services).ToKernelPlugin());
        }

        return kernel;
    }
}

public static class SentinelSemanticKernelServiceCollectionExtensions
{
    /// <summary>Registers <see cref="SentinelKernelFactory"/>. The host must already call <c>AddApplication</c> and <c>AddInfrastructure</c>.</summary>
    public static IServiceCollection AddSentinelSemanticKernel(this IServiceCollection services)
    {
        services.AddScoped<SentinelKernelFactory>();
        return services;
    }
}
