using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sentinel.Evals;

internal sealed record CorpusDocument(string TenantId, string ExternalId, string Title, string Content, string? Canary, IReadOnlySet<string> Principals)
{
    public string Topic => Title.Contains(" - ", StringComparison.Ordinal) ? Title[(Title.IndexOf(" - ", StringComparison.Ordinal) + 3)..] : Title;
}

internal sealed record CorpusPersona(string Name, string TenantId, string ObjectId, IReadOnlyList<string> Groups, IReadOnlyList<string> Roles)
{
    /// <summary>The ACL principals this persona holds: everyone, itself and each of its groups.</summary>
    public IReadOnlySet<string> Principals =>
        new HashSet<string>(["everyone", "user:" + ObjectId.ToLowerInvariant(), .. Groups.Select(g => "group:" + g.ToLowerInvariant())], StringComparer.Ordinal);

    public bool MayRead(CorpusDocument document) =>
        string.Equals(document.TenantId, TenantId, StringComparison.OrdinalIgnoreCase) && document.Principals.Overlaps(Principals);
}

/// <summary>The demo corpus, read from the same file the gateway seeds from: it is the ground truth for ACL decisions.</summary>
internal sealed class Corpus
{
    public required IReadOnlyList<CorpusDocument> Documents { get; init; }

    public required IReadOnlyList<CorpusPersona> Personas { get; init; }

    public static Corpus Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var documents = new List<CorpusDocument>();
        foreach (var tenant in root["tenants"]!.AsArray())
        {
            foreach (var doc in tenant!["documents"]!.AsArray())
            {
                documents.Add(new CorpusDocument(
                    (string)tenant["tenantId"]!,
                    (string)doc!["externalId"]!,
                    (string)doc["title"]!,
                    (string)doc["content"]!,
                    (string?)doc["canary"],
                    doc["principals"]!.AsArray().Select(p => ((string)p!).ToLowerInvariant()).ToHashSet(StringComparer.Ordinal)));
            }
        }

        var personas = root["personas"]!.AsArray().Select(p => new CorpusPersona(
            (string)p!["name"]!,
            (string)p["tenantId"]!,
            (string)p["objectId"]!,
            p["groups"]!.AsArray().Select(g => (string)g!).ToList(),
            p["roles"]!.AsArray().Select(r => (string)r!).ToList())).ToList();

        return new Corpus { Documents = documents, Personas = personas };
    }

    public static string DefaultPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Sentinel.Gateway", "Demo", "corpus.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("src/Sentinel.Gateway/Demo/corpus.json was not found above the evaluation's output directory.");
    }
}

internal sealed class GatewayClient(string baseUrl) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(10) };
    private readonly Dictionary<string, string> _tokens = [];

    public async Task<string> TokenAsync(string persona)
    {
        if (_tokens.TryGetValue(persona, out var cached))
        {
            return cached;
        }

        using var response = await _http.PostAsJsonAsync("/dev/token", new { persona });
        response.EnsureSuccessStatusCode();
        var token = (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["access_token"]!;
        _tokens[persona] = token;
        return token;
    }

    public async Task<(int Status, string Body, double Milliseconds)> PostAsync(string persona, string path, object body, TimeSpan? timeout = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await TokenAsync(persona));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
        using var response = await _http.SendAsync(request, cancellation.Token);
        var text = await response.Content.ReadAsStringAsync(cancellation.Token);
        return ((int)response.StatusCode, text, watch.Elapsed.TotalMilliseconds);
    }

    public async Task<(int Status, string Body)> GetAsync(string persona, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await TokenAsync(persona));
        using var response = await _http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => _http.Dispose();
}
