namespace SubtitleEditConverterApi.Infrastructure;

/// <summary>A JSON-pointer located problem inside a request (profile field, zip entry, ...).</summary>
public sealed record FieldError(string Pointer, string Message, string? Hint = null);

/// <summary>
/// The one exception type our layer throws for client-visible failures. It carries the stable
/// <see cref="Code"/> (docs/design.md §4.8) and is rendered as RFC 7807 problem JSON.
/// </summary>
public sealed class ApiException : Exception
{
    public int Status { get; }
    public string Code { get; }
    public string Title { get; }
    public string Hint { get; }
    public IReadOnlyList<FieldError> Errors { get; init; } = [];
    /// <summary>Verbose-only diagnostics (removed when ERRORS_VERBOSE=false).</summary>
    public Dictionary<string, object?> Debug { get; } = new();
    /// <summary>Diagnostics that are safe in non-verbose mode too.</summary>
    public Dictionary<string, object?> Extensions { get; } = new();
    public int? RetryAfterSeconds { get; init; }

    public ApiException(int status, string code, string detail, string hint, Exception? inner = null)
        : base(detail, inner)
    {
        Status = status;
        Code = code;
        Hint = hint;
        Title = ErrorCodes.Title(code);
    }
}

public static class ErrorCodes
{
    public const string InvalidRequest = "invalid-request";
    public const string InvalidProfile = "invalid-profile";
    public const string EmptyInput = "empty-input";
    public const string Unauthorized = "unauthorized";
    public const string NotFound = "not-found";
    public const string PresetNotFound = "preset-not-found";
    public const string PayloadTooLarge = "payload-too-large";
    public const string ZipLimitsExceeded = "zip-limits-exceeded";
    public const string UnsafeZip = "unsafe-zip";
    public const string UnsupportedMediaType = "unsupported-media-type";
    public const string FormatNotDetected = "format-not-detected";
    public const string FormatNotSupported = "format-not-supported";
    public const string EncodingError = "encoding-error";
    public const string UnsafeXml = "unsafe-xml";
    public const string InputLimitsExceeded = "input-limits-exceeded";
    public const string ConversionFailed = "conversion-failed";
    public const string RateLimited = "rate-limited";
    public const string QueueFull = "queue-full";
    public const string ConversionTimeout = "conversion-timeout";
    public const string InternalError = "internal-error";

    public static string Title(string code) => code switch
    {
        InvalidRequest => "Invalid request",
        InvalidProfile => "Invalid profile",
        EmptyInput => "Empty input",
        Unauthorized => "Unauthorized",
        NotFound => "Not found",
        PresetNotFound => "Preset not found",
        PayloadTooLarge => "Payload too large",
        ZipLimitsExceeded => "Zip limits exceeded",
        UnsafeZip => "Unsafe zip entry",
        UnsupportedMediaType => "Unsupported media type",
        FormatNotDetected => "Format not detected",
        FormatNotSupported => "Format not supported",
        EncodingError => "Encoding error",
        UnsafeXml => "Unsafe XML",
        InputLimitsExceeded => "Input limits exceeded",
        ConversionFailed => "Conversion failed",
        RateLimited => "Rate limited",
        QueueFull => "Queue full",
        ConversionTimeout => "Conversion timeout",
        _ => "Internal error",
    };
}
