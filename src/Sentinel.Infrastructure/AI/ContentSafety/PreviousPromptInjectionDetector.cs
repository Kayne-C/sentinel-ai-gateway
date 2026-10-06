using Microsoft.Extensions.DependencyInjection;
using Sentinel.Guardrails.Injection;

namespace Sentinel.Infrastructure.AI.ContentSafety;

/// <summary>
/// Holds the detector that was registered before Prompt Shields were added, re-created from its original service
/// descriptor with its original lifetime, so wrapping it in the composite changes neither how it is built nor how
/// long it lives.
/// </summary>
internal sealed class PreviousPromptInjectionDetector : IDisposable, IAsyncDisposable
{
    private readonly bool _owned;

    public PreviousPromptInjectionDetector(IServiceProvider services, ServiceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.ImplementationInstance is IPromptInjectionDetector instance)
        {
            Detector = instance;
        }
        else if (descriptor.ImplementationFactory is { } factory)
        {
            Detector = (IPromptInjectionDetector)factory(services);
            _owned = true;
        }
        else
        {
            var type = descriptor.ImplementationType
                ?? throw new InvalidOperationException("The previous prompt-injection detector registration has no implementation.");
            Detector = (IPromptInjectionDetector)ActivatorUtilities.CreateInstance(services, type);
            _owned = true;
        }
    }

    public IPromptInjectionDetector Detector { get; }

    public void Dispose()
    {
        if (_owned && Detector is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_owned)
        {
            return;
        }

        if (Detector is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (Detector is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
