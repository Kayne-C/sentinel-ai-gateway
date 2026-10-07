using Microsoft.Extensions.AI;
using Sentinel.Application.Common;
using Sentinel.Infrastructure.AI.Offline;

namespace Sentinel.Infrastructure.IntegrationTests.AI;

public sealed class HashingEmbeddingGeneratorTests
{
    private readonly HashingEmbeddingGenerator _generator = new();

    [Theory]
    [InlineData("Yıllık izin kaç gün?")]
    [InlineData("")]
    [InlineData("!!! ??? ...")]
    [InlineData("[EMAIL_1]")]
    public async Task Every_text_maps_to_a_unit_vector_of_the_shared_width(string text)
    {
        var vector = await _generator.GenerateVectorAsync(text);

        Assert.Equal(EmbeddingDefaults.Dimensions, vector.Length);
        Assert.Equal(1.0, Norm(vector.Span), precision: 5);
    }

    [Fact]
    public async Task The_same_text_always_yields_the_same_vector()
    {
        var first = await _generator.GenerateVectorAsync("Şifre sıfırlama adımları nelerdir?");
        var second = await new HashingEmbeddingGenerator().GenerateVectorAsync("Şifre sıfırlama adımları nelerdir?");

        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public async Task Vectors_are_stable_across_processes_and_machines()
    {
        // FNV-1a is specified bit for bit; string.GetHashCode (randomised per process) would fail this.
        Assert.Equal(0xaf63dc4c8601ec8cUL, HashingEmbeddingGenerator.Fnv1a64("a"));

        // Golden values: any change to features, weights or hashing changes them, and would silently make vectors
        // stored by an earlier version incomparable. Update deliberately (and re-embed) or not at all.
        var vector = (await _generator.GenerateVectorAsync("Yıllık izin kaç gün?")).ToArray();
        Assert.Equal(unchecked((int)0xBED148EF), BitConverter.SingleToInt32Bits(vector[149]));
        Assert.Equal(unchecked((int)0xBED148EF), BitConverter.SingleToInt32Bits(vector[281]));
        Assert.Equal(unchecked((int)0xBED148EF), BitConverter.SingleToInt32Bits(vector[302]));
    }

    [Fact]
    public async Task Turkish_casing_and_diacritics_are_folded()
    {
        var expected = (await _generator.GenerateVectorAsync("izin sifre gunluk")).ToArray();

        Assert.Equal(expected, (await _generator.GenerateVectorAsync("İZİN ŞİFRE GÜNLÜK")).ToArray());
        Assert.Equal(expected, (await _generator.GenerateVectorAsync("IZIN şifre günlük")).ToArray());
    }

    [Theory]
    [InlineData("Yıllık izin kaç gün?", "Tam zamanlı çalışanlar yılda 14 gün yıllık izin kullanabilir.", "Sunucular her pazar gecesi bakıma alınır ve yedeklenir.")]
    [InlineData("How do I reset my VPN password?", "To reset your VPN password, open the self-service portal and choose Reset.", "The cafeteria serves lunch between noon and two o'clock.")]
    [InlineData("sifre nasil sifirlanir", "Şifrenizi sıfırlamak için self servis portalını kullanın.", "Toplantı odaları takvim üzerinden rezerve edilir.")]
    [InlineData("izinlerimi nereden görürüm", "İzin bakiyenizi İK portalındaki izinler sayfasında görebilirsiniz.", "Kargo gönderileri resepsiyona teslim edilir.")]
    public async Task Related_texts_score_higher_than_unrelated_ones(string query, string related, string unrelated)
    {
        var embeddings = await _generator.GenerateAsync([query, related, unrelated]);

        var relatedScore = Cosine(embeddings[0].Vector.Span, embeddings[1].Vector.Span);
        var unrelatedScore = Cosine(embeddings[0].Vector.Span, embeddings[2].Vector.Span);
        Assert.True(relatedScore > unrelatedScore + 0.1, $"related {relatedScore:0.000} vs unrelated {unrelatedScore:0.000}");
        Assert.True(relatedScore > 0.1, $"related {relatedScore:0.000}");
    }

    [Fact]
    public async Task Paraphrased_questions_are_close_enough_for_the_semantic_cache()
    {
        var embeddings = await _generator.GenerateAsync(["Yıllık izin kaç gün?", "yillik izin kac gun", "Kaç gün yıllık iznim var?"]);

        Assert.True(Cosine(embeddings[0].Vector.Span, embeddings[1].Vector.Span) > 0.99);
        Assert.True(Cosine(embeddings[0].Vector.Span, embeddings[2].Vector.Span) > 0.45);
    }

    [Fact]
    public async Task Placeholders_do_not_influence_the_vector()
    {
        var plain = await _generator.GenerateVectorAsync("Who approves the leave of ?");
        var withPlaceholder = await _generator.GenerateVectorAsync("Who approves the leave of [EMAIL_1]?");

        Assert.Equal(plain.ToArray(), withPlaceholder.ToArray());
    }

    [Fact]
    public async Task Other_dimensions_are_refused()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _generator.GenerateAsync(["text"], new EmbeddingGenerationOptions { Dimensions = 1536 }));
    }

    [Fact]
    public void Metadata_reports_the_model_and_width()
    {
        var metadata = _generator.GetService<EmbeddingGeneratorMetadata>();

        Assert.NotNull(metadata);
        Assert.Equal(HashingEmbeddingGenerator.ModelId, metadata.DefaultModelId);
        Assert.Equal(EmbeddingDefaults.Dimensions, metadata.DefaultModelDimensions);
    }

    internal static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
        }

        return dot / (Norm(a) * Norm(b));
    }

    private static double Norm(ReadOnlySpan<float> vector)
    {
        double sum = 0;
        foreach (var value in vector)
        {
            sum += value * (double)value;
        }

        return Math.Sqrt(sum);
    }
}
