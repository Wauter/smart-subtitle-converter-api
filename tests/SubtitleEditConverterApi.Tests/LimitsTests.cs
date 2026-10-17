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
        Assert.Equal(100, (int)j["zip"]!["maxEntries"]!);
        Assert.Equal(52428800, (long)j["zip"]!["maxUnpackedBytes"]!);
        Assert.Equal(100, (double)j["zip"]!["maxRatio"]!);
        Assert.Equal(10485760, (long)j["zip"]!["maxEntryBytes"]!);
        Assert.Equal(30, (int)j["conversionTimeoutSeconds"]!);
        Assert.Equal(32, (int)j["queue"]!["capacity"]!);
        Assert.Equal(0, (int)j["queue"]!["depth"]!);
        Assert.Equal(0, (int)j["rateLimitPerMinute"]!);
        Assert.False((bool)j["auth"]!["required"]!);
        Assert.Equal(2047999, (long)j["binary"]!["ebuStlMaxBytes"]!);
        Assert.Equal(["maxUploadBytes", "maxRequestBodyBytes", "json", "maxProfileBytes", "maxCues", "maxLineChars", "zip", "conversionTimeoutSeconds", "queue", "rateLimitPerMinute", "auth", "binary"],
            j.Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public async Task Env_values_reach_limits_including_zip_ratio_and_profile_size()
    {
        using var f = ApiFactory.With(new() { ["ZIP_MAX_RATIO"] = "7.5", ["PROFILE_MAX_BYTES"] = "2048", ["MAX_UPLOAD_BYTES"] = "300000", ["QUEUE_CAPACITY"] = "3" });
        var c = f.CreateClient();
        var j = await (await c.GetAsync("/v1/limits")).Json();
        Assert.Equal(7.5, (double)j["zip"]!["maxRatio"]!);
        Assert.Equal(2048, (int)j["maxProfileBytes"]!);
        Assert.Equal(300000, (long)j["maxUploadBytes"]!);
        Assert.Equal(4 * 100000 + 2048 + 65536, (long)j["maxRequestBodyBytes"]!["json"]!);
        Assert.Equal(3, (int)j["queue"]!["capacity"]!);
        // PROFILE_MAX_BYTES is enforced, not just reported
        var big = "{\"description\":\"" + new string('x', 3000) + "\"}";
        var r = await c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "x.srt", ("target", "vtt"), ("profile", big)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Contains("2048", (string)(await r.Json())["detail"]!);
    }

    [Fact]
    public async Task Zip_max_ratio_from_env_is_enforced()
    {
        using var f = ApiFactory.With(new() { ["ZIP_MAX_RATIO"] = "2" });
        var zip = Samples.Zip(("a.srt", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Samples.Srt, 20000)))));
        var r = await f.CreateClient().PostAsync("/v1/convert", Samples.Form(zip, "a.zip", ("target", "vtt")));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Contains("2:1", (string)(await r.Json())["hint"]!);
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
