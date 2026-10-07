using Sentinel.Guardrails.Pii;

namespace Sentinel.Guardrails.Tests.Pii;

public sealed class PiiRedactorTests
{
    private readonly PiiRedactor _redactor = Guards.Redactor();

    [Fact]
    public void Every_entity_type_is_replaced_and_findings_point_at_the_original_text()
    {
        const string text = "Ben Ayşe, TCKN 10000000146, VKN: 1234567890, IBAN TR33 0006 1005 1978 6457 8413 26, " +
            "kart 4111 1111 1111 1111, tel 0532 123 45 67, e-posta ayse@example.com, IP 192.168.1.10.";
        var vault = new PiiVault();

        var result = _redactor.Redact(text, vault, PiiOrigin.Caller);

        Assert.Equal(
            "Ben Ayşe, TCKN [TCKN_1], VKN: [VKN_1], IBAN [IBAN_1], kart [CARD_1], tel [PHONE_1], e-posta [EMAIL_1], IP [IP_1].",
            result.Text);
        Assert.Equal(
            [PiiType.NationalId, PiiType.TaxNumber, PiiType.Iban, PiiType.PaymentCard, PiiType.PhoneNumber, PiiType.Email, PiiType.IpAddress],
            result.Findings.Select(f => f.Type));
        Assert.Equal(
            ["10000000146", "1234567890", "TR33 0006 1005 1978 6457 8413 26", "4111 1111 1111 1111", "0532 123 45 67", "ayse@example.com", "192.168.1.10"],
            result.Findings.Select(f => text.Substring(f.Start, f.Length)));
        Assert.Equal(7, vault.DistinctValues);
    }

    [Fact]
    public void The_same_value_in_different_formatting_gets_the_same_placeholder()
    {
        var vault = new PiiVault();

        var result = _redactor.Redact(
            "TR33 0006 1005 1978 6457 8413 26 = TR330006100519786457841326; 0532 123 45 67 = 05321234567; John@Example.com = john@example.com",
            vault,
            PiiOrigin.Caller);

        Assert.Equal("[IBAN_1] = [IBAN_1]; [PHONE_1] = [PHONE_1]; [EMAIL_1] = [EMAIL_1]", result.Text);
        Assert.Equal(2, vault.Occurrences[PiiType.Iban]);
        Assert.Equal(3, vault.DistinctValues);
    }

    [Theory]
    [InlineData("05321234567@example.com", "[EMAIL_1]")] // e-mail beats the phone number in its local part
    [InlineData("::ffff:192.168.1.1", "[IP_1]")] // IPv6 beats the embedded IPv4
    [InlineData("+90 532 123 45 67", "[PHONE_1]")] // Turkish and E.164 patterns agree: one finding
    [InlineData("VKN 5321234565", "VKN [VKN_1]")] // same span: the more confident tax number beats a bare mobile
    public void Overlapping_candidates_resolve_to_one_finding(string text, string expected)
    {
        var result = _redactor.Redact(text, new PiiVault(), PiiOrigin.Caller);
        Assert.Equal(expected, result.Text);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void Existing_placeholders_are_never_redacted_again()
    {
        var vault = new PiiVault();
        var first = _redactor.Redact("Mail ayse@example.com, tel 0532 123 45 67", vault, PiiOrigin.Caller);

        var second = _redactor.Redact(first.Text, vault, PiiOrigin.Caller);

        Assert.Equal(first.Text, second.Text);
        Assert.Empty(second.Findings);
        Assert.Empty(_redactor.Redact("[EMAIL_1] [PHONE_12] [IP_3] [TCKN_99]", vault, PiiOrigin.Caller).Findings);
    }

    [Fact]
    public void Wrapping_a_value_in_placeholder_syntax_does_not_protect_it()
    {
        var result = _redactor.Redact("[EMAIL_ayse@example.com] [PHONE_05321234567]", new PiiVault(), PiiOrigin.Caller);
        Assert.DoesNotContain("ayse@example.com", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("05321234567", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TCKN: １０００００００１４６", "１０００００００１４６")] // full-width digits
    [InlineData("mail: ay\u200Bse@exam\u200Dple.com", "ay\u200Bse@exam\u200Dple.com")] // zero-width characters inside
    [InlineData("kart 4111\u00A01111\u00A01111\u00A01111 ok", "4111\u00A01111\u00A01111\u00A01111")] // no-break spaces
    [InlineData("tel 0532–123–45–67", "0532–123–45–67")] // en dashes
    [InlineData("TCKN ١٠٠٠٠٠٠٠١٤٦ ok", "١٠٠٠٠٠٠٠١٤٦")] // Arabic-Indic digits
    [InlineData("kart 4111\uFE0F1111\uFE0F1111\uFE0F1111 ok", "4111\uFE0F1111\uFE0F1111\uFE0F1111")] // variation selectors
    [InlineData("TCKN 1000\U000E00200000146 ok", "1000\U000E00200000146")] // a tag character
    public void Unicode_evasion_does_not_hide_pii(string text, string value)
    {
        var result = _redactor.Redact(text, new PiiVault(), PiiOrigin.Caller);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(value, text.Substring(finding.Start, finding.Length));
        Assert.DoesNotContain(value, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_redaction_returns_the_text_unchanged()
    {
        var redactor = Guards.Redactor(new PiiOptions { Enabled = false });
        var result = redactor.Redact("ayse@example.com", new PiiVault(), PiiOrigin.Caller);
        Assert.Equal("ayse@example.com", result.Text);
        Assert.False(result.HasPii);
    }

    [Fact]
    public void Only_configured_types_are_redacted()
    {
        var redactor = Guards.Redactor(new PiiOptions { Types = [PiiType.Email] });
        var result = redactor.Redact("ayse@example.com 0532 123 45 67", new PiiVault(), PiiOrigin.Caller);
        Assert.Equal("[EMAIL_1] 0532 123 45 67", result.Text);
    }

    [Fact]
    public void Text_without_pii_is_returned_as_is()
    {
        const string text = "Toplantı 2025-06-01 saat 14:30'da, bütçe 1.250.000,00 TL, sürüm 1.2.3.4.5.";
        var result = _redactor.Redact(text, new PiiVault(), PiiOrigin.Caller);
        Assert.Same(text, result.Text);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Concurrent_use_with_separate_vaults_is_consistent()
    {
        const string text = "ayse@example.com 0532 123 45 67 TR330006100519786457841326";
        var results = new string[64];
        Parallel.For(0, results.Length, i => results[i] = _redactor.Redact(text, new PiiVault(), PiiOrigin.Caller).Text);
        Assert.All(results, r => Assert.Equal("[EMAIL_1] [PHONE_1] [IBAN_1]", r));
    }

    [Fact]
    public void Large_texts_full_of_candidates_are_redacted_quickly()
    {
        var text = string.Concat(Enumerable.Repeat("ayse@example.com 0532 123 45 67 TR330006100519786457841326 4111 1111 1111 1111 2025-06-01 1.2.3.4.5 ", 2000));
        _redactor.Redact("warm up 0532 123 45 67", new PiiVault(), PiiOrigin.Caller);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = _redactor.Redact(text, new PiiVault(), PiiOrigin.Caller);
        stopwatch.Stop();

        Assert.Equal(8000, result.Findings.Count);
        Assert.True(stopwatch.ElapsedMilliseconds < 10_000, $"took {stopwatch.ElapsedMilliseconds} ms");
    }
}
