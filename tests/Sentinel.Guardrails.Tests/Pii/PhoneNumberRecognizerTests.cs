using Sentinel.Guardrails.Pii.Recognizers;

namespace Sentinel.Guardrails.Tests.Pii;

public sealed class PhoneNumberRecognizerTests
{
    private readonly PhoneNumberRecognizer _recognizer = new();

    [Theory]
    [InlineData("+90 532 123 45 67")]
    [InlineData("+905321234567")]
    [InlineData("0 532 123 45 67")]
    [InlineData("05321234567")]
    [InlineData("5321234567")]
    [InlineData("532 123 45 67")]
    [InlineData("(0212) 123 45 67")]
    [InlineData("0212 123 45 67")]
    [InlineData("+90 (212) 123 45 67")]
    [InlineData("+90 212 123 4567")]
    [InlineData("0532-123-45-67")]
    [InlineData("0532.123.45.67")]
    [InlineData("0090 532 123 45 67")]
    [InlineData("0850 222 33 44")]
    [InlineData("+1 (415) 555-2671")]
    [InlineData("+44 20 7946 0958")]
    [InlineData("+4930123456")]
    public void Turkish_and_international_formats_are_recognized(string phone)
    {
        var found = _recognizer.Found($"Beni {phone} numarasından arayın.");
        Assert.Contains(phone, found);
    }

    [Theory]
    [InlineData("2025-06-01")]
    [InlineData("01.06.2025")]
    [InlineData("12:30")]
    [InlineData("1.234,56 TL")]
    [InlineData("5.321.234.567,89")]
    [InlineData("10000000146")]
    [InlineData("TR33 0006 1005 1978 6457 8413 26")]
    [InlineData("TR330006100519786457841326")]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("5555555555554444")]
    [InlineData("53212345678")]
    [InlineData("192.168.1.1")]
    [InlineData("v5321234567")]
    public void Dates_amounts_and_other_digit_runs_are_not_phone_numbers(string text)
    {
        Assert.Empty(_recognizer.Found($"Değer: {text} kaydedildi"));
    }

    [Theory]
    [InlineData("Sipariş numaranız 5321234567 olarak oluşturuldu.")]
    [InlineData("Takip no 5551234567 ile sorgulayın.")]
    [InlineData("Order id 5321234567 shipped.")]
    public void Ten_bare_digits_starting_with_5_are_not_a_phone_number_without_a_phone_context(string text)
    {
        Assert.Empty(_recognizer.Found(text));
    }

    [Theory]
    [InlineData("Tel: 5321234567")]
    [InlineData("Cep 5321234567 numaralı hat")]
    [InlineData("WhatsApp'tan 5321234567 yazın")]
    [InlineData("please call 5321234567 today")]
    public void Ten_bare_digits_starting_with_5_are_a_phone_number_with_a_phone_context(string text)
    {
        Assert.Contains("5321234567", _recognizer.Found(text));
    }

    [Fact]
    public void A_phone_number_after_a_date_is_still_found()
    {
        Assert.Contains("0532 123 45 67", _recognizer.Found("Tarih 01.06.2025 0532 123 45 67 numarasıyla görüşüldü"));
    }
}
