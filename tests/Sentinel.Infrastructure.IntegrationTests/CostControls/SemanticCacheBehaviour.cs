using Microsoft.Extensions.Time.Testing;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Infrastructure.CostControls;

namespace Sentinel.Infrastructure.IntegrationTests.CostControls;

/// <summary>
/// The semantic-cache contract, run against every implementation. Each test gets a fresh cache (for Redis: its own
/// index and key prefix), so tests cannot see each other's entries.
/// </summary>
public abstract class SemanticCacheBehaviour
{
    /// <summary>Tenant ids that must be stored and matched exactly: query syntax, separators, case, Unicode.</summary>
    internal static readonly string[] HostileTenantIdValues =
    [
        "contoso.com",
        "Contoso.com",
        "user@contoso.com",
        "Contoso Ltd",
        "a b  c",
        "4f9b7c5e-2d1a-4b8e-9c3f-0a1b2c3d4e5f",
        "con*",
        "*",
        "x} | @ns:{ask",
        "{evil}",
        "back\\slash",
        "quote\"and'apostrophe",
        "pipe|tenant",
        "$tenant",
        "percent%20sign",
        "a,b",
        "semi;colon:colon",
        "tilde~caret^",
        "paren(s)[brackets]<angle>",
        "-leading-hyphen",
        "trailing-dot.",
        "@",
        "İstanbul Şirketi",
        "emoji😀tenant",
        "zero\u200Bwidth",
    ];

    public static TheoryData<string> HostileTenantIds => new(HostileTenantIdValues);

    protected FakeTimeProvider Time { get; } = new(new DateTimeOffset(2099, 1, 15, 12, 0, 0, TimeSpan.Zero));

    protected abstract ISemanticCache CreateCache(SemanticCacheOptions options);

    [Fact]
    public async Task Returns_the_stored_answer_when_similarity_is_above_the_threshold()
    {
        var cache = Cache();
        var stored = TestVectors.Random(1);
        var answer = Answer("What is the leave policy?", Guid.NewGuid());
        await cache.StoreAsync(Scope(), stored, answer, CancellationToken.None);

        var hit = await cache.FindAsync(Scope(), TestVectors.WithSimilarity(stored, 0.95, 2), CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Equal(answer.Answer, hit.Answer.Answer);
        Assert.Equal(0.95, hit.Similarity, 3);
    }

    [Fact]
    public async Task Returns_nothing_when_similarity_is_below_the_threshold()
    {
        var cache = Cache();
        var stored = TestVectors.Random(1);
        await cache.StoreAsync(Scope(), stored, Answer("q", Guid.NewGuid()), CancellationToken.None);

        Assert.Null(await cache.FindAsync(Scope(), TestVectors.WithSimilarity(stored, 0.90, 2), CancellationToken.None));
        Assert.Null(await cache.FindAsync(Scope(), TestVectors.WithSimilarity(stored, 0.0, 3), CancellationToken.None));
    }

    [Fact]
    public async Task The_threshold_is_configurable()
    {
        var cache = Cache(o => o.SimilarityThreshold = 0.80);
        var stored = TestVectors.Random(1);
        await cache.StoreAsync(Scope(), stored, Answer("q", Guid.NewGuid()), CancellationToken.None);

        Assert.NotNull(await cache.FindAsync(Scope(), TestVectors.WithSimilarity(stored, 0.85, 2), CancellationToken.None));
        Assert.Null(await cache.FindAsync(Scope(), TestVectors.WithSimilarity(stored, 0.75, 3), CancellationToken.None));
    }

    [Fact]
    public async Task The_most_similar_entry_of_the_scope_wins()
    {
        var cache = Cache();
        var query = TestVectors.Random(1);
        await cache.StoreAsync(Scope(), TestVectors.WithSimilarity(query, 0.93, 2), Answer("further", Guid.NewGuid()), CancellationToken.None);
        await cache.StoreAsync(Scope(), TestVectors.WithSimilarity(query, 0.99, 3), Answer("closest", Guid.NewGuid()), CancellationToken.None);
        await cache.StoreAsync(Scope(), TestVectors.WithSimilarity(query, 0.95, 4), Answer("middle", Guid.NewGuid()), CancellationToken.None);

        var hit = await cache.FindAsync(Scope(), query, CancellationToken.None);

        Assert.Equal("closest", hit?.Answer.Question);
    }

    [Fact]
    public async Task An_identical_embedding_in_another_tenant_is_never_returned()
    {
        var cache = Cache();
        var embedding = TestVectors.Random(1);
        await cache.StoreAsync(Scope(tenant: "tenant-a"), embedding, Answer("secret of a", Guid.NewGuid()), CancellationToken.None);

        Assert.Null(await cache.FindAsync(Scope(tenant: "tenant-b"), embedding, CancellationToken.None));
        Assert.Null(await cache.FindAsync(Scope(tenant: "tenant"), embedding, CancellationToken.None));
        Assert.Null(await cache.FindAsync(Scope(tenant: "tenant-a2"), embedding, CancellationToken.None));
        Assert.NotNull(await cache.FindAsync(Scope(tenant: "tenant-a"), embedding, CancellationToken.None));
    }

    [Fact]
    public async Task An_identical_embedding_in_another_namespace_or_tier_is_never_returned()
    {
        var cache = Cache();
        var embedding = TestVectors.Random(1);
        await cache.StoreAsync(Scope(ns: "ask", tier: "fast"), embedding, Answer("q", Guid.NewGuid()), CancellationToken.None);

        Assert.Null(await cache.FindAsync(Scope(ns: "proxy", tier: "fast"), embedding, CancellationToken.None));
        Assert.Null(await cache.FindAsync(Scope(ns: "ask", tier: "reasoning"), embedding, CancellationToken.None));
        Assert.Null(await cache.FindAsync(Scope(ns: "ASK", tier: "fast"), embedding, CancellationToken.None));
        Assert.NotNull(await cache.FindAsync(Scope(ns: "ask", tier: "fast"), embedding, CancellationToken.None));
    }

    [Fact]
    public async Task Every_hostile_tenant_id_sees_only_its_own_answer()
    {
        // Same embedding everywhere: only the partitioning can tell the entries apart.
        var cache = Cache();
        var embedding = TestVectors.Random(7);
        foreach (var tenant in HostileTenantIdValues)
        {
            await cache.StoreAsync(Scope(tenant: tenant), embedding, Answer(tenant, Guid.NewGuid()), CancellationToken.None);
        }

        foreach (var tenant in HostileTenantIdValues)
        {
            var hit = await cache.FindAsync(Scope(tenant: tenant), embedding, CancellationToken.None);
            Assert.True(hit is not null && hit.Answer.Question == tenant, $"tenant '{tenant}' got '{hit?.Answer.Question}'");
        }

        // Ids that were never stored, but that a sloppy query (prefix, case folding, separator split) would match.
        foreach (var decoy in new[] { "a", "b", "contoso", "CONTOSO.COM", "con", "x", "evil", "user", "Contoso", "istanbul şirketi", "zerowidth" })
        {
            Assert.Null(await cache.FindAsync(Scope(tenant: decoy), embedding, CancellationToken.None));
        }
    }

    [Theory]
    [MemberData(nameof(HostileTenantIds))]
    public async Task A_hostile_tenant_id_is_isolated_from_its_neighbours(string tenant)
    {
        var cache = Cache();
        var embedding = TestVectors.Random(11);
        await cache.StoreAsync(Scope(tenant: tenant), embedding, Answer(tenant, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(tenant, (await cache.FindAsync(Scope(tenant: tenant), embedding, CancellationToken.None))?.Answer.Question);
        foreach (var neighbour in new[] { tenant + "x", "x" + tenant, tenant.ToUpperInvariant() + "_", tenant[..^1] + "?" })
        {
            Assert.Null(await cache.FindAsync(Scope(tenant: neighbour), embedding, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Scopes_that_cannot_be_partitioned_exactly_are_never_cached()
    {
        // Built at runtime: control characters and lone surrogates do not survive theory-data serialisation.
        string[] unrepresentable =
        [
            " leading-space",
            "trailing-space ",
            "tab\tinside",
            "new\nline",
            "unit" + (char)0x1F + "separator",
            "lone" + (char)0xD800 + "surrogate",
            new string('t', 257),
        ];

        var cache = Cache();
        var embedding = TestVectors.Random(1);
        foreach (var tenant in unrepresentable)
        {
            await cache.StoreAsync(Scope(tenant: tenant), embedding, Answer("q", Guid.NewGuid()), CancellationToken.None);
        }

        foreach (var tenant in unrepresentable)
        {
            Assert.Null(await cache.FindAsync(Scope(tenant: tenant), embedding, CancellationToken.None));
            Assert.Null(await cache.FindAsync(Scope(tenant: tenant.Trim()), embedding, CancellationToken.None));
        }

        foreach (var ns in new[] { " ask", "ask\r" })
        {
            await cache.StoreAsync(Scope(ns: ns), embedding, Answer("q", Guid.NewGuid()), CancellationToken.None);
            Assert.Null(await cache.FindAsync(Scope(ns: ns), embedding, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Missing_scope_parts_are_rejected()
    {
        var cache = Cache();
        var embedding = TestVectors.Random(1);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => cache.FindAsync(new SemanticCacheScope(" ", "ask", "fast"), embedding, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => cache.StoreAsync(new SemanticCacheScope("t", "", "fast"), embedding, Answer("q"), CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => cache.InvalidateDocumentAsync("", Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Invalidating_a_document_drops_every_answer_citing_it_and_nothing_else()
    {
        var cache = Cache();
        var handbook = Guid.NewGuid();
        var pricing = Guid.NewGuid();
        var citesHandbook = TestVectors.Random(1);
        var citesBoth = TestVectors.Random(2);
        var citesPricing = TestVectors.Random(3);
        await cache.StoreAsync(Scope(ns: "ask"), citesHandbook, Answer("handbook", handbook), CancellationToken.None);
        await cache.StoreAsync(Scope(ns: "proxy", tier: "reasoning"), citesBoth, Answer("both", pricing, handbook), CancellationToken.None);
        await cache.StoreAsync(Scope(ns: "ask"), citesPricing, Answer("pricing", pricing), CancellationToken.None);

        await cache.InvalidateDocumentAsync("contoso", handbook, CancellationToken.None);

        Assert.Null(await cache.FindAsync(Scope(ns: "ask"), citesHandbook, CancellationToken.None));
        Assert.Null(await cache.FindAsync(Scope(ns: "proxy", tier: "reasoning"), citesBoth, CancellationToken.None));
        Assert.Equal("pricing", (await cache.FindAsync(Scope(ns: "ask"), citesPricing, CancellationToken.None))?.Answer.Question);
    }

    [Fact]
    public async Task Invalidation_only_affects_the_given_tenant()
    {
        var cache = Cache();
        var document = Guid.NewGuid();
        var embedding = TestVectors.Random(1);
        await cache.StoreAsync(Scope(tenant: "tenant-a"), embedding, Answer("a", document), CancellationToken.None);
        await cache.StoreAsync(Scope(tenant: "tenant-a.b"), embedding, Answer("a.b", document), CancellationToken.None);

        await cache.InvalidateDocumentAsync("tenant-a", document, CancellationToken.None);

        Assert.Null(await cache.FindAsync(Scope(tenant: "tenant-a"), embedding, CancellationToken.None));
        Assert.Equal("a.b", (await cache.FindAsync(Scope(tenant: "tenant-a.b"), embedding, CancellationToken.None))?.Answer.Question);
    }

    [Fact]
    public async Task Invalidation_removes_more_entries_than_fit_in_one_page()
    {
        var cache = Cache();
        var document = Guid.NewGuid();
        var other = Guid.NewGuid();
        var embedding = TestVectors.Random(1);
        foreach (var batch in Enumerable.Range(0, 1_150).Chunk(50))
        {
            await Task.WhenAll(batch.Select(i => cache.StoreAsync(Scope(), embedding, Answer($"q{i}", document), CancellationToken.None)));
        }

        var survivor = TestVectors.Random(2);
        await cache.StoreAsync(Scope(), survivor, Answer("survivor", other), CancellationToken.None);

        await cache.InvalidateDocumentAsync("contoso", document, CancellationToken.None);

        Assert.Null(await cache.FindAsync(Scope(), embedding, CancellationToken.None));
        Assert.Equal("survivor", (await cache.FindAsync(Scope(), survivor, CancellationToken.None))?.Answer.Question);
    }

    [Fact]
    public async Task Entries_expire_after_the_ttl()
    {
        var cache = Cache(o => o.Ttl = TimeSpan.FromHours(2));
        var embedding = TestVectors.Random(1);
        await cache.StoreAsync(Scope(), embedding, Answer("q", Guid.NewGuid()), CancellationToken.None);

        Time.Advance(TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1));
        Assert.NotNull(await cache.FindAsync(Scope(), embedding, CancellationToken.None));

        Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await cache.FindAsync(Scope(), embedding, CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_entry_does_not_shadow_a_valid_one()
    {
        var cache = Cache(o => o.Ttl = TimeSpan.FromHours(2));
        var query = TestVectors.Random(1);
        await cache.StoreAsync(Scope(), query, Answer("old", Guid.NewGuid()), CancellationToken.None);
        Time.Advance(TimeSpan.FromHours(1));
        await cache.StoreAsync(Scope(), TestVectors.WithSimilarity(query, 0.95, 2), Answer("fresh", Guid.NewGuid()), CancellationToken.None);

        Time.Advance(TimeSpan.FromMinutes(90));

        Assert.Equal("fresh", (await cache.FindAsync(Scope(), query, CancellationToken.None))?.Answer.Question);
    }

    [Fact]
    public async Task A_scope_keeps_only_its_newest_entries()
    {
        var cache = Cache(o => o.MaxEntriesPerScope = 3);
        var embeddings = Enumerable.Range(1, 5).Select(TestVectors.Random).ToArray();
        for (var i = 0; i < embeddings.Length; i++)
        {
            await cache.StoreAsync(Scope(), embeddings[i], Answer($"q{i}", Guid.NewGuid()), CancellationToken.None);
            await cache.StoreAsync(Scope(tenant: "other"), embeddings[i], Answer($"other{i}", Guid.NewGuid()), CancellationToken.None);
            Time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Null(await cache.FindAsync(Scope(), embeddings[0], CancellationToken.None));
        Assert.Null(await cache.FindAsync(Scope(), embeddings[1], CancellationToken.None));
        for (var i = 2; i < embeddings.Length; i++)
        {
            Assert.Equal($"q{i}", (await cache.FindAsync(Scope(), embeddings[i], CancellationToken.None))?.Answer.Question);
        }

        // The bound is per scope: another tenant's entries neither count nor get evicted on its behalf.
        Assert.Equal("other4", (await cache.FindAsync(Scope(tenant: "other"), embeddings[4], CancellationToken.None))?.Answer.Question);
    }

    [Fact]
    public async Task A_cached_answer_round_trips_intact()
    {
        var cache = Cache();
        var embedding = TestVectors.Random(1);
        var source = new SourceReference(Guid.NewGuid(), "hr/leave-policy.md", 3, "Leave policy — İzin politikası");
        var answer = new CachedAnswer(
            "Yıllık izin kaç gün?", "14 iş günü [1].", [source], "qwen2.5:0.5b", 812, 64, new DateTime(2099, 1, 15, 11, 59, 0, DateTimeKind.Utc));
        await cache.StoreAsync(Scope(), embedding, answer, CancellationToken.None);

        var hit = await cache.FindAsync(Scope(), embedding, CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Equal(answer.Question, hit.Answer.Question);
        Assert.Equal(answer.Answer, hit.Answer.Answer);
        Assert.Equal(answer.Model, hit.Answer.Model);
        Assert.Equal(answer.PromptTokens, hit.Answer.PromptTokens);
        Assert.Equal(answer.CompletionTokens, hit.Answer.CompletionTokens);
        Assert.Equal(answer.CreatedAtUtc, hit.Answer.CreatedAtUtc);
        Assert.Equal(source, Assert.Single(hit.Answer.Sources));
        Assert.Equal(1.0, hit.Similarity, 3);
    }

    [Fact]
    public async Task Embeddings_of_the_wrong_width_are_rejected()
    {
        var cache = Cache();
        var tooShort = new float[EmbeddingDefaults.Dimensions - 1];
        tooShort[0] = 1;

        await Assert.ThrowsAsync<ArgumentException>(() => cache.FindAsync(Scope(), tooShort, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.StoreAsync(Scope(), tooShort, Answer("q"), CancellationToken.None));
    }

    [Fact]
    public async Task Vectors_without_a_direction_are_never_cached_or_matched()
    {
        var cache = Cache();
        var zero = new float[EmbeddingDefaults.Dimensions];
        var notANumber = TestVectors.Random(1);
        notANumber[5] = float.NaN;

        await cache.StoreAsync(Scope(), zero, Answer("zero"), CancellationToken.None);
        await cache.StoreAsync(Scope(), notANumber, Answer("nan"), CancellationToken.None);

        Assert.Null(await cache.FindAsync(Scope(), zero, CancellationToken.None));
        Assert.Null(await cache.FindAsync(Scope(), notANumber, CancellationToken.None));
        Assert.Null(await cache.FindAsync(Scope(), TestVectors.Random(1), CancellationToken.None));
    }

    protected static SemanticCacheScope Scope(string tenant = "contoso", string ns = "ask", string tier = "fast") => new(tenant, ns, tier);

    protected static CachedAnswer Answer(string question, params Guid[] documents) => new(
        question,
        $"answer to {question}",
        [.. documents.Select((id, i) => new SourceReference(id, $"doc-{i}", 1, $"Document {i}"))],
        "qwen2.5:0.5b",
        100,
        20,
        new DateTime(2099, 1, 15, 12, 0, 0, DateTimeKind.Utc));

    private ISemanticCache Cache(Action<SemanticCacheOptions>? configure = null)
    {
        var options = new SemanticCacheOptions();
        configure?.Invoke(options);
        return CreateCache(options);
    }
}
