using Sentinel.Guardrails.Pii.Recognizers;

namespace Sentinel.Guardrails.Tests.Pii;

public sealed class IbanRecognizerTests
{
    private readonly IbanRecognizer _recognizer = new();

    [Theory]
    [InlineData("TR330006100519786457841326")]
    [InlineData("TR33 0006 1005 1978 6457 8413 26")]
    [InlineData("tr33 0006 1005 1978 6457 8413 26")]
    [InlineData("DE89370400440532013000")]
    [InlineData("GB82 WEST 1234 5698 7654 32")]
    [InlineData("NO9386011117947")]
    [InlineData("BE68539007547034")]
    [InlineData("FR1420041010050500013M02606")]
    [InlineData("MT84MALT011000012345MTLCAST001S")]
    [InlineData("LC55HEMM000100010012001200023015")]
    [InlineData("RU0204452560040702810412345678901")]
    public void Valid_ibans_in_print_and_electronic_format_are_recognized(string iban)
    {
        Assert.Equal([iban], _recognizer.Found($"IBAN: {iban}, teşekkürler"));
    }

    [Fact]
    public void Generated_ibans_for_every_registry_country_are_recognized()
    {
        var random = new Random(3001);
        Assert.True(IbanRegistry.Lengths.Count >= 89);
        foreach (var (country, length) in IbanRegistry.Lengths)
        {
            var iban = TestData.Iban(country, length, random);
            Assert.Equal([iban], _recognizer.Found($"Hesap {iban}"));
            Assert.Equal([TestData.Grouped(iban)], _recognizer.Found($"Hesap {TestData.Grouped(iban)}"));
        }
    }

    [Theory]
    [InlineData("TR340006100519786457841326")] // check digits
    [InlineData("TR33000610051978645784132")] // too short for TR
    [InlineData("TR3300061005197864578413261")] // too long for TR
    [InlineData("XX330006100519786457841326")] // not in the registry
    [InlineData("GB82 WEST 1234 5698 7654 3")]
    public void Invalid_ibans_are_rejected(string value)
    {
        Assert.Empty(_recognizer.Found(value));
    }

    [Fact]
    public void A_following_group_like_word_does_not_hide_the_iban()
    {
        var text = "BE68 5390 0754 7034 test";
        Assert.Equal(["BE68 5390 0754 7034"], _recognizer.Found(text));
    }

    [Fact]
    public void Ibans_glued_to_other_letters_are_not_recognized()
    {
        Assert.Empty(_recognizer.Found("XTR330006100519786457841326"));
        Assert.Empty(_recognizer.Found("TR330006100519786457841326X"));
    }
}
