using LibraryBot.Services;

namespace LibraryBot.Tests;

public class TextUtilsTests
{
    [Theory]
    [InlineData("Tom & Jerry", "Tom &amp; Jerry")]
    [InlineData("a<b>", "a&lt;b&gt;")]
    [InlineData("plain text", "plain text")]
    [InlineData("", "")]
    public void EscapeHtml_EscapesSpecialChars(string input, string expected)
        => Assert.Equal(expected, TextUtils.EscapeHtml(input));

    [Fact]
    public void EscapeHtml_Null_ReturnsEmpty()
        => Assert.Equal("", TextUtils.EscapeHtml(null));

    [Fact] // '&' має екрануватися першим, щоб не подвоювати вже екрановані сутності
    public void EscapeHtml_AmpersandFirst_NoDoubleEscape()
        => Assert.Equal("&lt;&amp;&gt;", TextUtils.EscapeHtml("<&>"));
}
