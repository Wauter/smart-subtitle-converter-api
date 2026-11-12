namespace SmartSubtitleConverterApi.Infrastructure;

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
    public const string JobNotFound = "job-not-found";
    public const string JobInputNotStored = "job-input-not-stored";
    public const string JobOutputNotStored = "job-output-not-stored";
    public const string JobNotReady = "job-not-ready";
    public const string JobFileGone = "job-file-gone";
    public const string JobLocationNotReplayable = "job-location-not-replayable";
    public const string StorageLocationRejected = "storage-location-rejected";
    public const string StorageNotFound = "storage-not-found";
    public const string StorageIoFailed = "storage-io-failed";
    public const string StorageListDisabled = "storage-list-disabled";
    public const string StorageNotListable = "storage-not-listable";
    public const string StorageCursorInvalid = "storage-cursor-invalid";
    public const string OutputExists = "output-exists";
    public const string InvalidRequest = "invalid-request";
    public const string InvalidProfile = "invalid-profile";
    public const string EmptyInput = "empty-input";
    public const string Unauthorized = "unauthorized";
    public const string NotFound = "not-found";
    public const string PresetNotFound = "preset-not-found";
    public const string PayloadTooLarge = "payload-too-large";
    public const string UnsupportedMediaType = "unsupported-media-type";
    public const string FormatNotDetected = "format-not-detected";
    public const string FormatNotSupported = "format-not-supported";
    public const string FormatNotWritable = "format-not-writable";
    public const string FormatOptionInvalid = "format-option-invalid";
    public const string InvalidBinaryInput = "invalid-binary-input";
    public const string AmbiguousFormat = "ambiguous-format";
    public const string AmbiguousEncoding = "ambiguous-encoding";
    public const string EncodingError = "encoding-error";
    public const string UnsafeXml = "unsafe-xml";
    public const string InputLimitsExceeded = "input-limits-exceeded";
    public const string ConversionFailed = "conversion-failed";
    public const string RateLimited = "rate-limited";
    public const string QueueFull = "queue-full";
    public const string ConversionTimeout = "conversion-timeout";
    public const string InternalError = "internal-error";
    public const string HiddenPath = "hidden-path";
    public const string ExtensionNotAllowed = "extension-not-allowed";
    public const string HostNotAllowed = "host-not-allowed";
    public const string OriginNotAllowed = "origin-not-allowed";
    public const string FolderTooLarge = "folder-too-large";
    public const string BrowseBusy = "browse-busy";
    public const string ConversionEngineStuck = "conversion-engine-stuck";

    public static string Title(string code) => code switch
    {
        JobNotFound => "Job not found",
        JobInputNotStored => "Job input not stored",
        JobOutputNotStored => "Job output not stored",
        JobNotReady => "Job not ready",
        JobFileGone => "Job file gone",
        JobLocationNotReplayable => "Job location not replayable",
        StorageLocationRejected => "Storage location rejected",
        StorageNotFound => "Storage file not found",
        StorageIoFailed => "Storage I/O failed",
        StorageListDisabled => "Storage listing disabled",
        StorageNotListable => "Storage location not listable",
        StorageCursorInvalid => "Storage cursor invalid",
        OutputExists => "Output exists",
        InvalidRequest => "Invalid request",
        InvalidProfile => "Invalid profile",
        EmptyInput => "Empty input",
        Unauthorized => "Unauthorized",
        NotFound => "Not found",
        PresetNotFound => "Preset not found",
        PayloadTooLarge => "Payload too large",
        UnsupportedMediaType => "Unsupported media type",
        FormatNotDetected => "Format not detected",
        FormatNotSupported => "Format not supported",
        FormatNotWritable => "Format not writable",
        FormatOptionInvalid => "Invalid format option",
        InvalidBinaryInput => "Invalid binary input",
        AmbiguousFormat => "Ambiguous format",
        AmbiguousEncoding => "Ambiguous encoding",
        EncodingError => "Encoding error",
        UnsafeXml => "Unsafe XML",
        InputLimitsExceeded => "Input limits exceeded",
        ConversionFailed => "Conversion failed",
        RateLimited => "Rate limited",
        QueueFull => "Queue full",
        ConversionTimeout => "Conversion timeout",
        InternalError => "Internal error",
        HiddenPath => "Hidden path",
        ExtensionNotAllowed => "Extension not allowed",
        HostNotAllowed => "Host not allowed",
        OriginNotAllowed => "Origin not allowed",
        FolderTooLarge => "Folder too large",
        BrowseBusy => "Browse busy",
        ConversionEngineStuck => "Conversion engine stuck",
        // Every code above has its own title; anything else is an unexpected server error.
        _ => "Internal error",
    };
}
