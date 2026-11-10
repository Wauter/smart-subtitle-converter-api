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
    /// <summary>API_AUTH_DISABLED=true turns bearer keys off (local experiments only; loud startup warning).</summary>
    public bool ApiAuthDisabled { get; init; }
    /// <summary>ALLOWED_HOSTS: extra Host names accepted besides localhost, 127.0.0.1 and [::1]; "*" accepts any Host.</summary>
    public string[] AllowedHosts { get; init; } = [];
    /// <summary>ERRORS_VERBOSE: stack traces and internals in error responses. Off by default; details always go to the server log.</summary>
    public bool ErrorsVerbose { get; init; }
    /// <summary>Browse queue (listing, read by location, job file reads): concurrent slots, waiting line and wait budget.</summary>
    public int BrowseConcurrency { get; init; } = 4;
    public int BrowseQueueCapacity { get; init; } = 32;
    public int BrowseWaitSeconds { get; init; } = 30;
    /// <summary>
    /// CONVERSION_STUCK_GRACE_SECONDS: how long a timed-out conversion may keep running before the process exits on purpose
    /// (so the restart policy brings up a clean one). 0 = 2 x CONVERSION_TIMEOUT_SECONDS.
    /// </summary>
    public int ConversionStuckGraceSeconds { get; init; }
    public int EffectiveStuckGraceSeconds => ConversionStuckGraceSeconds > 0 ? ConversionStuckGraceSeconds : 2 * ConversionTimeoutSeconds;
    public bool DocsEnabled { get; init; } = true;
    public bool MetricsEnabled { get; init; }
    public int? MetricsPort { get; init; }
    public int RateLimitPerMinute { get; init; }
    public int MaxCues { get; init; } = 200_000;
    public int MaxLineChars { get; init; } = 10_000;
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
    /// <summary>STORAGE_LIST_ENABLED: browse allowed locations and read files by location (/v1/storage/*). On by default; still limited to the allowlists.</summary>
    public bool StorageListEnabled { get; init; } = true;
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

        static string[] Roots(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return [];
            if (!value.TrimStart().StartsWith('[')) return value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(value) ?? []; }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidOperationException($"Invalid configuration: STORAGE_FILE_ROOTS=\"{value}\" starts with '[' but is not a JSON array of strings ({ex.Message}). "
                    + "Fix: use STORAGE_FILE_ROOTS=[\"/data/in\",\"/data/out\"] or the ':'-separated form STORAGE_FILE_ROOTS=/data/in:/data/out.", ex);
            }
        }
        // Typed values: keep failing startup on malformed input, but name the variable, the value and an example fix.
        T Get<T>(string key, T fallback)
        {
            try { return c.GetValue(key, fallback)!; }
            catch (InvalidOperationException ex)
            {
                var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                var expected = type == typeof(bool) ? "true or false" : "a whole number";
                var example = type == typeof(bool) ? "true" : (fallback?.ToString() ?? "8080");
                throw new InvalidOperationException($"Invalid configuration: {key}=\"{c[key]}\" is not {expected}. Fix: set {key}={example} (or unset it for the default)"
                    + (key == "IO_TIMEOUT_SECONDS" ? "; whole seconds, clamped to 1..300." : key == "S3_FORCE_PATH_STYLE" ? "; true for MinIO/path-style endpoints, false for AWS." : "."), ex);
            }
        }
        var d = new ApiOptions();
        var o = new ApiOptions
        {
            MaxUploadBytes = Get("MAX_UPLOAD_BYTES", d.MaxUploadBytes),
            ConversionTimeoutSeconds = Get("CONVERSION_TIMEOUT_SECONDS", d.ConversionTimeoutSeconds),
            QueueCapacity = Math.Max(1, Get("QUEUE_CAPACITY", d.QueueCapacity)),
            ProfilesDir = c["PROFILES_DIR"] ?? d.ProfilesDir,
            ApiKeys = c["API_KEYS"],
            ApiKeysFile = c["API_KEYS_FILE"],
            ErrorsVerbose = Get("ERRORS_VERBOSE", false),
            ApiAuthDisabled = Get("API_AUTH_DISABLED", false),
            AllowedHosts = (c["ALLOWED_HOSTS"] ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            BrowseConcurrency = Math.Clamp(Get("BROWSE_CONCURRENCY", d.BrowseConcurrency), 1, 64),
            BrowseQueueCapacity = Math.Clamp(Get("BROWSE_QUEUE_CAPACITY", d.BrowseQueueCapacity), 0, 1000),
            BrowseWaitSeconds = Math.Clamp(Get("BROWSE_WAIT_SECONDS", d.BrowseWaitSeconds), 1, 300),
            ConversionStuckGraceSeconds = Math.Clamp(Get("CONVERSION_STUCK_GRACE_SECONDS", 0), 0, 86400),
            DocsEnabled = Get("DOCS_ENABLED", true),
            MetricsEnabled = Get("METRICS_ENABLED", false),
            MetricsPort = Get<int?>("METRICS_PORT", (int?)null),
            RateLimitPerMinute = Get("RATE_LIMIT_PER_MINUTE", 0),
            MaxCues = Get("MAX_CUES", d.MaxCues),
            MaxLineChars = Get("MAX_LINE_CHARS", d.MaxLineChars),
            S3Endpoint = c["S3_ENDPOINT"],
            S3Region = c["S3_REGION"] ?? d.S3Region,
            S3ForcePathStyle = Get("S3_FORCE_PATH_STYLE", false),
            S3AllowedBuckets = List(c["S3_ALLOWED_BUCKETS"]),
            S3AllowedPrefixes = List(c["S3_ALLOWED_PREFIXES"]),
            HttpAllowedHosts = List(c["HTTP_ALLOWED_HOSTS"]),
            IoTimeoutSeconds = Math.Clamp(Get("IO_TIMEOUT_SECONDS", d.IoTimeoutSeconds), 1, 300),
            IoRetries = Math.Clamp(Get("IO_RETRIES", d.IoRetries), 0, 10),
            StorageFileRoots = Roots(c["STORAGE_FILE_ROOTS"]),
            StorageFileAtomic = Get("STORAGE_FILE_ATOMIC", false),
            StorageListEnabled = Get("STORAGE_LIST_ENABLED", true),
            RequestWaitSeconds = Math.Clamp(Get("REQUEST_WAIT_SECONDS", d.RequestWaitSeconds), 0, 60),
            JobTtlSeconds = Math.Clamp(Get("JOB_TTL_SECONDS", d.JobTtlSeconds), 1, 86400),
            JobMaxEntries = Math.Clamp(Get("JOB_MAX_ENTRIES", d.JobMaxEntries), 1, 10000),
            ProfileMaxBytes = Math.Max(1024, Get("PROFILE_MAX_BYTES", d.ProfileMaxBytes)),
            KnownProxies = List(c["KNOWN_PROXIES"]),
        };
        // Filesystem storage needs per-architecture open(2) flags; an unknown architecture fails startup instead of guessing.
        if (o.StorageFileRoots.Length > 0 && OperatingSystem.IsLinux() && Storage.LinuxOpenFlags.For(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture) == null)
            throw new InvalidOperationException(Storage.LinuxOpenFlags.Unsupported(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture));
        return o;
    }
}
