using Sentinel.Guardrails.Pii.Recognizers;

namespace Sentinel.Guardrails.Tests.Pii;

public sealed class NationalIdRecognizerTests
{
    private readonly NationalIdRecognizer _recognizer = new();

    [Theory]
    [InlineData("100 000 001 46")]
    [InlineData("123-456-789-50")]
    [InlineData("190 909 090 18")]
    public void The_written_out_form_in_groups_of_three_is_recognized(string tckn)
    {
        Assert.Equal([tckn], _recognizer.Found($"T.C. Kimlik No: {tckn}."));
    }

    [Theory]
    [InlineData("100 000 001 47")] // wrong check digit
    [InlineData("4111 1111 1111 1111")] // a card number is not a slice of national ids
    [InlineData("0532 123 45 67")]
    [InlineData("1 100 000 001 46")] // part of a longer grouped number
    [InlineData("100 000 001 46 7")]
    public void Grouped_numbers_that_are_not_national_ids_are_left_alone(string text)
    {
        Assert.Empty(_recognizer.Found($"Değer: {text} yazıldı."));
    }

    [Theory]
    [InlineData("10000000146")]
    [InlineData("11111111110")]
    [InlineData("12345678950")]
    [InlineData("19090909018")] // (odd*7 - even) is negative: a naive C# % would compute the wrong d10
    public void Well_known_valid_numbers_are_recognized(string tckn)
    {
        Assert.Equal([tckn], _recognizer.Found($"T.C. Kimlik No: {tckn}."));
    }

    [Fact]
    public void Generated_valid_numbers_are_recognized()
    {
        var random = new Random(1001);
        for (var i = 0; i < 300; i++)
        {
            var tckn = TestData.NationalId(random);
            Assert.Equal([tckn], _recognizer.Found($"kimlik {tckn} ile"));
        }
    }

    [Fact]
    public void Changing_any_single_digit_invalidates_the_number()
    {
        var random = new Random(1002);
        for (var i = 0; i < 50; i++)
        {
            var tckn = TestData.NationalId(random);
            for (var position = 0; position < 11; position++)
            {
                var digit = tckn[position] - '0';
                var changed = tckn[..position] + (char)('0' + ((digit + 1 + random.Next(9)) % 10)) + tckn[(position + 1)..];
                Assert.Empty(_recognizer.Found(changed));
            }
        }
    }

    [Theory]
    [InlineData("10000000147")] // wrong d11
    [InlineData("10000000156")] // wrong d10
    [InlineData("01234567890")] // leading zero
    [InlineData("1000000014")] // 10 digits
    [InlineData("100000001460")] // 12 digits
    public void Invalid_numbers_are_rejected(string value)
    {
        Assert.Empty(_recognizer.Found(value));
    }

    [Theory]
    [InlineData("TC10000000146")]
    [InlineData("10000000146X")]
    [InlineData("910000000146")]
    [InlineData("100000001469")]
    [InlineData("3.10000000146")]
    [InlineData("10000000146.5")]
    public void Numbers_embedded_in_longer_runs_are_not_recognized(string text)
    {
        Assert.Empty(_recognizer.Found(text));
    }
}
