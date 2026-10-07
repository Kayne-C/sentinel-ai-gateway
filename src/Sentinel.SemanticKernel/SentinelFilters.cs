using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Guardrails;
using Sentinel.Domain.Knowledge;

namespace Sentinel.SemanticKernel;

/// <summary>
/// Runs after a prompt template has been rendered and before the model sees it: PII in the arguments the template was
/// filled with is replaced by placeholders and a prompt-injection attempt stops the call. The same
/// <see cref="IPromptGuard"/> protects the REST API and the OpenAI proxy.
/// </summary>
public sealed class SentinelPromptRenderFilter(IPromptGuard guard) : IPromptRenderFilter
{
    public async Task OnPromptRenderAsync(PromptRenderContext context, Func<PromptRenderContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        await next(context);
        if (string.IsNullOrEmpty(context.RenderedPrompt))
        {
            return;
        }

        var request = SentinelRequestContext.From(context.Kernel);
        var guarded = await guard.GuardInputAsync(
            [new ChatMessage(ChatRole.User, context.RenderedPrompt)], request.Vault, context.CancellationToken);
        if (guarded.Blocked)
        {
            throw new SentinelPolicyException("Guardrails.PromptInjection", "The prompt was rejected by the prompt-injection guardrail.");
        }

        context.RenderedPrompt = guarded.Messages[0].Text;
    }
}

/// <summary>
/// Everything a function returns to the model during automatic function calling is untrusted: it may come from a
/// document, a web page or a customer record. A result carrying an injection is replaced by a notice (the model is told
/// that content was withheld; the attack text never reaches it), and PII in a result stays masked in the final answer.
/// </summary>
public sealed class SentinelAutoFunctionFilter(IPromptGuard guard) : IAutoFunctionInvocationFilter
{
    internal const string Withheld = "[Bu araç sonucu güvenlik politikası nedeniyle gizlendi.]";

    public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        await next(context);
        if (context.Function.Metadata.AdditionalProperties.TryGetValue(KnowledgePlugin.GuardedMetadataKey, out var marker) && marker is true)
        {
            return; // already guarded by the function itself
        }

        if (context.Result?.GetValue<object>() is not { } value)
        {
            return;
        }

        var text = value as string ?? value.ToString();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var request = SentinelRequestContext.From(context.Kernel);
        var label = $"tool:{context.Function.PluginName}.{context.Function.Name}";

        // The context guard has the semantics wanted for tool output: injections are dropped, PII is masked with
        // context origin, so it is never restored into the answer.
        var guarded = await guard.GuardContextAsync(
            [new RetrievedChunk(Guid.Empty, label, 0, label, Classification.Internal, 0, text, 1.0)],
            request.Vault,
            context.CancellationToken);

        var replacement = guarded.Chunks.Count == 1 ? guarded.Chunks[0].Text : Withheld;
        context.Result = new FunctionResult(context.Result, replacement);
    }
}
