using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace SmartSubtitleConverterApi.Infrastructure;

/// <summary>
/// LOG_FORMAT=text: one plain line per entry, <c>2026-11-09T13:40:51.531Z info  Storage.StorageCheck  message</c>.
/// UTC ISO timestamp, short level, category without the app's root namespace, then the message. Exceptions follow
/// on indented lines. No colors or escape codes, so the output stays grep-friendly in files and log collectors.
/// </summary>
public sealed class LineConsoleFormatter() : ConsoleFormatter(Name)
{
    public new const string Name = "line";
    private const string Root = "SmartSubtitleConverterApi.";
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce", LogLevel.Debug => "dbug", LogLevel.Information => "info",
        LogLevel.Warning => "warn", LogLevel.Error => "fail", LogLevel.Critical => "crit", _ => "none",
    };

    public static string Category(string category) => category.StartsWith(Root, StringComparison.Ordinal) ? category[Root.Length..] : category;

    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter writer)
    {
        var message = entry.Formatter?.Invoke(entry.State, entry.Exception);
        if (message == null && entry.Exception == null) return;
        var line = $"{Clock().UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} {Level(entry.LogLevel),-5} {Category(entry.Category)}  {(message ?? "").ReplaceLineEndings(" | ")}";
        writer.Write(line);
        writer.Write('\n');
        if (entry.Exception != null)
            foreach (var l in entry.Exception.ToString().Split('\n'))
            { writer.Write("    "); writer.Write(l.TrimEnd('\r')); writer.Write('\n'); }
    }
}
