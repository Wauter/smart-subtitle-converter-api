namespace SmartSubtitleConverterApi.Infrastructure;

/// <summary>
/// Browser-access protection (H1, DNS rebinding and cross-site requests). The Host header must name an allowed host:
/// localhost, 127.0.0.1 and [::1] always, plus ALLOWED_HOSTS ("*" accepts any Host). A request with an Origin header is
/// accepted only when the Origin's host is in that same list (the API's own origin). The API sends no CORS headers:
/// a browser front end should not hold an API key. Requests without Origin (curl, SDKs, the MCP) are unaffected. Ports are not compared. Health probes
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

    /// <summary>True when <paramref name="origin"/> is our own origin: its host is allowed (with ALLOWED_HOSTS=*, it must equal the request's Host).</summary>
    public bool OriginAllowed(string origin, string? requestHost)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0) return false; // "null", file:, garbage
        var host = Normalize(u.HostNameType == UriHostNameType.IPv6 ? u.Host : u.IdnHost);
        if (AnyHost) return requestHost != null && host == Normalize(requestHost);
        return _hosts.Contains(host);
    }

    public static bool Exempt(PathString path) =>
        path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) || path.Equals("/readyz", StringComparison.OrdinalIgnoreCase) || path.Equals("/metrics", StringComparison.OrdinalIgnoreCase);

    public void Log(ILogger log)
    {
        if (AnyHost)
        {
            log.LogWarning("{Line}", "security.hosts WARNING ALLOWED_HOSTS=* : any Host header is accepted, which removes the DNS-rebinding protection. "
                + "Only use this behind a reverse proxy that already checks the Host. Browser requests (with an Origin header) are still only accepted from the same host they were sent to.");
            return;
        }
        log.LogInformation("{Line}", $"security.hosts allowed={string.Join(",", _hosts)} (requests with any other Host header get 400 host-not-allowed; "
            + "add the name or IP clients use, the compose service name of this API, or your reverse proxy's public hostname to ALLOWED_HOSTS, comma separated)");
        log.LogInformation("{Line}", "security.origin requests with an Origin header are accepted only from an allowed host above; no CORS headers are sent; other browser origins get 403 origin-not-allowed. Requests without Origin (curl, SDKs, the MCP) are unaffected.");
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
                log.LogWarning("security.host-rejected host={host} path={path}", shown, ctx.Request.Path.Value);
                throw new ApiException(400, ErrorCodes.HostNotAllowed, $"The Host header '{shown}' is not an allowed host for this API.",
                    $"Call the API by an allowed name ({string.Join(", ", filter.Effective)}), or ask the operator to add '{HostOriginFilter.Normalize(host ?? "")}' to ALLOWED_HOSTS (comma separated) and restart the API.");
            }
            var origin = ctx.Request.Headers.Origin.ToString();
            if (ctx.Request.Headers.ContainsKey("Origin") && !filter.OriginAllowed(origin, host))
            {
                var shown = Sanitize(origin);
                log.LogWarning("security.origin-rejected origin={origin} method={method} path={path}", shown, ctx.Request.Method, ctx.Request.Path.Value);
                throw new ApiException(403, ErrorCodes.OriginNotAllowed, $"Requests from the web page origin '{shown}' are not accepted.",
                    "This API refuses browser requests from other sites, to protect your files. Call it from a server-side client (curl, an SDK, the MCP) without an Origin header, "
                    + "or open the page from an allowed host (see ALLOWED_HOSTS).");
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
