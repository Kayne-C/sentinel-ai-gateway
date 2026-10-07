using System.Text.Json;
using System.Text.Json.Serialization;
using Sentinel.Domain.Knowledge;

namespace Sentinel.Gateway.Security;

/// <summary>A person or workload of the demo corpus; <c>/dev/token</c> issues tokens for these.</summary>
public sealed record DemoPersona(
    string Name,
    string DisplayName,
    string TenantId,
    string ObjectId,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Roles,
    bool Application = false);

public sealed record DemoDocument(
    string ExternalId,
    string Title,
    Classification Classification,
    IReadOnlyList<string> Principals,
    string? Canary,
    string Content);

public sealed record DemoTenant(string TenantId, string Name, IReadOnlyDictionary<string, string> Groups, IReadOnlyList<DemoDocument> Documents);

/// <summary>The fictional demo corpus: two tenants, restricted documents carrying canary tokens, one poisoned document.</summary>
public sealed class DemoCorpus
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public required IReadOnlyList<DemoTenant> Tenants { get; init; }

    public required IReadOnlyList<DemoPersona> Personas { get; init; }

    public static DemoCorpus Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<DemoCorpus>(stream, Json) ?? throw new InvalidOperationException($"{path} is empty.");
    }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Demo", "corpus.json");

    public DemoPersona? FindPersona(string name) => Personas.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
