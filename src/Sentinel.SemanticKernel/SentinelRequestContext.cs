using Microsoft.SemanticKernel;
using Sentinel.Domain.Identity;
using Sentinel.Guardrails.Pii;

namespace Sentinel.SemanticKernel;

/// <summary>
/// Who is asking and which placeholders belong to this request. It travels in <see cref="Kernel.Data"/>, so every filter
/// and plugin of one request sees the same caller and the same <see cref="PiiVault"/>, and nothing is shared between
/// requests. Create one <see cref="Kernel"/> per request (kernels are cheap) and attach the context to it.
/// </summary>
public sealed class SentinelRequestContext(CallerIdentity caller, PiiVault? vault = null)
{
    public const string DataKey = "sentinel.request-context";

    /// <summary>The authenticated caller; retrieval is restricted to what this identity may read.</summary>
    public CallerIdentity Caller { get; } = caller ?? throw new ArgumentNullException(nameof(caller));

    /// <summary>Placeholders created for this request; use it with <c>IPromptGuard.GuardOutput</c> on the final answer.</summary>
    public PiiVault Vault { get; } = vault ?? new PiiVault();

    public static SentinelRequestContext From(Kernel kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        return kernel.Data.TryGetValue(DataKey, out var value) && value is SentinelRequestContext context
            ? context
            : throw new InvalidOperationException(
                "No caller is attached to this kernel. Create it with SentinelKernelFactory.Create(caller) or call kernel.UseSentinel(caller).");
    }
}

public static class SentinelKernelExtensions
{
    /// <summary>Attaches the caller to the kernel. Filters and plugins refuse to run on a kernel without one.</summary>
    public static SentinelRequestContext UseSentinel(this Kernel kernel, CallerIdentity caller, PiiVault? vault = null)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        var context = new SentinelRequestContext(caller, vault);
        kernel.Data[SentinelRequestContext.DataKey] = context;
        return context;
    }
}

/// <summary>Raised when a guardrail refuses to let content through (prompt injection). Maps to HTTP 422 in a host.</summary>
public sealed class SentinelPolicyException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
