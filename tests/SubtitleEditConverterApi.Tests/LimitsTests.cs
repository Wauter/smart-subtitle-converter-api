using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SubtitleEditConverterApi.Tests;

public class LimitsTests
{
    [Fact]
    public async Task Limits_reports_defaults_and_no_store()
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().GetAsync("/v1/limits");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl!.ToString());
        var j = await r.Json();
        Assert.Equal(10485760, (long)j["maxUploadBytes"]!);
        Assert.Equal(10747904, (long)j["maxRequestBodyBytes"]!["multipart"]!);
        Assert.Equal(14112088, (long)j["maxRequestBodyBytes"]!["json"]!);
        Assert.Equal(10485760, (long)j["json"]!["maxDecodedContentBytes"]!);
        Assert.Contains("4*ceil(n/3)", (string)j["json"]!["base64"]!);
        Assert.Equal(65536, (int)j["maxProfileBytes"]!);
        Assert.Equal(200000, (int)j["maxCues"]!);
        Assert.Equal(10000, (int)j["maxLineChars"]!);
        Assert.Equal(30, (int)j["conversionTimeoutSeconds"]!);
        Assert.Equal(32, (int)j["queue"]!["capacity"]!);
        Assert.Equal(0, (int)j["queue"]!["depth"]!);
        Assert.Equal(0, (int)j["rateLimitPerMinute"]!);
        Assert.False((bool)j["auth"]!["required"]!);
        Assert.Equal(2047999, (long)j["binary"]!["ebuStlMaxBytes"]!);
        Assert.Equal(["maxUploadBytes", "maxRequestBodyBytes", "json", "maxProfileBytes", "maxCues", "maxLineChars", "conversionTimeoutSeconds", "queue", "rateLimitPerMinute", "auth", "binary"],
            j.Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public async Task Limits_needs_a_key_when_auth_is_on()
    {
        using var f = ApiFactory.With(new() { ["API_KEYS"] = "ci:secret-key" });
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/v1/limits")).StatusCode);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-key");
        var r = await c.GetAsync("/v1/limits");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True((bool)(await r.Json())["auth"]!["required"]!);
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("\"ci\"", body);
    }
}
