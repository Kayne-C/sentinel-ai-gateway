using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Domain.Audit;
using Sentinel.Domain.Common;
using Sentinel.Domain.Knowledge;
using Sentinel.Guardrails.Injection;
using Sentinel.Guardrails.Pii;
using RoutingContext = Sentinel.Application.Abstractions.RoutingContext;

namespace Sentinel.Infrastructure.IntegrationTests.AI.Fakes;

/// <summary>Stand-ins for the modules other teams own, just real enough for an end-to-end offline RAG run.</summary>
internal sealed partial class EmailRedactor : IPiiRedactor
{
    public RedactionResult Redact(string text, PiiVault vault, PiiOrigin origin)
    {
        var findings = new List<PiiFinding>();
        var redacted = Email().Replace(text, m =>
        {
            var placeholder = vault.Protect(PiiType.Email, m.Value, origin);
            findings.Add(new PiiFinding(PiiType.Email, m.Index, m.Length, placeholder));
            return placeholder;
        });
        return new RedactionResult(redacted, findings);
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Email();
}

internal sealed class KeywordDetector : IPromptInjectionDetector
{
    public const string Name = "local-keyword";
    public const string Rule = "injection.override";

    public ConcurrentQueue<string> Inspected { get; } = new();

    public ValueTask<InjectionVerdict> InspectAsync(string text, ContentOrigin origin, CancellationToken cancellationToken = default)
    {
        Inspected.Enqueue(text);
        return ValueTask.FromResult(text.Contains("ignore previous instructions", StringComparison.OrdinalIgnoreCase)
            ? new InjectionVerdict(true, 0.9, [Rule], Name)
            : InjectionVerdict.Clean(Name));
    }
}

/// <summary>Brute-force cosine search with the ACL and tenant applied before ranking, like the real store.</summary>
internal sealed class InMemoryVectorSearch(IEmbeddingGenerator<string, Embedding<float>> embeddings) : IVectorSearch
{
    private readonly List<(string TenantId, HashSet<string> Principals, RetrievedChunk Chunk, float[] Vector)> _chunks = [];

    public async Task AddAsync(string tenantId, string externalId, string text, params string[] principals)
    {
        var vector = (await embeddings.GenerateVectorAsync(text)).ToArray();
        var chunk = new RetrievedChunk(Guid.NewGuid(), externalId, 1, externalId, Classification.Internal, 0, text, 0);
        _chunks.Add((tenantId, [.. principals], chunk, vector));
    }

    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(VectorQuery query, CancellationToken cancellationToken)
    {
        var results = _chunks
            .Where(c => c.TenantId == query.TenantId && c.Principals.Overlaps(query.Principals))
            .Select(c => c.Chunk with { Similarity = Dot(c.Vector, query.Embedding.Span) })
            .Where(c => c.Similarity >= query.MinSimilarity)
            .OrderByDescending(c => c.Similarity)
            .Take(query.Top)
            .ToList();
        return Task.FromResult<IReadOnlyList<RetrievedChunk>>(results);
    }

    private static double Dot(float[] a, ReadOnlySpan<float> b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
        }

        return dot;
    }
}

internal sealed class InMemorySemanticCache : ISemanticCache
{
    private readonly List<(SemanticCacheScope Scope, float[] Vector, CachedAnswer Answer)> _entries = [];

    public Task<CacheCandidate?> FindAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken)
    {
        var best = _entries
            .Where(e => e.Scope == scope)
            .Select(e => new CacheCandidate(e.Answer, Dot(e.Vector, embedding.Span)))
            .Where(c => c.Similarity >= 0.95)
            .OrderByDescending(c => c.Similarity)
            .FirstOrDefault();
        return Task.FromResult(best);
    }

    public Task StoreAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CachedAnswer answer, CancellationToken cancellationToken)
    {
        _entries.Add((scope, embedding.ToArray(), answer));
        return Task.CompletedTask;
    }

    public Task InvalidateDocumentAsync(string tenantId, Guid documentId, CancellationToken cancellationToken) => Task.CompletedTask;

    private static double Dot(float[] a, ReadOnlySpan<float> b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
        }

        return dot;
    }
}

internal sealed class UnlimitedBudget : ITokenBudget
{
    public List<int> Settled { get; } = [];

    public Task<BudgetLease> ReserveAsync(BudgetRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new BudgetLease(true, request.TenantId, request.SubjectId, request.EstimatedTokens, long.MaxValue, long.MaxValue, null, null));

    public Task SettleAsync(BudgetLease lease, int actualTokens, CancellationToken cancellationToken)
    {
        Settled.Add(actualTokens);
        return Task.CompletedTask;
    }

    public Task<BudgetStatus> GetStatusAsync(string tenantId, string subjectId, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class WordCounter : ITokenCounter
{
    public int CountTokens(string text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

internal sealed class FastTierRouter : IModelRouter
{
    public ModelRoute Route(RoutingContext context) => new(ModelCatalogOptions.FastTier, string.Empty, 0, ["test"]);
}

internal sealed class MemoryAuditLog : IAuditLog
{
    public ConcurrentQueue<AuditEvent> Events { get; } = new();

    public Task<AuditEntry> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Events.Enqueue(auditEvent);
        return Task.FromResult(new AuditEntry(Events.Count, auditEvent, null, HashChain.Sha256Hex(Events.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    public Task<AuditVerification> VerifyAsync(string tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PagedResponse<AuditEntry>> ListAsync(AuditQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>Captures requests and answers with a canned response, for provider wiring tests without a network.</summary>
internal sealed class RecordingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request, body));
        return respond(request, body);
    }
}
