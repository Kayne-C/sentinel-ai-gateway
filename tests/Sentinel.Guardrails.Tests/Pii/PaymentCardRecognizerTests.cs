using Sentinel.Guardrails.Pii.Recognizers;

namespace Sentinel.Guardrails.Tests.Pii;

public sealed class PaymentCardRecognizerTests
{
    private readonly PaymentCardRecognizer _recognizer = new();

    [Theory]
    [InlineData("4111111111111111")]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("4111-1111-1111-1111")]
    [InlineData("4012888888881881")]
    [InlineData("4222222222222")]
    [InlineData("5555555555554444")]
    [InlineData("5105 1051 0510 5100")]
    [InlineData("2223003122003222")]
    [InlineData("378282246310005")]
    [InlineData("3782 822463 10005")]
    [InlineData("6011111111111117")]
    [InlineData("6011000990139424")]
    [InlineData("3530111333300000")]
    [InlineData("3566002020360505")]
    [InlineData("6200000000000005")]
    [InlineData("9792030000000000")]
    public void Known_test_cards_are_recognized(string pan)
    {
        Assert.Equal([pan], _recognizer.Found($"Kart: {pan} son kullanma 12/27"));
    }

    [Theory]
    [InlineData("4", 16)]
    [InlineData("4", 13)]
    [InlineData("4", 19)]
    [InlineData("51", 16)]
    [InlineData("55", 16)]
    [InlineData("2221", 16)]
    [InlineData("2720", 16)]
    [InlineData("34", 15)]
    [InlineData("37", 15)]
    [InlineData("6011", 16)]
    [InlineData("644", 16)]
    [InlineData("649", 19)]
    [InlineData("65", 16)]
    [InlineData("3528", 16)]
    [InlineData("3589", 19)]
    [InlineData("62", 16)]
    [InlineData("9792", 16)]
    public void Generated_cards_of_every_issuer_are_recognized(string prefix, int length)
    {
        var random = new Random(prefix.GetHashCode(StringComparison.Ordinal) ^ length);
        for (var i = 0; i < 20; i++)
        {
            var pan = TestData.Card(prefix, length, random);
            Assert.Equal([pan], _recognizer.Found($"card {pan}."));
        }
    }

    [Theory]
    [InlineData("4111111111111112")] // Luhn
    [InlineData("1234567812345670")] // Luhn-valid, unknown issuer
    [InlineData("41111111111114")] // Luhn-valid Visa prefix, 14 digits
    [InlineData("3714496353984310")] // Amex prefix, 16 digits
    [InlineData("41111111111111110000")] // 20 digits
    [InlineData("x4111111111111111")]
    [InlineData("4111111111111111.50")]
    public void Invalid_or_embedded_numbers_are_rejected(string text)
    {
        Assert.Empty(_recognizer.Found(text));
    }

    [Theory]
    [InlineData("4111 1111 1111 1111 12 27 123", "4111 1111 1111 1111")]
    [InlineData("Kart 4111 1111 1111 1111 12/27 CVV 123", "4111 1111 1111 1111")]
    [InlineData("2024 4111 1111 1111 1111", "4111 1111 1111 1111")]
    [InlineData("TR330006100519786457841326 4111-1111-1111-1111 2025-06-01", "4111-1111-1111-1111")]
    public void Cards_next_to_other_number_groups_are_still_found(string text, string pan)
    {
        Assert.Equal([pan], _recognizer.Found(text));
    }

    [Fact]
    public void Two_cards_in_one_grouped_sequence_are_both_found()
    {
        Assert.Equal(["4111 1111 1111 1111", "5555 5555 5555 4444"], _recognizer.Found("4111 1111 1111 1111 5555 5555 5555 4444"));
    }
}

