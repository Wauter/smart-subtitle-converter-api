using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SmartSubtitleConverterApi.Infrastructure;

/// <summary>
/// Assigns X-Request-Id (honours a valid incoming one), sets the always-present response headers,
/// and writes one structured access-log record per request (design §7.2). Never logs content,
/// file names or keys.
/// </summary>
public sealed partial class RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> log)
{
    private const string Key = "requestId";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ValidId();

    public static string Get(HttpContext ctx) => ctx.Items[Key] as string ?? "unknown";

    public static void ApplyHeaders(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Request-Id"] = Get(ctx);
        if (ctx.Items["jobId"] is string jobId)
        {
            h["X-Job-Id"] = jobId;
            h.CacheControl = "no-store";
        }
        h["X-Libse-Version"] = LibseInfo.Version;
        h.XContentTypeOptions = "nosniff";
        if (!ctx.Request.Path.StartsWithSegments("/docs"))
        {
            h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
        }
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var incoming = ctx.Request.Headers["X-Request-Id"].ToString();
        var id = ValidId().IsMatch(incoming) ? incoming : Ulid.New();
        ctx.Items[Key] = id;
        ctx.Response.OnStarting(() => { ApplyHeaders(ctx); return Task.CompletedTask; });

        var sw = Stopwatch.StartNew();
        using var scope = log.BeginScope(new Dictionary<string, object> { [Key] = LogSafe.Clean(id) ?? "" });
        try
        {
            await next(ctx);
        }
        finally
        {
            sw.Stop();
            var path = ctx.Request.Path.Value ?? "";
            if (path is not "/healthz" and not "/readyz" and not "/metrics")
            {
                var items = ctx.Items;
                log.LogInformation(
                    "request {method} {route} {status} {durationMs}ms queueWaitMs={queueWaitMs} bytesIn={bytesIn} bytesOut={bytesOut} format={format} encoding={encoding} cues={cues} preset={preset} operations={operations} keyName={keyName} file={file} code={code}",
                    LogSafe.Clean(ctx.Request.Method), LogSafe.Clean((ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? path),
                    ctx.Response.StatusCode, sw.ElapsedMilliseconds,
                    LogSafe.Number(items["queueWaitMs"]), ctx.Request.ContentLength, ctx.Response.ContentLength ?? LogSafe.Number(items["bytesOut"]),
                    LogSafe.Clean(items["format"]), LogSafe.Clean(items["encoding"]), LogSafe.Number(items["cues"]), LogSafe.Clean(items["preset"]),
                    LogSafe.Clean(items["operations"]), LogSafe.Clean(items["keyName"]), LogSafe.Clean(items["fileHash"]), LogSafe.Clean(items["errorCode"]));
            }
        }
    }

    /// <summary>Logs file names only as a short hash plus extension (design §7.2).</summary>
    public static string HashFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "-";
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..12].ToLowerInvariant();
        return hash + Path.GetExtension(name).ToLowerInvariant();
    }
}

/// <summary>Minimal ULID generator (48-bit ms timestamp + 80 random bits, Crockford base32).</summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--) { bytes[i] = (byte)(ms & 0xFF); ms >>= 8; }
        RandomNumberGenerator.Fill(bytes[6..]);
        var value = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var chars = new char[26];
        for (var i = 25; i >= 0; i--) { chars[i] = Alphabet[(int)(value % 32)]; value /= 32; }
        return new string(chars);
    }
}

public static class LibseInfo
{
    /// <summary>The libse NuGet package version (the assembly version lags it), stamped at build time.</summary>
    public static readonly string Version =
        typeof(LibseInfo).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LibseVersion")?.Value ?? "unknown";

    public static readonly string AppVersion =
        typeof(LibseInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>
    /// Short git commit (7 hex characters) the image was built from: the Dockerfile passes the REVISION build argument
    /// to dotnet publish as assembly metadata. "unknown" for local builds or anything that isn't a commit hash.
    /// </summary>
    public static readonly string Revision = ShortRevision(
        typeof(LibseInfo).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SourceRevision")?.Value);

    internal static string ShortRevision(string? value) =>
        value is { Length: >= 7 and <= 64 } && value.All(char.IsAsciiHexDigit) ? value[..7].ToLowerInvariant() : "unknown";
}
