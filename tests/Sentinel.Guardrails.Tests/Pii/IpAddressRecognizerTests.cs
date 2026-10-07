using Sentinel.Guardrails.Pii.Recognizers;

namespace Sentinel.Guardrails.Tests.Pii;

public sealed class IpAddressRecognizerTests
{
    private readonly IpAddressRecognizer _recognizer = new();

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.255")]
    [InlineData("8.8.8.8")]
    [InlineData("255.255.255.255")]
    [InlineData("2001:db8::1")]
    [InlineData("fe80::1ff:fe23:4567:890a")]
    [InlineData("::1")]
    [InlineData("2001:0db8:85a3:0000:0000:8a2e:0370:7334")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("2001:db8::")]
    public void Addresses_are_recognized(string ip)
    {
        Assert.Contains(ip, _recognizer.Found($"Kaynak IP: {ip} engellendi"));
    }

    [Theory]
    [InlineData("256.1.1.1")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("v1.2.3.4")]
    [InlineData("12:30:45")]
    [InlineData("00:1A:2B:3C:4D:5E")]
    [InlineData("std::vector")]
    [InlineData("a::b")]
    [InlineData("::")]
    public void Non_addresses_are_rejected(string text)
    {
        Assert.Empty(_recognizer.Found($"x {text} y"));
    }

    [Fact]
    public void A_trailing_colon_does_not_hide_an_ipv6_address()
    {
        Assert.Equal(["2001:db8::1"], _recognizer.Found("Sunucu 2001:db8::1: erişilemiyor"));
    }
}
