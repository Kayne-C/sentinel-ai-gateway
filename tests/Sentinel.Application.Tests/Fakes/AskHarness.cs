using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Application.Features.Ask;
using Sentinel.Application.Features.Proxy;
using Sentinel.Application.Guardrails;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Knowledge;
using Sentinel.Guardrails.Injection;
using Sentinel.Guardrails.Pii;

namespace Sentinel.Application.Tests.Fakes;

/// <summary>The handler and the proxy service wired to fakes for every port, with tweakable options.</summary>
internal sealed class AskHarness
{
    private FakeChatClientProvider? _provider;

    public static readonly CallerIdentity Caller =
        new("tenant-a", "user-1", "Ayse", ["grp-hr"], [SentinelRoles.User], CallerKind.User);

    public RegexPiiRedactor Redactor { get; } = new();

    public KeywordInjectionDetector Detector { get; } = new();

    public RecordingEmbeddingGenerator Embeddings { get; } = new();

    public FakeVectorSearch VectorSearch { get; } = new();

    public FakeSemanticCache Cache { get; } = new();

    public FakeTokenBudget Budget { get; } = new();

    public WordTokenCounter TokenCounter { get; } = new();

    public FakeModelRouter Router { get; } = new();

    public ScriptedChatClient Model { get; } = new();

    public FakeAuditLog Audit { get; } = new();

    public FakeChatClientProvider Provider => _provider ??= new FakeChatClientProvider(Model);

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

    public RagOptions Rag { get; } = new();

    public PiiOptions Pii { get; } = new();

    public InjectionOptions Injection { get; } = new();

    public AuditPolicyOptions AuditPolicy { get; } = new();

    public ModelCatalogOptions Catalog { get; } = new()
    {
        Tiers =
        {
            ["fast"] = new ModelTierOptions { Model = "test-fast", InputPricePer1MTokens = 1m, OutputPricePer1MTokens = 2m, MaxOutputTokens = 256 },
        },
    };

    public PromptGuard CreateGuard() => new(Redactor, Detector, Options.Create(Pii), Options.Create(Injection));

    public AskQuestionCommandHandler CreateHandler() => new(
        CreateGuard(), Embeddings, VectorSearch, Cache, Budget, TokenCounter, Router, Provider, Audit,
        Options.Create(Rag), Options.Create(Catalog), Options.Create(AuditPolicy), Time, NullLogger<AskQuestionCommandHandler>.Instance);

    public ChatProxyService CreateProxy() => new(
        CreateGuard(), Budget, TokenCounter, Router, Audit, Options.Create(Catalog), Options.Create(AuditPolicy), Time,
        NullLogger<ChatProxyService>.Instance);

    public Task<Result<AskResponse>> AskAsync(string question, int? topK = null) =>
        CreateHandler().Handle(new AskQuestionCommand(Caller, question, topK), CancellationToken.None);

    public static RetrievedChunk Chunk(string externalId, string text, double similarity = 0.9, int version = 1, int ordinal = 0) =>
        new(DocumentId(externalId), externalId, version, "Title of " + externalId, Classification.Internal, ordinal, text, similarity);

    public static SourceReference Source(string externalId, int version = 1) =>
        new(DocumentId(externalId), externalId, version, "Title of " + externalId);

    /// <summary>Stable id per external id, so tests can build matching cache entries.</summary>
    public static Guid DocumentId(string externalId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(externalId)).AsSpan(0, 16));
}
