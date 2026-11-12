namespace SmartSubtitleConverterApi.Infrastructure;

/// <summary>
/// Browser-access protection (H1, DNS rebinding and cross-site requests). The Host header must name an allowed host:
/// localhost, 127.0.0.1 and [::1] always, plus ALLOWED_HOSTS ("*" accepts any Host). A request with an Origin header is
/// accepted only when the Origin's host and port match the request's own Host (scheme is ignored; a missing port defaults
/// to 80 or 443) and that host is allowed. The API sends no CORS headers: a browser front end should not hold an API key.
/// Requests without Origin (curl, SDKs, the MCP) are unaffected. Health probes
/// (/healthz, /readyz, /metrics) are exempt: orchestrators call them by pod IP and they expose no data.
/// </summary>
public sealed class HostOriginFilter
{
    public static readonly string[] AlwaysAllowed = ["localhost", "127.0.0.1", "[::1]"];
    private readonly HashSet<string> _hosts;
    public bool AnyHost { get; }
    public IReadOnlyCollection<string> Effective => _hosts;

    public HostOriginFilter(ApiOptions opts)
    {
        AnyHost = opts.AllowedHosts.Contains("*");
        _hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in AlwaysAllowed.Concat(opts.AllowedHosts.Where(h => h != "*"))) _hosts.Add(Normalize(h));
    }

    /// <summary>Lower-cased host without port; IPv6 in brackets; a trailing dot removed.</summary>
    public static string Normalize(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        if (h.StartsWith('['))
        {
            var close = h.IndexOf(']');
            return close > 0 ? h[..(close + 1)] : h;
        }
        if (h.Count(c => c == ':') > 1) return "[" + h + "]"; // bare IPv6 literal
        var colon = h.IndexOf(':');
        if (colon >= 0) h = h[..colon];
        return h.TrimEnd('.');
    }

    public bool HostAllowed(string? host) => AnyHost || (!string.IsNullOrEmpty(host) && _hosts.Contains(Normalize(host)));

    /// <summary>Host and port from a Host header; a missing port is 443 when <paramref name="scheme"/> is https, otherwise 80.</summary>
    public static bool TryHostPort(string? hostHeader, string? scheme, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrEmpty(hostHeader)) return false;
        var hs = new HostString(hostHeader);
        if (string.IsNullOrEmpty(hs.Host)) return false;
        host = Normalize(hs.Host);
        port = hs.Port ?? (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        return true;
    }

    /// <summary>
    /// True when <paramref name="origin"/> is this request's own origin: same host and port as <paramref name="requestHost"/>
    /// (scheme is ignored; default ports 80/443) and that host is allowed.
    /// </summary>
    public bool OriginAllowed(string origin, string? requestHost, string? requestScheme = "http")
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0) return false; // "null", file:, garbage
        if (!TryHostPort(requestHost, requestScheme, out var reqHost, out var reqPort)) return false;
        var host = Normalize(u.HostNameType == UriHostNameType.IPv6 ? u.Host : u.IdnHost);
        if (host != reqHost || u.Port != reqPort) return false;
        return AnyHost || _hosts.Contains(host);
    }

    public static bool Exempt(PathString path) =>
        path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) || path.Equals("/readyz", StringComparison.OrdinalIgnoreCase) || path.Equals("/metrics", StringComparison.OrdinalIgnoreCase);

    public void Log(ILogger log)
    {
        if (AnyHost)
        {
            log.LogWarning("{Line}", "security.hosts WARNING ALLOWED_HOSTS=* : any Host header is accepted, which removes the DNS-rebinding protection. "
                + "Only use this behind a reverse proxy that already checks the Host. Browser requests (with an Origin header) are still only accepted when the Origin's host and port match the Host they were sent to.");
            return;
        }
        log.LogInformation("{Line}", $"security.hosts allowed={string.Join(",", _hosts)} (requests with any other Host header get 400 host-not-allowed; "
            + "add the name or IP clients use, the compose service name of this API, or your reverse proxy's public hostname to ALLOWED_HOSTS, comma separated)");
        log.LogInformation("{Line}", "security.origin requests with an Origin header are accepted only when the Origin's host and port match this request's Host and that host is allowed; no CORS headers are sent; other browser origins get 403 origin-not-allowed. Requests without Origin (curl, SDKs, the MCP) are unaffected.");
    }
}

public sealed class HostOriginMiddleware(RequestDelegate next, HostOriginFilter filter, ILogger<HostOriginMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!HostOriginFilter.Exempt(ctx.Request.Path))
        {
            var host = ctx.Request.Host.HasValue ? ctx.Request.Host.Value : null;
            if (!filter.HostAllowed(host))
            {
                var shown = Sanitize(host);
                log.LogWarning("security.host-rejected host={host} path={path}", shown, LogSafe.Clean(ctx.Request.Path.Value));
                throw new ApiException(400, ErrorCodes.HostNotAllowed, $"The Host header '{shown}' is not an allowed host for this API.",
                    $"Call the API by an allowed name ({string.Join(", ", filter.Effective)}), or ask the operator to add '{HostOriginFilter.Normalize(host ?? "")}' to ALLOWED_HOSTS (comma separated) and restart the API.");
            }
            var origin = ctx.Request.Headers.Origin.ToString();
            if (ctx.Request.Headers.ContainsKey("Origin") && !filter.OriginAllowed(origin, host, ctx.Request.Scheme))
            {
                var shown = Sanitize(origin);
                log.LogWarning("security.origin-rejected origin={origin} method={method} path={path}", shown, LogSafe.Clean(ctx.Request.Method), LogSafe.Clean(ctx.Request.Path.Value));
                throw new ApiException(403, ErrorCodes.OriginNotAllowed, $"Requests from the web page origin '{shown}' are not accepted.",
                    "This API refuses browser requests from other sites, to protect your files. Call it from a server-side client (curl, an SDK, the MCP) without an Origin header, "
                    + "or open the page from the same host and port the API was called on (see ALLOWED_HOSTS).");
            }
        }
        await next(ctx);
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var s = new string(value.Select(c => c < 32 || c > 126 ? '?' : c).ToArray());
        return s.Length > 200 ? s[..200] + "..." : s;
    }
}
