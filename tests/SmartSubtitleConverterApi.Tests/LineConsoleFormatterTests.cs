using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSubtitleConverterApi.Infrastructure;
namespace SmartSubtitleConverterApi.Tests;

public sealed class LineConsoleFormatterTests
{
    private static string Format(LogLevel level, string category, string message, Exception? ex = null)
    {
        var f = new LineConsoleFormatter { Clock = () => new DateTimeOffset(2026, 11, 9, 13, 40, 51, 531, TimeSpan.Zero) };
        using var w = new StringWriter();
        f.Write(new LogEntry<string>(level, category, default, message, ex, (s, _) => s), null, w);
        return w.ToString();
    }

    [Fact]
    public void Writes_one_plain_line_with_utc_time_short_level_and_category()
    {
        Assert.Equal("2026-11-09T13:40:51.531Z info  Storage.StorageCheck  storage.s3 bucket=subs ok\n",
            Format(LogLevel.Information, "SmartSubtitleConverterApi.Storage.StorageCheck", "storage.s3 bucket=subs ok"));
        Assert.Equal("2026-11-09T13:40:51.531Z warn  Microsoft.Hosting.Lifetime  a | b\n", Format(LogLevel.Warning, "Microsoft.Hosting.Lifetime", "a\nb"));
    }

    [Fact]
    public void Exceptions_follow_on_indented_lines_without_escape_codes()
    {
        var text = Format(LogLevel.Error, "Program", "boom", new InvalidOperationException("bad"));
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal("2026-11-09T13:40:51.531Z fail  Program  boom", lines[0]);
        Assert.StartsWith("    System.InvalidOperationException: bad", lines[1]);
        Assert.DoesNotContain('\u001b', text);
    }
}
