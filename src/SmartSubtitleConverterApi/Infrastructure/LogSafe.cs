namespace SmartSubtitleConverterApi.Infrastructure;

/// <summary>
/// Makes client-influenced values safe to put in a log entry: line breaks are removed and other control
/// characters become '?', so a value such as a header, method or file name can't start a forged log line.
/// Both console formatters already escape or collapse line breaks; this keeps the guarantee at the call site.
/// </summary>
public static class LogSafe
{
    public static string? Clean(string? value)
    {
        if (value is null) return null;
        var s = value.Replace("\r", "").Replace("\n", "");
        foreach (var ch in s)
            if (char.IsControl(ch))
                return new string(s.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return s;
    }

    /// <summary>String form of a non-numeric log value (null stays null).</summary>
    public static string? Clean(object? value) => value is null ? null : Clean(value.ToString());

    /// <summary>Numeric log value kept numeric for structured (JSON) logs; anything else is null.</summary>
    public static long? Number(object? value) => value switch
    {
        int i => i,
        long l => l,
        double d => (long)d,
        _ => null,
    };
}
