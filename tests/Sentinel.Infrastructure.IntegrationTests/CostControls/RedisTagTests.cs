using Sentinel.Infrastructure.CostControls.Caching;

namespace Sentinel.Infrastructure.IntegrationTests.CostControls;

/// <summary>Unit tests of the TAG escaping rules; the cache behaviour tests prove them against a real Redis 8.</summary>
public sealed class RedisTagTests
{
    [Theory]
    [InlineData("contoso", "contoso")]
    [InlineData("under_score", "under_score")]
    [InlineData("4f9b7c5e-2d1a-4b8e-9c3f-0a1b2c3d4e5f", @"4f9b7c5e\-2d1a\-4b8e\-9c3f\-0a1b2c3d4e5f")]
    [InlineData("contoso.com", @"contoso\.com")]
    [InlineData("user@contoso.com", @"user\@contoso\.com")]
    [InlineData("Contoso Ltd", @"Contoso\ Ltd")]
    [InlineData("con*", @"con\*")]
    [InlineData("x} | @ns:{ask", @"x\}\ \|\ \@ns\:\{ask")]
    [InlineData(@"back\slash", @"back\\slash")]
    [InlineData("$param", @"\$param")]
    [InlineData("a,b", @"a\,b")]
    [InlineData("\"'`~!#%^&()+=[]<>;?/", @"\""\'\`\~\!\#\%\^\&\(\)\+\=\[\]\<\>\;\?\/")]
    public void Ascii_punctuation_and_spaces_are_escaped(string value, string escaped) => Assert.Equal(escaped, RedisTag.Escape(value));

    [Theory]
    [InlineData("İstanbul Şirketi", @"İstanbul\ Şirketi")]
    [InlineData("emoji😀tenant", "emoji😀tenant")]
    [InlineData("en–dash€", "en–dash€")]
    [InlineData("zero\u200Bwidth", "zero\u200Bwidth")]
    public void Non_ascii_characters_are_left_alone_because_escaping_them_breaks_matching(string value, string escaped) =>
        Assert.Equal(escaped, RedisTag.Escape(value));

    [Fact]
    public void Filter_wraps_the_escaped_value_in_braces() =>
        Assert.Equal(@"@tenant:{a\.b\}}", RedisTag.Filter("tenant", "a.b}"));

    [Fact]
    public void Every_ascii_character_that_is_not_a_letter_digit_or_underscore_is_escaped()
    {
        for (var c = (char)0x20; c < 0x7F; c++)
        {
            var escaped = RedisTag.Escape(c.ToString());
            var expected = char.IsAsciiLetterOrDigit(c) || c == '_' ? c.ToString() : "\\" + c;
            Assert.Equal(expected, escaped);
        }
    }

    [Theory]
    [InlineData("contoso")]
    [InlineData("a,b")]
    [InlineData("Contoso Ltd")]
    [InlineData("x} | @ns:{ask")]
    [InlineData("emoji😀")]
    [InlineData("a")]
    public void Ordinary_values_are_representable(string value) => Assert.True(RedisTag.IsRepresentable(value));

    [Fact]
    public void Values_the_engine_would_alter_are_not_representable()
    {
        string?[] values =
        [
            null,
            "",
            " leading",
            "trailing ",
            "trailing\u00A0",
            "\u3000ideographic-space",
            "tab\tinside",
            "line\nbreak",
            "nul\0",
            "del\u007F",
            "unit" + RedisTag.SingleValueSeparator + "separator",
            "lone" + (char)0xD800,
            (char)0xDC00 + "lone",
            "swapped" + (char)0xDC00 + (char)0xD800,
            new string('x', RedisTag.MaxValueLength + 1),
        ];

        Assert.All(values, v => Assert.False(RedisTag.IsRepresentable(v), $"'{v}' should not be representable"));
        Assert.True(RedisTag.IsRepresentable(new string('x', RedisTag.MaxValueLength)));
        Assert.True(RedisTag.IsRepresentable("pair" + "😀"));
    }
}
