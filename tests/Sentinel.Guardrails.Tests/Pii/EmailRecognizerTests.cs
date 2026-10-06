using Sentinel.Guardrails.Pii.Recognizers;

namespace Sentinel.Guardrails.Tests.Pii;

public sealed class EmailRecognizerTests
{
    private readonly EmailRecognizer _recognizer = new();

    [Theory]
    [InlineData("john.doe@example.com")]
    [InlineData("JOHN+tag@Example.CO.UK")]
    [InlineData("a_b-c@sub.domain.org")]
    [InlineData("ahmet.yılmaz@örnek.com.tr")]
    [InlineData("user@xn--80ak6aa92e.com")]
    [InlineData("x@y.io")]
    public void Addresses_are_recognized(string email)
    {
        Assert.Equal([email], _recognizer.Found($"Yazın: {email}. Teşekkürler"));
    }

    [Theory]
    [InlineData("john@")]
    [InlineData("@example.com")]
    [InlineData("john@example")]
    [InlineData("john@example.c")]
    [InlineData("john.@example.com")]
    [InlineData("[EMAIL_1]")]
    public void Malformed_addresses_are_rejected(string text)
    {
        Assert.Empty(_recognizer.Found(text));
    }

    [Fact]
    public void Overlong_addresses_are_rejected()
    {
        var local = new string('a', 64);
        var label = new string('b', 63);
        var tooLong = $"{local}@{label}.{label}.{label}.{label}.com";
        Assert.True(tooLong.Length > 254);
        Assert.Empty(_recognizer.Found(tooLong));
    }
}
