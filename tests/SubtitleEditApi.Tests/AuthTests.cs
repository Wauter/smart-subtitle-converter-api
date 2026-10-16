using System.Net;
using System.Net.Http.Headers;

namespace SubtitleEditApi.Tests;

public class AuthTests : IDisposable
{
    private readonly string _keyFile = Path.GetTempFileName();
    public void Dispose() => File.Delete(_keyFile);

    [Fact]
    public async Task Open_mode_without_keys()
    {
        using var f = new ApiFactory();
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/v1/formats")).StatusCode);
    }

    [Fact]
    public async Task Keys_required_rotation_and_open_health_docs()
    {
        File.WriteAllText(_keyFile, "# comment\nci:old-key\n");
        using var f = ApiFactory.With(new() { ["API_KEYS_FILE"] = _keyFile, ["API_KEYS"] = "env-key" });
        var c = f.CreateClient();

        var missing = await c.GetAsync("/v1/formats");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("unauthorized", (string)(await missing.Json())["code"]!);
        Assert.Contains("Bearer", missing.Headers.WwwAuthenticate.ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(c, "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "old-key")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "env-key")).StatusCode);

        // rotation: overlap old + new, then drop old
        File.WriteAllText(_keyFile, "ci:old-key\nci2:new-key\n");
        File.SetLastWriteTimeUtc(_keyFile, DateTime.UtcNow.AddSeconds(2));
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "old-key")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "new-key")).StatusCode);
        File.WriteAllText(_keyFile, "ci2:new-key\n");
        File.SetLastWriteTimeUtc(_keyFile, DateTime.UtcNow.AddSeconds(4));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(c, "old-key")).StatusCode);

        foreach (var open in new[] { "/healthz", "/readyz", "/openapi/v1.json", "/docs/" })
            Assert.NotEqual(HttpStatusCode.Unauthorized, (await c.GetAsync(open)).StatusCode);
    }

    private static Task<HttpResponseMessage> Get(HttpClient c, string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/v1/formats");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return c.SendAsync(req);
    }
}
