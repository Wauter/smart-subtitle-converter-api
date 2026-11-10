using System.Security.Cryptography;
using System.Text;

namespace SmartSubtitleConverterApi.Infrastructure;

/// <summary>
/// Bearer API keys (design §6.1), required by default. Keys come from API_KEYS (comma separated) and/or API_KEYS_FILE
/// (one per line, '#' comments, optional 'name:key'). Only SHA-256 hashes are kept in memory; comparison is constant time.
/// The file is re-read when its mtime changes. API_AUTH_DISABLED=true turns auth off. When the key file has no key,
/// <see cref="ApiKeyBootstrap"/> generates one into it at startup.
/// </summary>
public sealed class ApiKeyStore
{
    private readonly ApiOptions _opts;
    private readonly ILogger<ApiKeyStore> _log;
    private readonly Lock _lock = new();
    private IReadOnlyList<(string Name, byte[] Hash)> _keys = [];
    private DateTime _fileMtime = DateTime.MinValue;

    public ApiKeyStore(ApiOptions opts, ILogger<ApiKeyStore> log)
    {
        _opts = opts;
        _log = log;
        Reload(force: true);
    }

    public bool Enabled => !_opts.ApiAuthDisabled;
    public int Count => _keys.Count;

    private void Reload(bool force)
    {
        var path = _opts.ApiKeysFile;
        var mtime = !string.IsNullOrEmpty(path) && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (!force && mtime == _fileMtime) return;

        lock (_lock)
        {
            var list = new List<(string, byte[])>();
            foreach (var k in (_opts.ApiKeys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                list.Add(Parse(k));
            }
            if (!string.IsNullOrEmpty(path) && (File.Exists(path) || _opts.ApiKeys is not { Length: > 0 }))
            {
                try
                {
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith('#')) continue;
                        list.Add(Parse(line));
                    }
                    if (!force) _log.LogWarning("auth.keys-reloaded from file ({count} keys)", list.Count);
                }
                catch (Exception ex)
                {
                    _log.LogError("auth.keys-file-unreadable: {error}", ex.Message);
                }
            }
            _keys = list;
            _fileMtime = mtime;
        }
    }

    private static (string, byte[]) Parse(string entry)
    {
        var idx = entry.IndexOf(':');
        var (name, key) = idx > 0 ? (entry[..idx], entry[(idx + 1)..]) : (null, entry);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return (name ?? "key-" + Convert.ToHexString(hash)[..8].ToLowerInvariant(), hash);
    }

    /// <summary>Returns the key name, or null when the key is not valid.</summary>
    public string? Validate(string presented)
    {
        Reload(force: false);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        string? match = null;
        foreach (var (name, h) in _keys)
        {
            if (CryptographicOperations.FixedTimeEquals(h, hash)) match ??= name;
        }
        return match;
    }
}

public sealed class ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx, ApiKeyStore keys)
    {
        if (!keys.Enabled || !ctx.Request.Path.StartsWithSegments("/v1"))
        {
            await next(ctx);
            return;
        }
        var header = ctx.Request.Headers.Authorization.ToString();
        string? name = null;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            name = keys.Validate(header[7..].Trim());
        }
        if (name == null)
        {
            var reason = header.Length == 0 ? "missing" : "invalid";
            var prefix = header.Length > 7
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(header[7..].Trim())))[..8].ToLowerInvariant()
                : "-";
            log.LogWarning("auth.rejected reason={reason} keyHashPrefix={prefix}", reason, prefix);
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            throw new ApiException(401, ErrorCodes.Unauthorized, $"API key {reason}.", "Send `Authorization: Bearer <key>`.");
        }
        ctx.Items["keyName"] = name;
        await next(ctx);
    }
}

/// <summary>Thrown when startup can't continue because of the API key configuration; the message is the operator-facing explanation.</summary>
public sealed class StartupConfigException(string message) : Exception(message);

/// <summary>
/// Startup check for API keys (H1). Auth is on unless API_AUTH_DISABLED=true. With no key anywhere, the key file
/// (API_KEYS_FILE, /api_keys.txt in the container image) must exist as a writable regular file: a strong random key is
/// written into it in place (it may be a single bind-mounted file, so no rename) and the file is set to 0600.
/// The key itself is never logged, not even masked.
/// </summary>
public static class ApiKeyBootstrap
{
    public const string HostFileExample = "./api_keys.txt";

    public static int CountKeys(string? envKeys, IEnumerable<string> fileLines) =>
        (envKeys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length
        + fileLines.Select(l => l.Trim()).Count(l => l.Length > 0 && !l.StartsWith('#'));

    /// <summary>A new key: "ssc_" + 32 random bytes, base64url (no ':' so it is never read as name:key).</summary>
    public static string NewKey() => "ssc_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Validates the key setup and generates a key when needed. Logs the outcome; throws <see cref="StartupConfigException"/> when startup must stop.</summary>
    public static void Ensure(ApiOptions opts, ILogger log)
    {
        var storage = opts.StorageFileRoots.Length > 0 || opts.S3AllowedBuckets.Length > 0 || opts.HttpAllowedHosts.Length > 0;
        if (opts.ApiAuthDisabled)
        {
            var banner = new string('!', 100);
            log.LogWarning("{Line}", banner);
            log.LogWarning("{Line}", "auth.disabled API_AUTH_DISABLED=true: every /v1 endpoint is open to anyone who can reach this port. No API key is checked. "
                + "Use this only for throwaway local experiments. Fix: remove API_AUTH_DISABLED (or set it to false) and mount an api_keys.txt (see docs/security.md, Securing the API).");
            if (storage)
                log.LogWarning("{Line}", "auth.disabled DANGER: storage is configured (STORAGE_FILE_ROOTS / S3_ALLOWED_BUCKETS / HTTP_ALLOWED_HOSTS), so anyone who can reach this port can read and write those locations. "
                    + "Turn auth back on before you use this API with real files.");
            log.LogWarning("{Line}", banner);
            return;
        }
        var envCount = CountKeys(opts.ApiKeys, []);
        var path = opts.ApiKeysFile;
        if (string.IsNullOrWhiteSpace(path))
        {
            if (envCount > 0) { log.LogInformation("{Line}", $"auth.enabled keys={envCount} source=API_KEYS (requests to /v1 need Authorization: Bearer <key>)"); return; }
            throw new StartupConfigException(
                "No API key is configured and API_KEYS_FILE is not set, so the API can't start safely.\n"
                + "  Why: the API requires a key by default, so web pages and other programs can't use it without permission.\n"
                + "  Fix (recommended): create an empty file on the host (touch api_keys.txt), mount it read-write at /api_keys.txt and set API_KEYS_FILE=/api_keys.txt. "
                + "On first start the API writes a new key into that file.\n"
                + "  Or: put your own key in that file (one per line), or set API_KEYS=<key>.\n"
                + "  Or, only for throwaway local tests: set API_AUTH_DISABLED=true.");
        }
        if (envCount > 0 && !File.Exists(path) && !Directory.Exists(path))
        {
            log.LogInformation("{Line}", $"auth.enabled keys={envCount} source=API_KEYS (API_KEYS_FILE={path} is not present, so only API_KEYS is used; requests to /v1 need Authorization: Bearer <key>)");
            return;
        }
        if (Directory.Exists(path))
            throw new StartupConfigException(
                $"API_KEYS_FILE={path} is a directory, not a file.\n"
                + "  Why: this usually means the file did not exist on the host when the container started, so Docker created an empty directory with that name instead of mounting a file.\n"
                + $"  Fix: stop the container, remove that directory on the host (rmdir {HostFileExample}), create an empty file (touch {HostFileExample}), "
                + $"make it writable for the container user (UID 1654), for example: sudo chown 1654 {HostFileExample} && chmod 600 {HostFileExample}, then start again. "
                + $"The volume line should look like: - {HostFileExample}:{path}");
        if (!File.Exists(path))
            throw new StartupConfigException(
                $"API_KEYS_FILE={path} does not exist.\n"
                + "  Why: the API requires an API key by default and reads it from this file.\n"
                + $"  Fix: create an empty file on the host (touch {HostFileExample}), make it writable for the container user (UID 1654): sudo chown 1654 {HostFileExample} && chmod 600 {HostFileExample}, "
                + $"and mount it read-write: - {HostFileExample}:{path}. On first start the API writes a new key into it. "
                + "Or put your own key in it, or set API_KEYS=<key>, or (only for throwaway local tests) API_AUTH_DISABLED=true.");
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StartupConfigException(
                $"API_KEYS_FILE={path} can't be read ({ex.GetType().Name}).\n"
                + $"  Fix: make the file readable for the container user (UID 1654), for example: sudo chown 1654 {HostFileExample} && chmod 600 {HostFileExample}.");
        }
        var fileCount = CountKeys(null, lines);
        if (fileCount + envCount > 0)
        {
            log.LogInformation("{Line}", $"auth.enabled keys={fileCount + envCount} fromFile={fileCount} fromApiKeys={envCount} file={path} (requests to /v1 need Authorization: Bearer <key>; the file is re-read when it changes)");
            return;
        }
        // No key anywhere: generate one into the existing file, in place.
        var key = NewKey();
        try
        {
            // FileMode.Open: never create (a missing file is reported above) and never replace the inode (bind mounts).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.Seek(0, SeekOrigin.End);
            var prefix = stream.Length > 0 && !EndsWithNewline(stream) ? "\n" : "";
            stream.Seek(0, SeekOrigin.End);
            var text = prefix
                + $"# Generated by smart-subtitle-converter-api on {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC because this file had no key.\n"
                + "# Clients send it as: Authorization: Bearer <key>. Keep this file private. Add more keys one per line (optional name:key).\n"
                + key + "\n";
            stream.Write(Encoding.UTF8.GetBytes(text));
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StartupConfigException(
                $"API_KEYS_FILE={path} has no key and the API could not write a new one into it ({ex.GetType().Name}).\n"
                + "  Why: when the key file is empty the API generates a key into it on first start, which needs write access.\n"
                + $"  Fix: mount the file read-write (no :ro on the volume line, not as a Docker secret) and make it writable for the container user (UID 1654): sudo chown 1654 {HostFileExample} && chmod 600 {HostFileExample}. "
                + "Or put your own key in the file yourself (one per line) and keep the read-only mount.");
        }
        var modeNote = "";
        try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { modeNote = " (could not set the file to owner-only 0600; do it on the host: chmod 600 " + HostFileExample + ")"; }
        log.LogWarning("{Line}", new string('=', 100));
        log.LogWarning("{Line}", $"auth.key-generated No API key was configured, so a new random key was generated and written to {path} inside the container{modeNote}. The key is not printed here.");
        log.LogWarning("{Line}", $"auth.key-generated Where to find it: in the file you mounted at {path} (for example {HostFileExample} next to docker-compose.yml). Open it on the host; the key is the line that does not start with '#'.");
        log.LogWarning("{Line}", "auth.key-generated Clients send it as the header: Authorization: Bearer <key>.");
        log.LogWarning("{Line}", "auth.key-generated MCP server: mount the same file read-only and set SUBTITLE_API_KEY_FILE=<path of the file inside the MCP container>, or set SUBTITLE_API_KEY=<key>. Restart the MCP after changing it.");
        log.LogWarning("{Line}", "auth.key-generated Postman: set the apiKey environment variable to the key.");
        log.LogWarning("{Line}", new string('=', 100));
    }

    private static bool EndsWithNewline(FileStream s)
    {
        s.Seek(-1, SeekOrigin.End);
        return s.ReadByte() == '\n';
    }
}
