using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Knowledge;

namespace Sentinel.Application.Tests.Knowledge;

public sealed class TextChunkerTests
{
    [Fact]
    public void Sentences_end_at_terminators_followed_by_whitespace_and_an_upper_case_letter()
    {
        var sentences = Sentences("Bu bir cümle. Bu da ikinci! Üçüncü mü? Evet... Dördüncü… Son cümle.");

        Assert.Equal(["Bu bir cümle.", "Bu da ikinci!", "Üçüncü mü?", "Evet...", "Dördüncü…", "Son cümle."], sentences);
    }

    [Theory]
    [InlineData("Geldi. İzmir'e gitti.")]
    [InlineData("Geldi. Şimdi burada.")]
    [InlineData("Geldi. Çok yorgundu.")]
    [InlineData("Geldi. Öğlen yemeği yedi.")]
    [InlineData("Geldi. Üzgündü.")]
    public void Turkish_upper_case_letters_start_a_new_sentence(string text)
    {
        Assert.Equal(2, Sentences(text).Length);
    }

    [Theory]
    [InlineData("Prof. Dr. Ahmet Yılmaz toplantıya katıldı.")]
    [InlineData("Doç. Dr. Ayşe Kaya sunum yaptı.")]
    [InlineData("Elma, armut vb. Meyveler alındı.")]
    [InlineData("Kırmızı, mavi vs. Renkler seçildi.")]
    [InlineData("Örn. Ankara gibi büyük şehirler.")]
    [InlineData("Bazı şehirler, örn. Ankara, büyüktür.")]
    [InlineData("Acme Ltd. Şti. sözleşmeyi imzaladı.")]
    [InlineData("Acme Ltd.Şti. Genel Müdürü geldi.")]
    [InlineData("Acme A.Ş. Genel Müdürü konuştu.")]
    [InlineData("Daire No. 5 ve Tel. Numarası verildi.")]
    [InlineData("Some fruit, e.g. Apples, are tasty.")]
    [InlineData("That is, i.e. The point stands.")]
    [InlineData("M. Kemal Samsun'a çıktı.")]
    [InlineData("2. Dünya Savaşı 1945'te bitti.")]
    [InlineData("T.C. Kimlik numarası gereklidir.")]
    public void Common_abbreviations_initials_and_ordinals_do_not_end_a_sentence(string text)
    {
        Assert.Equal([text], Sentences(text));
    }

    [Fact]
    public void Abbreviations_are_case_sensitive_so_a_sentence_ending_in_lower_case_no_still_splits()
    {
        Assert.Equal(["The answer was no.", "Then we left."], Sentences("The answer was no. Then we left."));
    }

    [Fact]
    public void Closing_quotes_stay_with_their_sentence()
    {
        Assert.Equal(["Dedi ki: \"Gel.\"", "Sonra gitti."], Sentences("Dedi ki: \"Gel.\" Sonra gitti."));
        Assert.Equal(["Bitti.", "“Yeni” bir gün."], Sentences("Bitti. “Yeni” bir gün."));
    }

    [Fact]
    public void Decimal_points_and_lower_case_continuations_do_not_split()
    {
        var text = "Oran 3.5 oldu. ve devam etti.";
        Assert.Equal([text], Sentences(text));
    }

    [Fact]
    public void Paragraphs_are_separated_by_blank_lines()
    {
        var paragraphs = TextChunker.SplitParagraphs("A b.\r\n\r\n  \t\nC d.\nstill C.\n\n\n\nE");

        Assert.Equal(["A b.", "C d.\nstill C.", "E"], paragraphs);
    }

    [Fact]
    public void Chunks_respect_the_token_target_and_overlap_with_whole_trailing_sentences()
    {
        // 20 sentences of exactly 5 tokens each.
        var content = string.Join(' ', Enumerable.Range(0, 20).Select(i => $"Cümle{i} bir iki üç son."));

        var chunks = Chunker(target: 20, overlap: 5).Chunk(content).Value;

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.InRange(c.TokenCount, 1, 20));
        for (var i = 1; i < chunks.Count; i++)
        {
            var previousLastSentence = TextChunker.SplitSentences(chunks[i - 1].Text)[^1].Text;
            Assert.StartsWith(previousLastSentence + " ", chunks[i].Text, StringComparison.Ordinal);
        }

        // Every sentence is retrievable from at least one chunk, in order.
        var covered = chunks.SelectMany(c => TextChunker.SplitSentences(c.Text).Select(s => s.Text)).Distinct().ToList();
        Assert.Equal(TextChunker.SplitSentences(content).Select(s => s.Text), covered);
    }

    [Fact]
    public void Oversized_sentences_are_split_between_words_never_inside_a_word()
    {
        var words = Enumerable.Range(0, 100).Select(i => $"kelime{i}").ToArray();
        var content = string.Join(' ', words);

        var chunks = Chunker(target: 10, overlap: 3).Chunk(content).Value;

        Assert.All(chunks, c => Assert.InRange(c.TokenCount, 1, 10));
        var position = 0;
        foreach (var chunk in chunks)
        {
            var chunkWords = chunk.Text.Split(' ');
            var start = Array.IndexOf(words, chunkWords[0]);
            Assert.True(start >= 0, $"'{chunkWords[0]}' is not a whole word of the input");
            Assert.Equal(words.Skip(start).Take(chunkWords.Length), chunkWords);
            Assert.True(start <= position, "chunks must not skip words");
            position = start + chunkWords.Length;
        }

        Assert.Equal(words.Length, position);
    }

    [Fact]
    public void A_single_word_longer_than_the_target_is_kept_whole_in_its_own_chunk()
    {
        var longWord = new string('x', 80);
        var content = $"kısa bir giriş {longWord} ve kısa bir son";

        var chunks = Chunker(target: 5, overlap: 1, counter: new CharacterTokenCounter()).Chunk(content).Value;

        Assert.Contains(chunks, c => c.Text == longWord);
        Assert.All(chunks.Where(c => c.Text != longWord), c => Assert.DoesNotContain("x", c.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Paragraph_breaks_are_preserved_inside_a_chunk()
    {
        var chunks = Chunker(target: 100).Chunk("Bir. İki.\n\nÜç dört.\nBeş.").Value;

        var chunk = Assert.Single(chunks);
        Assert.Equal("Bir. İki.\n\nÜç dört.\nBeş.", chunk.Text);
        Assert.Equal(0, chunk.Ordinal);
        Assert.False(chunk.Quarantined);
    }

    [Fact]
    public void Chunking_is_deterministic_and_ordinals_are_sequential()
    {
        var content = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Paragraf {i}. Prof. Dr. Ali {i} geldi. Örn. Bu bir örnek {i}!"));
        var chunker = Chunker(target: 15, overlap: 4);

        var first = chunker.Chunk(content).Value;
        var second = Chunker(target: 15, overlap: 4).Chunk(content).Value;

        Assert.Equal(first, second);
        Assert.Equal(Enumerable.Range(0, first.Count), first.Select(c => c.Ordinal));
    }

    [Fact]
    public void Documents_needing_more_than_MaxChunks_chunks_are_rejected()
    {
        var content = string.Join(' ', Enumerable.Range(0, 50).Select(i => $"Cümle {i} burada."));

        var result = Chunker(target: 5, overlap: 0, maxChunks: 3).Chunk(content);

        Assert.True(result.IsFailure);
        Assert.Equal("Knowledge.TooManyChunks", result.Error.Code);
    }

    [Theory]
    [InlineData(0, 0, 10)]
    [InlineData(10, 10, 10)]
    [InlineData(10, -1, 10)]
    [InlineData(10, 2, 0)]
    public void Invalid_options_are_rejected(int target, int overlap, int maxChunks)
    {
        Assert.Throws<ArgumentException>(() => Chunker(target, overlap, maxChunks));
    }

    private static TextChunker Chunker(int target = 300, int overlap = 40, int maxChunks = 2000, ITokenCounter? counter = null) =>
        new(counter ?? new WordTokenCounter(), Options.Create(new ChunkingOptions { TargetTokens = target, OverlapTokens = overlap, MaxChunks = maxChunks }));

    private static string[] Sentences(string paragraph) => [.. TextChunker.SplitSentences(paragraph).Select(s => s.Text)];

    /// <summary>Roughly one token per four characters, so a single long word can exceed the target.</summary>
    private sealed class CharacterTokenCounter : ITokenCounter
    {
        public int CountTokens(string text) => (text.Length + 3) / 4;
    }
}
