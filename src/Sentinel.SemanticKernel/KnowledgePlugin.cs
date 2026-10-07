using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Application.Guardrails;
using Sentinel.Guardrails.Injection;

namespace Sentinel.SemanticKernel;

/// <summary>
/// "Search the company knowledge base" as a Semantic Kernel function. Whatever the model asks for, the search runs with
/// the tenant and ACL principals of the caller attached to the kernel, so an agent cannot read more than its user can,
/// not even by being tricked: the principals are not a parameter of the function.
/// </summary>
public sealed class KnowledgePlugin(
    IPromptGuard guard,
    IVectorSearch vectorSearch,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IOptions<RagOptions> options)
{
    public const string PluginName = "Knowledge";

    /// <summary>
    /// Metadata key marking a function whose output has already been through the guardrails (and carries the gateway's own
    /// <c>&lt;document&gt;</c> wrapper, which a second scan would mistake for a delimiter injection).
    /// </summary>
    public const string GuardedMetadataKey = "sentinel.guarded";

    /// <summary>The plugin as the Kernel should register it: <c>search_knowledge</c>, marked as pre-guarded.</summary>
    public KernelPlugin ToKernelPlugin()
    {
        var function = KernelFunctionFactory.CreateFromMethod(
            SearchAsync,
            new KernelFunctionFromMethodOptions
            {
                FunctionName = "search_knowledge",
                Description = "Searches the company's internal documents the current user is allowed to read and returns the most relevant passages with their sources.",
                AdditionalMetadata = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(
                    new Dictionary<string, object?> { [GuardedMetadataKey] = true }),
            });
        return KernelPluginFactory.CreateFromFunctions(PluginName, [function]);
    }

    [KernelFunction("search_knowledge")]
    [Description("Searches the company's internal documents the current user is allowed to read and returns the most relevant passages with their sources.")]
    public async Task<string> SearchAsync(
        Kernel kernel,
        [Description("What to look for, as a short natural-language query.")] string query,
        [Description("How many passages to return (1-8).")] int top = 4,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        var request = SentinelRequestContext.From(kernel);
        var rag = options.Value;

        // The query is model-written and may be steered by an injection earlier in the conversation.
        var guardedQuery = await guard.GuardInputAsync([new ChatMessage(ChatRole.Tool, query ?? string.Empty)], request.Vault, cancellationToken);
        if (guardedQuery.Blocked)
        {
            throw new SentinelPolicyException("Guardrails.PromptInjection", "The search query was rejected by the prompt-injection guardrail.");
        }

        var embedding = (await embeddings.GenerateAsync([guardedQuery.Messages[0].Text], cancellationToken: cancellationToken)).Single().Vector;
        var found = await vectorSearch.SearchAsync(
            new VectorQuery(request.Caller.TenantId, request.Caller.Principals, embedding, Math.Clamp(top, 1, 8), rag.MinSimilarity),
            cancellationToken);

        var context = await guard.GuardContextAsync(found, request.Vault, cancellationToken);
        if (context.Chunks.Count == 0)
        {
            return "Bu sorgu için yetkili bir belge bulunamadı.";
        }

        var builder = new StringBuilder();
        foreach (var chunk in context.Chunks)
        {
            builder.AppendLine(Spotlight.FormatDocument(
                $"{chunk.ExternalId} v{chunk.DocumentVersion.ToString(CultureInfo.InvariantCulture)}", chunk.Text));
        }

        return builder.ToString();
    }
}
