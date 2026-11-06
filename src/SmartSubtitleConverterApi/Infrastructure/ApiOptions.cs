namespace SmartSubtitleConverterApi.Infrastructure;

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
    public string[] CorsOrigins { get; init; } = [];
    public string[] KnownProxies { get; init; } = [];
    public string? S3Endpoint { get; init; }
    public string S3Region { get; init; } = "us-east-1";
    public bool S3ForcePathStyle { get; init; }
    public string[] S3AllowedBuckets { get; init; } = [];
    public string[] S3AllowedPrefixes { get; init; } = [];
    public string[] HttpAllowedHosts { get; init; } = [];
    public int IoTimeoutSeconds { get; init; } = 10;
    public int IoRetries { get; init; } = 3;
    public string[] StorageFileRoots { get; init; } = [];
    public bool StorageFileAtomic { get; init; }
    public int RequestWaitSeconds { get; init; } = 30;
    public int JobTtlSeconds { get; init; } = 21600;
    public int JobMaxEntries { get; init; } = 400;
    public int ProfileMaxBytes { get; init; } = 64 * 1024;

    /// <summary>Body cap for multipart uploads: the file plus framing and small form fields.</summary>
    public long MaxMultipartBodyBytes => MaxUploadBytes + 256 * 1024;

    /// <summary>
    /// Body cap for JSON endpoints (/v1/convert/json, /v1/detect): base64 of a MAX_UPLOAD_BYTES file
    /// (4*ceil(n/3)) plus a full profile plus 64 KiB of other fields, so MAX_UPLOAD_BYTES means the same
    /// decoded size on both paths.
    /// </summary>
    public long MaxJsonBodyBytes => 4 * ((MaxUploadBytes + 2) / 3) + ProfileMaxBytes + 64 * 1024;

    public static ApiOptions FromConfiguration(IConfiguration c)
    {
        static string[] List(string? v) =>
            (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        static string[] Roots(string? value) => string.IsNullOrWhiteSpace(value) ? []
            : value.TrimStart().StartsWith('[') ? System.Text.Json.JsonSerializer.Deserialize<string[]>(value) ?? []
            : value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
            S3Endpoint = c["S3_ENDPOINT"],
            S3Region = c["S3_REGION"] ?? d.S3Region,
            S3ForcePathStyle = c.GetValue("S3_FORCE_PATH_STYLE", false),
            S3AllowedBuckets = List(c["S3_ALLOWED_BUCKETS"]),
            S3AllowedPrefixes = List(c["S3_ALLOWED_PREFIXES"]),
            HttpAllowedHosts = List(c["HTTP_ALLOWED_HOSTS"]),
            IoTimeoutSeconds = Math.Clamp(c.GetValue("IO_TIMEOUT_SECONDS", d.IoTimeoutSeconds), 1, 300),
            IoRetries = Math.Clamp(c.GetValue("IO_RETRIES", d.IoRetries), 0, 10),
            StorageFileRoots = Roots(c["STORAGE_FILE_ROOTS"]),
            StorageFileAtomic = c.GetValue("STORAGE_FILE_ATOMIC", false),
            RequestWaitSeconds = Math.Clamp(c.GetValue("REQUEST_WAIT_SECONDS", d.RequestWaitSeconds), 0, 60),
            JobTtlSeconds = Math.Clamp(c.GetValue("JOB_TTL_SECONDS", d.JobTtlSeconds), 1, 86400),
            JobMaxEntries = Math.Clamp(c.GetValue("JOB_MAX_ENTRIES", d.JobMaxEntries), 1, 10000),
            ProfileMaxBytes = Math.Max(1024, c.GetValue("PROFILE_MAX_BYTES", d.ProfileMaxBytes)),
            CorsOrigins = List(c["CORS_ORIGINS"]),
            KnownProxies = List(c["KNOWN_PROXIES"]),
        };
    }
}
