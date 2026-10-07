using System.Reflection;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;

namespace Sentinel.ArchitectureTests;

public sealed class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(Result).Assembly;
    private static readonly Assembly Guardrails = typeof(Sentinel.Guardrails.Pii.PiiVault).Assembly;
    private static readonly Assembly Application = typeof(ISender).Assembly;
    private static readonly Assembly Infrastructure = typeof(Sentinel.Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly SemanticKernel = typeof(Sentinel.SemanticKernel.SentinelKernelFactory).Assembly;
    private static readonly Assembly Gateway = typeof(Sentinel.Gateway.Security.GatewayPolicies).Assembly;

    private static IEnumerable<string> References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!);

    private static bool StartsWithAny(string name, params string[] prefixes) => prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    [Fact]
    public void Domain_has_no_dependencies() =>
        Assert.DoesNotContain(References(Domain), n => StartsWithAny(n, "Sentinel.", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "StackExchange"));

    [Fact]
    public void Guardrails_are_self_contained_text_processing() =>
        // No I/O, no model, no database: they can run anywhere, including in front of a remote detector.
        Assert.DoesNotContain(References(Guardrails), n => StartsWithAny(
            n, "Sentinel.", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "StackExchange", "Microsoft.Extensions.AI", "System.Net.Http"));

    [Fact]
    public void Application_knows_no_database_cache_or_web_framework() =>
        Assert.DoesNotContain(References(Application), n => StartsWithAny(
            n, "Sentinel.Infrastructure", "Sentinel.Gateway", "Sentinel.SemanticKernel", "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore",
            "Microsoft.Data.SqlClient", "Microsoft.Data.Sqlite", "StackExchange", "NRedisStack", "Yarp", "OpenAI"));

    [Fact]
    public void Infrastructure_does_not_depend_on_hosts() =>
        Assert.DoesNotContain(References(Infrastructure), n => StartsWithAny(n, "Sentinel.Gateway", "Sentinel.SemanticKernel", "Yarp", "Microsoft.AspNetCore.Authentication"));

    [Fact]
    public void The_semantic_kernel_integration_depends_on_ports_not_on_infrastructure() =>
        Assert.DoesNotContain(References(SemanticKernel), n => StartsWithAny(n, "Sentinel.Infrastructure", "Sentinel.Gateway", "Microsoft.EntityFrameworkCore", "StackExchange"));

    [Fact]
    public void Aggregates_change_state_only_through_their_methods()
    {
        var offenders = Domain.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Sentinel.Domain.Knowledge", StringComparison.Ordinal) == true && t is { IsClass: true, IsAbstract: false } && !t.Name.Contains('<', StringComparison.Ordinal))
            .Where(t => t.GetMethod("<Clone>$") is null) // records are immutable values; classes are aggregates
            .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(p => p.SetMethod?.IsPublic == true)
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_request_has_exactly_one_internal_sealed_handler()
    {
        var types = Application.GetTypes();
        var handlers = types
            .Where(t => !t.IsAbstract && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
            .ToList();
        var requests = types.Where(t => !t.IsAbstract && !t.IsInterface && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))).ToList();

        Assert.NotEmpty(requests);
        Assert.All(handlers, h => Assert.True(h.IsSealed && !h.IsPublic, $"{h.Name} must be internal sealed."));
        Assert.All(requests, r => Assert.Single(handlers, h => h.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericArguments()[0] == r)));
    }

    [Fact]
    public void Every_use_case_carries_the_authenticated_caller()
    {
        // Tenant and ACL principals come from this identity; a request type that cannot carry it could not be restricted.
        var requests = Application.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)));

        Assert.All(requests, r =>
            Assert.True(
                r.GetProperties().Any(p => p.PropertyType == typeof(CallerIdentity) && p.Name == "Caller"),
                $"{r.Name} must have a 'CallerIdentity Caller' property."));
    }

    [Fact]
    public void The_gateway_never_touches_data_stores_directly()
    {
        // Documents, vectors, the cache and the audit log are reachable only through use cases, where tenant and ACL
        // checks live; a controller that injected a repository could skip them.
        string[] forbidden = ["IVectorSearch", "IKnowledgeRepository", "IAuditLog", "ISemanticCache", "SentinelDbContext", "ITokenBudget", "IEventStoreAudit"];
        var root = FindRepositoryRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "Sentinel.Gateway"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, line, i)))
            .Where(x => forbidden.Any(name => x.line.Contains(name, StringComparison.Ordinal)))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_gateway_exposes_no_public_types_outside_its_contract()
    {
        // Hosts are not libraries: only the types tests and configuration bind to may be public.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Program", "GatewayCaller", "GatewayAuthException", "GatewayPolicies", "GatewayAuthOptions", "AuthMode", "EntraOptions", "DevelopmentAuthOptions",
            "GatewayAuthentication", "DevTokenIssuer", "DemoPersona", "DemoDocument", "DemoTenant", "DemoCorpus", "CallerIdentityFactory", "ProblemResults",
            "GatewayRateLimitOptions", "GatewayRateLimiting", "AskRequest", "UpsertDocumentRequest", "ApiEndpoints", "DevTokenRequest", "OpenAiProxyOptions",
            "IUpstreamInvoker", "EntraOptions",
        };

        var unexpected = Gateway.GetExportedTypes().Select(t => t.Name).Where(n => !allowed.Contains(n)).ToList();
        Assert.Empty(unexpected);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sentinel.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Sentinel.slnx was not found above the test output directory.");
    }
}
