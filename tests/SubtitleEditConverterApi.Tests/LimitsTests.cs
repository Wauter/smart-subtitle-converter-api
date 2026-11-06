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
        Assert.Equal(30, (int)j["requestWaitSeconds"]!);
        Assert.Equal(60, (int)j["requestMaxWaitSeconds"]!);
        Assert.Equal(10, (int)j["io"]!["timeoutSeconds"]!);
        Assert.Equal(3, (int)j["io"]!["retries"]!);
        Assert.Equal(400, (int)j["jobs"]!["maxEntries"]!);
        Assert.Equal(21600, (int)j["jobs"]!["ttlSeconds"]!);
        Assert.Equal(33, (int)j["jobs"]!["maxActive"]!);
        Assert.Equal(131072, (int)j["jobs"]!["maxMetadataBytes"]!);
        Assert.Empty(j["storage"]!["inputSchemes"]!.AsArray());
        Assert.Empty(j["storage"]!["outputSchemes"]!.AsArray());
        Assert.Equal(2047999, (long)j["binary"]!["ebuStlMaxBytes"]!);
        Assert.Equal(["maxUploadBytes", "maxRequestBodyBytes", "json", "maxProfileBytes", "maxCues", "maxLineChars", "conversionTimeoutSeconds", "queue", "rateLimitPerMinute", "auth", "binary", "requestWaitSeconds", "requestMaxWaitSeconds", "io", "jobs", "storage"],
            j.Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public async Task Limits_reports_configured_schemes_and_effective_clamped_values()
    {
        using var f = ApiFactory.With(new() {
            ["STORAGE_FILE_ROOTS"] = "/data", ["HTTP_ALLOWED_HOSTS"] = "example.com", ["S3_ALLOWED_BUCKETS"] = "subs",
            ["REQUEST_WAIT_SECONDS"] = "80", ["IO_TIMEOUT_SECONDS"] = "0", ["IO_RETRIES"] = "100",
            ["JOB_MAX_ENTRIES"] = "2", ["JOB_TTL_SECONDS"] = "12"
        });
        var j = await (await f.CreateClient().GetAsync("/v1/limits")).Json();
        Assert.Equal(60, (int)j["requestWaitSeconds"]!);
        Assert.Equal(1, (int)j["io"]!["timeoutSeconds"]!);
        Assert.Equal(10, (int)j["io"]!["retries"]!);
        Assert.Equal(2, (int)j["jobs"]!["maxEntries"]!);
        Assert.Equal(2, (int)j["jobs"]!["maxActive"]!);
        Assert.Equal(12, (int)j["jobs"]!["ttlSeconds"]!);
        var schemes = OperatingSystem.IsLinux() ? new[] { "file", "https", "s3" } : new[] { "https", "s3" };
        Assert.Equal(schemes, j["storage"]!["inputSchemes"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(schemes, j["storage"]!["outputSchemes"]!.AsArray().Select(n => (string)n!));
        Assert.DoesNotContain("example.com", j.ToJsonString());
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
