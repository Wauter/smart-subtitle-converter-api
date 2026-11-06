using System.Security.Cryptography;
using System.Text;

namespace SmartSubtitleConverterApi.Infrastructure;

/// <summary>
/// Optional bearer API keys (design §6.1). Keys come from API_KEYS (comma separated) and/or
/// API_KEYS_FILE (one per line, '#' comments, optional 'name:key'). Only SHA-256 hashes are kept
/// in memory; comparison is constant time. The file is re-read when its mtime changes.
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
        if (!Enabled)
        {
            log.LogWarning("auth.disabled: no API keys configured; /v1 endpoints are open");
        }
        else
        {
            log.LogInformation("auth.enabled with {count} key(s)", _keys.Count);
        }
    }

    public bool Enabled => _keys.Count > 0 || !string.IsNullOrEmpty(_opts.ApiKeysFile);

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
            if (!string.IsNullOrEmpty(path))
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
