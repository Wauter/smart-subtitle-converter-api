using System.Net;
using System.Net.Sockets;
using SubtitleEditConverterApi.Infrastructure;

namespace SubtitleEditConverterApi.Storage;

public sealed class HttpStorage : IStorage, IDisposable
{
    private readonly ApiOptions _opts;
    private readonly HttpClient _client;
    public HttpStorage(ApiOptions opts) : this(opts, new HttpClient(CreateHandler(opts))) { }
    // Explicit transport injection for deterministic tests; production always uses CreateHandler.
    public HttpStorage(ApiOptions opts, HttpClient client) { _opts = opts; _client = client; _client.Timeout = Timeout.InfiniteTimeSpan; }
    public static SocketsHttpHandler CreateHandler(ApiOptions opts) => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectCallback = async (context, ct) =>
        {
            CheckHost(context.DnsEndPoint.Host, opts);
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            CheckAddresses(addresses);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Use these checked IPs, never re-resolve the hostname before connecting. TLS keeps the original host.
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };
    private static void CheckHost(string host, ApiOptions opts)
    {
        host = host.Trim('[', ']').ToLowerInvariant();
        if (host.EndsWith('.') || host is "localhost" or "metadata" or "instance-data" || host.EndsWith(".localhost") || host == "metadata.google.internal"
            || !opts.HttpAllowedHosts.Any(h => h.Equals(host, StringComparison.OrdinalIgnoreCase)))
            throw StorageLocation.Rejected("HTTPS host is not in HTTP_ALLOWED_HOSTS, or is a local/metadata hostname.");
    }
    public static void CheckAddresses(IPAddress[] addresses)
    {
        if (addresses.Length == 0 || addresses.Any(IsBlocked))
            throw StorageLocation.Rejected("HTTPS destinations must resolve only to public unicast addresses, not local, private, reserved or metadata addresses.");
    }
    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (b[0] & 0xe0) != 0x20 || (b[0] == 0x20 && b[1] == 0x02) // block 6to4 tunnels
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) // Teredo
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
        return b[0] is 0 or 10 or 127 || b[0] >= 224
            || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127)
            || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
            || (b[0] == 198 && b[1] is 18 or 19) || (b[0] == 198 && b[1] == 51 && b[2] == 100)
            || (b[0] == 203 && b[1] == 0 && b[2] == 113)
            || address.Equals(IPAddress.Parse("168.63.129.16"));
    }
    private Uri Checked(StorageLocation location)
    {
        var uri = location.Uri!;
        if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw StorageLocation.Rejected("Only HTTPS URLs without credentials or fragments are accepted.");
        CheckHost(uri.IdnHost, _opts);
        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var ip)) CheckAddresses([ip]);
        return uri;
    }
    public Task<byte[]> ReadAsync(StorageLocation location, CancellationToken ct) => Transfer(location, HttpMethod.Get, null, null, ct);
    public async Task WriteAsync(StorageLocation location, byte[] bytes, string contentType, CancellationToken ct, bool replace = false)
    {
        if (bytes.LongLength > _opts.MaxUploadBytes) throw Problems.TooLarge(_opts);
        await Transfer(location, HttpMethod.Put, bytes, contentType, ct);
    }
    public async Task<bool> ExistsAsync(StorageLocation location, CancellationToken ct)
    {
        try { await Transfer(location, HttpMethod.Head, null, null, ct); return true; }
        catch (ApiException ex) when (ex.Code == "storage-not-found") { return false; }
    }
    private sealed class Transient(int status, int retryAfter = 0) : Exception { public int Status = status; public int RetryAfter = retryAfter; }
    private async Task<byte[]> Transfer(StorageLocation location, HttpMethod method, byte[]? bytes, string? contentType, CancellationToken ct)
    {
        var uri = Checked(location);
        for (var attempt = 0; ; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(_opts.IoTimeoutSeconds));
            try
            {
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.AcceptEncoding.ParseAdd("identity");
                if (bytes != null)
                {
                    request.Content = new ByteArrayContent(bytes);
                    request.Content.Headers.ContentType = new(contentType!);
                }
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var status = (int)response.StatusCode;
                if (status is 408 or 429 or 500 or 502 or 503 or 504)
                    throw new Transient(status, (int)Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0, 0, 10));
                if (status is >= 300 and < 400) throw StorageLocation.Rejected("HTTPS redirects are not followed; provide the final allowlisted URL.");
                if (status == 404) throw new ApiException(404, "storage-not-found", "HTTPS resource was not found (404).", "Check the URL and its expiry.");
                if (status is not (200 or 201 or 204) || (method == HttpMethod.Get && status != 200))
                {
                    var error = new ApiException(502, "storage-io-failed", $"HTTPS {method} returned {status}.", "Check URL permissions and expiry; 403 is not retried.");
                    error.Extensions["upstreamStatus"] = status; throw error;
                }
                if (method != HttpMethod.Get) return [];
                if (response.Content.Headers.ContentEncoding.Count != 0) throw StorageLocation.Rejected("Encoded HTTPS bodies are not accepted; serve the raw subtitle bytes.");
                if (response.Content.Headers.ContentLength > _opts.MaxUploadBytes) throw Problems.TooLarge(_opts);
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                return await StorageRouter.ReadCapped(stream, _opts.MaxUploadBytes, deadline.Token);
            }
            catch (HttpRequestException ex) when (FindRejection(ex) is { } rejection) { throw rejection; }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
            { throw new ApiException(502, "storage-io-failed", "HTTPS certificate or TLS validation failed.", "Check the destination certificate and hostname; this failure is not retried."); }
            catch (Exception ex) when (ex is Transient or HttpRequestException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= _opts.IoRetries)
                    throw new ApiException(502, "storage-io-failed", $"HTTPS {method} failed after {attempt + 1} attempts.", "A transfer timeout, connection error or transient HTTP status exhausted IO_RETRIES. Check the destination before resubmitting a PUT.");
                var wait = ex is Transient t && t.RetryAfter > 0 ? t.RetryAfter * 1000 : 250 * (1 << attempt) + Random.Shared.Next(100);
                await Task.Delay(wait, ct);
            }
        }
    }
    private static ApiException? FindRejection(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
            if (current is ApiException api && api.Code == "storage-location-rejected") return api;
        return null;
    }
    public void Dispose() => _client.Dispose();
}
