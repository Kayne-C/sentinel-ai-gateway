using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Sentinel.Gateway.Proxy;
using Sentinel.Gateway.Security;

namespace Sentinel.Gateway.IntegrationTests;

public static class Tenants
{
    public const string Contoso = "3f6a9b2e-7c41-4d8a-b5e3-2a9c4f1d7e60";
    public const string Fabrikam = "9d2e5c71-4b8f-4a36-8e1d-6c3b7a9f2d45";
    public const string SigningKey = "gateway-integration-tests-signing-key-0123456789";
}

/// <summary>
/// The real gateway (production pipeline, real DI) over an in-memory SQLite database, with demo tokens. Subclasses set
/// the model provider: offline for the knowledge-base tests, an OpenAI-compatible stub for the proxy tests.
/// </summary>
public abstract class GatewayFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString = $"Data Source=file:gateway-{Guid.NewGuid():N}?mode=memory&cache=shared";

    protected GatewayFactory()
    {
        // A shared in-memory database lives exactly as long as one connection to it stays open.
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    protected virtual bool SeedDemoCorpus => false;

    protected virtual IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = _connectionString,
                ["Database:ApplyMigrationsOnStartup"] = "true",
                ["Auth:Mode"] = "Development",
                ["Auth:AllowedTenants:0"] = Tenants.Contoso,
                ["Auth:AllowedTenants:1"] = Tenants.Fabrikam,
                ["Auth:Development:SigningKey"] = Tenants.SigningKey,
                ["Demo:SeedOnStartup"] = SeedDemoCorpus ? "true" : "false",
                ["RateLimiting:Enabled"] = "false",
                ["Gateway:ExposeApiDocs"] = "false",
            };

            foreach (var (key, value) in Settings)
            {
                values[key] = value;
            }

            configuration.AddInMemoryCollection(values);
        });

        builder.ConfigureServices(ConfigureTestServices);
    }

    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    public DemoCorpus Corpus => Services.GetRequiredService<DemoCorpus>();

    public string TokenFor(string persona) =>
        Services.GetRequiredService<DevTokenIssuer>().Issue(Corpus.FindPersona(persona) ?? throw new ArgumentException(persona));

    public HttpClient ClientFor(string persona)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(persona));
        return client;
    }

    /// <summary>A token with arbitrary claims, signed with the gateway's development key (or another key).</summary>
    public static string CustomToken(IEnumerable<Claim> claims, string? signingKey = null, DateTime? expires = null)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = DevelopmentAuthOptions.Issuer,
            Audience = DevelopmentAuthOptions.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now.AddMinutes(-2),
            Expires = expires ?? now.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey ?? Tenants.SigningKey)), SecurityAlgorithms.HmacSha256),
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAlive.Dispose();
        }
    }
}

/// <summary>Offline models (hashing embeddings, extractive answers), demo corpus ingested at startup.</summary>
public sealed class KnowledgeGatewayFactory : GatewayFactory
{
    protected override bool SeedDemoCorpus => true;

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Ai:Provider"] = "Offline",
        ["Budgets:Provider"] = "InMemory",
        ["SemanticCache:Provider"] = "InMemory",
    };
}

/// <summary>An OpenAI-compatible provider that is a handler in the test process, so every byte it receives can be inspected.</summary>
public sealed class ProxyGatewayFactory : GatewayFactory
{
    public StubUpstream Upstream { get; } = new();

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Ai:Provider"] = "OpenAICompatible",
        ["Ai:Endpoint"] = "http://localhost:9/v1",
        ["Ai:ApiKey"] = "provider-secret-key",
        ["Ai:EmbeddingModel"] = "stub-embedding",
        ["Models:DefaultTier"] = "fast",
        ["Models:Tiers:fast:Model"] = "stub-fast",
        ["Models:Tiers:fast:InputPricePer1MTokens"] = "1",
        ["Models:Tiers:fast:OutputPricePer1MTokens"] = "2",
        ["Models:Tiers:fast:MaxOutputTokens"] = "100",
        ["Budgets:Provider"] = "InMemory",
        ["SemanticCache:Provider"] = "InMemory",
        ["Audit:StoreRedactedPrompts"] = "true",
    };

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<IUpstreamInvoker>();
        services.AddSingleton<IUpstreamInvoker>(new StubInvoker(Upstream));
    }

    private sealed class StubInvoker(StubUpstream upstream) : IUpstreamInvoker
    {
        public HttpMessageInvoker Invoker { get; } = new(upstream);
    }
}

public sealed record CapturedRequest(Uri? Uri, string? Authorization, string? ContentType, string Body, IReadOnlyDictionary<string, string> Headers)
{
    public JsonNode Json => JsonNode.Parse(Body)!;
}

/// <summary>Plays the model provider: records what it is sent and answers with whatever the test scripted.</summary>
public sealed class StubUpstream : HttpMessageHandler
{
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();

    /// <summary>Builds the response for one request; the default echoes the last user message.</summary>
    public Func<CapturedRequest, HttpResponseMessage> Respond { get; set; } = DefaultEcho;

    public IReadOnlyList<CapturedRequest> Requests => [.. _requests];

    public void Reset()
    {
        _requests.Clear();
        Respond = DefaultEcho;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var captured = new CapturedRequest(
            request.RequestUri, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType, body, headers);
        _requests.Enqueue(captured);
        return Respond(captured);
    }

    public static HttpResponseMessage Json(string content, int promptTokens = 10, int completionTokens = 5, JsonObject? extraMessageFields = null)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content };
        foreach (var (name, value) in extraMessageFields ?? [])
        {
            message[name] = value?.DeepClone();
        }

        var body = new JsonObject
        {
            ["id"] = "chatcmpl-test",
            ["object"] = "chat.completion",
            ["created"] = 1700000000,
            ["model"] = "stub-fast",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = "stop" }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = promptTokens, ["completion_tokens"] = completionTokens, ["total_tokens"] = promptTokens + completionTokens },
        };
        return Json(HttpStatusCode.OK, body.ToJsonString());
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>An SSE answer made of the given content deltas, then a finish chunk and (optionally) the usage chunk.</summary>
    public static HttpResponseMessage Sse(IEnumerable<string> deltas, int promptTokens = 10, int completionTokens = 5, bool withUsage = true)
    {
        var builder = new StringBuilder();
        builder.Append("data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"stub-fast\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n");
        foreach (var delta in deltas)
        {
            builder.Append("data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"stub-fast\",\"choices\":[{\"index\":0,\"delta\":{\"content\":")
                .Append(JsonSerializer.Serialize(delta)).Append("},\"finish_reason\":null}]}\n\n");
        }

        builder.Append("data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"stub-fast\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        if (withUsage)
        {
            var usage = new JsonObject
            {
                ["id"] = "c1",
                ["object"] = "chat.completion.chunk",
                ["created"] = 1700000000,
                ["model"] = "stub-fast",
                ["choices"] = new JsonArray(),
                ["usage"] = new JsonObject { ["prompt_tokens"] = promptTokens, ["completion_tokens"] = completionTokens, ["total_tokens"] = promptTokens + completionTokens },
            };
            builder.Append("data: ").Append(usage.ToJsonString()).Append("\n\n");
        }

        builder.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    private static HttpResponseMessage DefaultEcho(CapturedRequest request)
    {
        var messages = request.Json["messages"]!.AsArray();
        var last = messages[^1]!["content"]!.GetValue<string>();
        return Json("echo: " + last);
    }
}

public static class HttpExtensions
{
    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body);

    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty JSON body: " + text);
    }
}
