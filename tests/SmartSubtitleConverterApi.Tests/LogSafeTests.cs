using SmartSubtitleConverterApi.Infrastructure;
namespace SmartSubtitleConverterApi.Tests;

public sealed class LogSafeTests
{
    [Theory]
    [InlineData("GET", "GET")]
    [InlineData("a\r\nfake.entry level=crit", "afake.entry level=crit")]
    [InlineData("tab\there\u001b[31m", "tab?here?[31m")]
    [InlineData("plain ünïcode", "plain ünïcode")]
    public void Clean_removes_line_breaks_and_masks_control_characters(string input, string expected) =>
        Assert.Equal(expected, LogSafe.Clean(input));

    [Fact]
    public void Clean_keeps_null_and_formats_objects()
    {
        Assert.Null(LogSafe.Clean((string?)null));
        Assert.Null(LogSafe.Clean((object?)null));
        Assert.Equal("12", LogSafe.Clean((object)12));
    }

    [Fact]
    public void Number_keeps_numbers_numeric()
    {
        Assert.Equal(5L, LogSafe.Number(5));
        Assert.Equal(7L, LogSafe.Number(7L));
        Assert.Null(LogSafe.Number("5"));
        Assert.Null(LogSafe.Number(null));
    }
}
