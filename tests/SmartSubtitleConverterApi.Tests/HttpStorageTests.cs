using System.Net;
using System.Text;
using SmartSubtitleConverterApi.Infrastructure;
using SmartSubtitleConverterApi.Storage;
namespace SmartSubtitleConverterApi.Tests;
public class HttpStorageTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private static ApiOptions Options(long max = 1024) => new() { HttpAllowedHosts = ["mam.example.com"], MaxUploadBytes = max, IoRetries = 1, IoTimeoutSeconds = 1 };
    [Theory]
    [InlineData("127.0.0.1")][InlineData("169.254.169.254")][InlineData("168.63.129.16")][InlineData("10.0.0.1")][InlineData("::1")][InlineData("::ffff:127.0.0.1")][InlineData("fd00::1")][InlineData("fe80::1")][InlineData("64:ff9b::7f00:1")][InlineData("0.0.0.0")][InlineData("100.100.100.200")]
    public void Blocks_local_reserved_and_metadata(string ip) => Assert.True(HttpStorage.IsBlocked(IPAddress.Parse(ip)));
    [Fact]
    public void Rejects_mixed_dns_answers_and_disables_proxy_redirects_and_cookies()
    {
        Assert.Throws<ApiException>(() => HttpStorage.CheckAddresses([IPAddress.Parse("1.1.1.1"), IPAddress.Loopback]));
        using var handler = HttpStorage.CreateHandler(Options());
        Assert.False(handler.UseProxy); Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseCookies); Assert.NotNull(handler.ConnectCallback);
        Assert.False(HttpStorage.IsBlocked(IPAddress.Parse("2606:4700:4700::1111")));
    }
    [Fact]
    public async Task Gets_raw_bytes_puts_exact_bytes_and_retries_transient_only()
    {
        var calls = 0;
        using var http = new HttpStorage(Options(), new HttpClient(new Handler(async (req, ct) =>
        {
            calls++;
            Assert.Null(req.Headers.Authorization);
            if (calls == 1) return new(HttpStatusCode.ServiceUnavailable);
            if (req.Method == HttpMethod.Put) Assert.Equal(Samples.Srt, await req.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent(Samples.Srt, Encoding.UTF8, "application/pdf") };
        })));
        var uri = StorageLocation.Parse("https://mam.example.com/sub.srt?signature=secret");
        Assert.Equal(Samples.Srt, Encoding.UTF8.GetString(await http.ReadAsync(uri, default)));
        await http.WriteAsync(uri, Encoding.UTF8.GetBytes(Samples.Srt), "text/plain", default);
        Assert.Equal(3, calls);
    }
    [Theory][InlineData(403)][InlineData(404)][InlineData(302)]
    public async Task Nontransient_status_is_not_retried_or_redirected(int status)
    {
        var calls = 0;
        using var http = new HttpStorage(Options(), new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)); })));
        var ex = await Assert.ThrowsAsync<ApiException>(() => http.ReadAsync(StorageLocation.Parse("https://mam.example.com/a?secret=1"), default));
        Assert.Equal(1, calls); Assert.DoesNotContain("secret", ex.Message);
    }
    [Fact]
    public async Task Size_cap_without_content_length_and_host_rejection()
    {
        var calls = 0;
        using var http = new HttpStorage(Options(3), new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[4])) }); })));
        Assert.Equal(413, (await Assert.ThrowsAsync<ApiException>(() => http.ReadAsync(StorageLocation.Parse("https://mam.example.com/a"), default))).Status);
        Assert.Equal(422, (await Assert.ThrowsAsync<ApiException>(() => http.ReadAsync(StorageLocation.Parse("https://other.example.com/a"), default))).Status);
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task Certificate_failure_is_not_retried()
    {
        var calls = 0;
        using var http = new HttpStorage(Options(), new HttpClient(new Handler((_, _) => { calls++; throw new HttpRequestException(HttpRequestError.SecureConnectionError, "certificate"); })));
        await Assert.ThrowsAsync<ApiException>(() => http.ReadAsync(StorageLocation.Parse("https://mam.example.com/a"), default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Each_attempt_has_a_timeout()
    {
        var calls = 0;
        using var http = new HttpStorage(Options(), new HttpClient(new Handler(async (_, ct) => { calls++; await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); })));
        Assert.Equal(502, (await Assert.ThrowsAsync<ApiException>(() => http.ReadAsync(StorageLocation.Parse("https://mam.example.com/a"), default))).Status);
        Assert.Equal(2, calls);
    }
}
