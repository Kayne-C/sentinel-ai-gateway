using Sentinel.Guardrails.Pii.Recognizers;

namespace Sentinel.Guardrails.Tests.Pii;

public sealed class TaxNumberRecognizerTests
{
    private readonly TaxNumberRecognizer _recognizer = new();

    [Theory]
    [InlineData("Vergi No: {0}")]
    [InlineData("VKN {0}")]
    [InlineData("vergi kimlik numarası {0}")]
    [InlineData("VERGİ NUMARASI: {0}")]
    [InlineData("Firmanın vergı no'su {0} olarak kayıtlı")]
    [InlineData("Tax ID: {0}")]
    [InlineData("tax number - {0}")]
    public void Valid_numbers_with_a_tax_keyword_before_them_are_recognized(string template)
    {
        foreach (var vkn in new[] { "1234567890", "9876543217", "0000000001" })
        {
            Assert.Equal([vkn], _recognizer.Found(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, vkn)));
        }
    }

    [Fact]
    public void Generated_valid_numbers_are_recognized_with_context()
    {
        var random = new Random(2001);
        for (var i = 0; i < 300; i++)
        {
            var vkn = TestData.TaxNumber(random);
            Assert.Equal([vkn], _recognizer.Found($"VKN: {vkn}"));
        }
    }

    [Fact]
    public void Without_a_keyword_ten_digit_numbers_are_ignored()
    {
        Assert.Empty(_recognizer.Found("Sipariş numaranız 1234567890, teşekkürler."));
        Assert.Empty(_recognizer.Found("1234567890"));
    }

    [Fact]
    public void A_keyword_further_back_than_the_window_does_not_count()
    {
        var padding = new string('x', TaxNumberRecognizer.ContextWindow);
        Assert.Empty(_recognizer.Found($"vergi {padding} 1234567890"));
    }

    [Theory]
    [InlineData("1234567891")]
    [InlineData("9876543210")]
    [InlineData("12345678901")]
    [InlineData("123456789")]
    public void Invalid_check_digit_or_length_is_rejected_even_with_context(string value)
    {
        Assert.Empty(_recognizer.Found($"Vergi no: {value}"));
    }

    [Fact]
    public void Generated_numbers_with_a_wrong_check_digit_are_rejected()
    {
        var random = new Random(2002);
        for (var i = 0; i < 100; i++)
        {
            var vkn = TestData.TaxNumber(random);
            var wrong = vkn[..9] + (char)('0' + ((vkn[9] - '0' + 1 + random.Next(9)) % 10));
            Assert.Empty(_recognizer.Found($"VKN {wrong}"));
        }
    }
}
