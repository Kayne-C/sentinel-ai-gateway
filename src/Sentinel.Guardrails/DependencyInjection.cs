using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sentinel.Guardrails.Injection;
using Sentinel.Guardrails.Pii;
using Sentinel.Guardrails.Pii.Recognizers;

namespace Sentinel.Guardrails;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the guardrails as singletons (all stateless; per-request state lives in <see cref="PiiVault"/>).
    /// Recognizers are added with <c>TryAddEnumerable</c> so hosts can contribute more; the redactor, output guard
    /// and injection detector use <c>TryAdd</c> so infrastructure can replace or decorate them (e.g. compose the
    /// heuristic detector with a model-based classifier).
    /// </summary>
    public static IServiceCollection AddGuardrails(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PiiOptions>().Bind(configuration.GetSection(PiiOptions.Section));
        services.AddOptions<InjectionOptions>().Bind(configuration.GetSection(InjectionOptions.Section));

        services.TryAddEnumerable(
        [
            ServiceDescriptor.Singleton<IPiiRecognizer, NationalIdRecognizer>(),
            ServiceDescriptor.Singleton<IPiiRecognizer, TaxNumberRecognizer>(),
            ServiceDescriptor.Singleton<IPiiRecognizer, IbanRecognizer>(),
            ServiceDescriptor.Singleton<IPiiRecognizer, PaymentCardRecognizer>(),
            ServiceDescriptor.Singleton<IPiiRecognizer, PhoneNumberRecognizer>(),
            ServiceDescriptor.Singleton<IPiiRecognizer, EmailRecognizer>(),
            ServiceDescriptor.Singleton<IPiiRecognizer, IpAddressRecognizer>(),
        ]);

        services.TryAddSingleton<IPiiRedactor, PiiRedactor>();
        services.TryAddSingleton<OutputGuard>();
        services.TryAddSingleton<IPromptInjectionDetector, HeuristicInjectionDetector>();
        return services;
    }

    /// <summary>
    /// Optional, for host startup: builds the injection rules' automata and JIT-compiles the recognizers so the
    /// first request does not pay the one-off cost (about a second). Pure CPU work, no I/O.
    /// </summary>
    public static IServiceProvider WarmUpGuardrails(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        HeuristicInjectionDetector.WarmUp();
        services.GetRequiredService<IPiiRedactor>()
            .Redact("warm-up ayse@example.com 0532 123 45 67 TR330006100519786457841326 10000000146", new PiiVault(), PiiOrigin.Caller);
        return services;
    }
}
