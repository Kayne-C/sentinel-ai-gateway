using Sentinel.Guardrails.Pii;

namespace Sentinel.Guardrails.Tests.Pii;

public sealed class OutputGuardTests
{
    private static PiiVault CallerVault(out string redactedPrompt)
    {
        var vault = new PiiVault();
        redactedPrompt = Guards.Redactor().Redact("Benim e-postam ayse@example.com, telefonum 0532 123 45 67.", vault, PiiOrigin.Caller).Text;
        return vault;
    }

    [Fact]
    public void Caller_placeholders_are_restored_in_the_answer()
    {
        var vault = CallerVault(out var prompt);
        Assert.Equal("Benim e-postam [EMAIL_1], telefonum [PHONE_1].", prompt);

        var answer = Guards.Output().Apply("Size [EMAIL_1] adresinden ve [PHONE_1] numarasından ulaşacağız.", vault);

        Assert.Equal("Size ayse@example.com adresinden ve 0532 123 45 67 numarasından ulaşacağız.", answer);
    }

    [Fact]
    public void New_pii_produced_by_the_model_is_masked_and_never_restored()
    {
        var vault = CallerVault(out _);

        var answer = Guards.Output().Apply("Muhasebe: mehmet@corp.example, IBAN TR330006100519786457841326.", vault);

        Assert.Equal("Muhasebe: [EMAIL_2], IBAN [IBAN_1].", answer);
        Assert.False(vault.TryReveal("[EMAIL_2]", includeContext: false, out _));
    }

    [Fact]
    public void Context_placeholders_stay_masked_in_the_answer()
    {
        var vault = CallerVault(out _);
        Guards.Redactor().Redact("Belgedeki kişi: 0533 765 43 21", vault, PiiOrigin.Context);

        var answer = Guards.Output().Apply("Belgede geçen numara [PHONE_2], sizinki [PHONE_1].", vault);

        Assert.Equal("Belgede geçen numara [PHONE_2], sizinki 0532 123 45 67.", answer);
    }

    [Fact]
    public void A_caller_value_repeated_by_the_model_comes_back_in_the_callers_spelling()
    {
        var vault = CallerVault(out _);

        var answer = Guards.Output().Apply("Adresiniz AYSE@example.com, numaranız 05321234567.", vault);

        Assert.Equal("Adresiniz ayse@example.com, numaranız 0532 123 45 67.", answer);
        Assert.Equal(2, vault.DistinctValues);
    }

    [Fact]
    public void Restoring_can_be_disabled()
    {
        var vault = CallerVault(out _);
        var guard = Guards.Output(new PiiOptions { RestoreCallerValuesInAnswers = false });

        Assert.Equal("Size [EMAIL_1] üzerinden ulaşacağız.", guard.Apply("Size [EMAIL_1] üzerinden ulaşacağız.", vault));
    }
}
