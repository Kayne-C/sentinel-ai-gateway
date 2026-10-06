using Sentinel.Infrastructure.CostControls.Tokens;

namespace Sentinel.Infrastructure.IntegrationTests.CostControls;

public sealed class TiktokenTokenCounterTests
{
    private static readonly TiktokenTokenCounter Counter = new();

    [Theory]
    [InlineData("", 0)]
    [InlineData("Hello, world!", 4)]
    [InlineData("Merhaba dünya!", 4)]
    [InlineData("Mikroservis ve monolit mimarilerini karşılaştır.", 14)]
    public void Counts_o200k_base_tokens(string text, int expected) => Assert.Equal(expected, Counter.CountTokens(text));

    [Fact]
    public void Turkish_text_usually_needs_more_tokens_than_its_english_equivalent()
    {
        var english = Counter.CountTokens("Compare microservice and monolith architectures with their advantages and disadvantages.");
        var turkish = Counter.CountTokens("Mikroservis ve monolit mimarilerini avantaj ve dezavantajlarıyla karşılaştırın.");

        Assert.True(turkish > english, $"tr={turkish} en={english}");
    }

    [Fact]
    public void Counting_is_deterministic_and_thread_safe()
    {
        var texts = Enumerable.Range(0, 64)
            .Select(i => $"İstek {i}: Yıllık izin politikası nedir? Request {i}: what is the annual leave policy? ```code({i});```")
            .ToArray();
        var expected = texts.Select(Counter.CountTokens).ToArray();

        var mismatches = 0;
        Parallel.For(0, texts.Length * 16, i =>
        {
            var index = i % texts.Length;
            if (Counter.CountTokens(texts[index]) != expected[index])
            {
                Interlocked.Increment(ref mismatches);
            }
        });

        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void Null_text_is_rejected() => Assert.Throws<ArgumentNullException>(() => Counter.CountTokens(null!));
}
