namespace SubtitleEditApi.Infrastructure;

/// <summary>Runtime configuration, read from environment variables (docs/design.md §3, §6).</summary>
public sealed class ApiOptions
{
    public long MaxUploadBytes { get; init; } = 10 * 1024 * 1024;
    public int ConversionTimeoutSeconds { get; init; } = 30;
    public int QueueCapacity { get; init; } = 32;
    public string? ProfilesDir { get; init; } = "/profiles";
    public string? ApiKeys { get; init; }
    public string? ApiKeysFile { get; init; }
    public bool ErrorsVerbose { get; init; } = true;
    public bool DocsEnabled { get; init; } = true;
    public bool MetricsEnabled { get; init; }
    public int? MetricsPort { get; init; }
    public int RateLimitPerMinute { get; init; }
    public int MaxCues { get; init; } = 200_000;
    public int MaxLineChars { get; init; } = 10_000;
    public int ZipMaxEntries { get; init; } = 100;
    public long ZipMaxUnpackedBytes { get; init; } = 50 * 1024 * 1024;
    public double ZipMaxRatio { get; init; } = 100;
    public string[] CorsOrigins { get; init; } = [];
    public string[] KnownProxies { get; init; } = [];
    public int ProfileMaxBytes { get; init; } = 64 * 1024;

    public static ApiOptions FromConfiguration(IConfiguration c)
    {
        static string[] List(string? v) =>
            (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var d = new ApiOptions();
        return new ApiOptions
        {
            MaxUploadBytes = c.GetValue("MAX_UPLOAD_BYTES", d.MaxUploadBytes),
            ConversionTimeoutSeconds = c.GetValue("CONVERSION_TIMEOUT_SECONDS", d.ConversionTimeoutSeconds),
            QueueCapacity = Math.Max(1, c.GetValue("QUEUE_CAPACITY", d.QueueCapacity)),
            ProfilesDir = c["PROFILES_DIR"] ?? d.ProfilesDir,
            ApiKeys = c["API_KEYS"],
            ApiKeysFile = c["API_KEYS_FILE"],
            ErrorsVerbose = c.GetValue("ERRORS_VERBOSE", true),
            DocsEnabled = c.GetValue("DOCS_ENABLED", true),
            MetricsEnabled = c.GetValue("METRICS_ENABLED", false),
            MetricsPort = c.GetValue<int?>("METRICS_PORT"),
            RateLimitPerMinute = c.GetValue("RATE_LIMIT_PER_MINUTE", 0),
            MaxCues = c.GetValue("MAX_CUES", d.MaxCues),
            MaxLineChars = c.GetValue("MAX_LINE_CHARS", d.MaxLineChars),
            ZipMaxEntries = c.GetValue("ZIP_MAX_ENTRIES", d.ZipMaxEntries),
            ZipMaxUnpackedBytes = c.GetValue("ZIP_MAX_UNPACKED_BYTES", d.ZipMaxUnpackedBytes),
            CorsOrigins = List(c["CORS_ORIGINS"]),
            KnownProxies = List(c["KNOWN_PROXIES"]),
        };
    }
}
