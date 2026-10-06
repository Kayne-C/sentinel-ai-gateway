using Microsoft.Extensions.AI;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Domain.Audit;
using Sentinel.Guardrails.Injection;

namespace Sentinel.Infrastructure.IntegrationTests.Knowledge;

internal static class Vectors
{
    public const int Dimensions = EmbeddingDefaults.Dimensions;

    /// <summary>Unit vector whose cosine with the first axis is exactly <paramref name="cosine"/>.</summary>
    public static float[] AtCosine(double cosine, int axis)
    {
        var vector = new float[Dimensions];
        vector[0] = (float)cosine;
        vector[axis] = (float)Math.Sqrt(1 - cosine * cosine);
        return vector;
    }

    public static float[] Query => AtCosine(1, 1);

    public static float[] Random(int seed)
    {
        var random = new Random(seed);
        var vector = new float[Dimensions];
        for (var i = 0; i < Dimensions; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2 - 1);
        }

        return vector;
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

internal sealed class WordTokenCounter : ITokenCounter
{
    public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

internal sealed class CleanInjectionDetector : IPromptInjectionDetector
{
    public ValueTask<InjectionVerdict> InspectAsync(string text, ContentOrigin origin, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(text.Contains("ignore previous instructions", StringComparison.OrdinalIgnoreCase)
            ? new InjectionVerdict(true, 1, ["injection.override"], "test")
            : InjectionVerdict.Clean("test"));
}

/// <summary>Every input embeds to the same direction, so any authorised chunk is a perfect match.</summary>
internal sealed class ConstantEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public int Inputs { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var inputs = values.ToList();
        Inputs += inputs.Count;
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(inputs.Select(_ => new Embedding<float>(Vectors.Query))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class RecordingSemanticCache : ISemanticCache
{
    public List<Guid> Invalidated { get; } = [];

    public Task<CacheCandidate?> FindAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken) =>
        Task.FromResult<CacheCandidate?>(null);

    public Task StoreAsync(SemanticCacheScope scope, ReadOnlyMemory<float> embedding, CachedAnswer answer, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task InvalidateDocumentAsync(string tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        Invalidated.Add(documentId);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingAuditLog : IAuditLog
{
    public List<AuditEvent> Events { get; } = [];

    public Task<AuditEntry> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Events.Add(auditEvent);
        return Task.FromResult(new AuditEntry(Events.Count, auditEvent, null, "hash"));
    }

    public Task<AuditVerification> VerifyAsync(string tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PagedResponse<AuditEntry>> ListAsync(AuditQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
}
