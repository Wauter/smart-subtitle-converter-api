using System.Net.Http.Json;
using SubtitleEditConverterApi.Infrastructure;
using SubtitleEditConverterApi.Jobs;

namespace SubtitleEditConverterApi.Tests;

public class JobTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public void Cap_ttl_owner_and_restart()
    {
        var clock = new Clock();
        var opts = new ApiOptions { JobMaxEntries = 2, JobTtlSeconds = 10 };
        using var store = new JobStore(opts, clock);
        var a = store.Create("a"); store.Finish(a);
        clock.Now = clock.Now.AddSeconds(1);
        var b = store.Create("b"); store.Finish(b);
        var c = store.Create();
        Assert.Equal(2, store.Count);
        Assert.Null(store.Get(a, "a"));
        Assert.Null(store.Get(b, "a"));
        Assert.NotNull(store.Get(b, "b"));
        clock.Now = clock.Now.AddSeconds(11);
        Assert.Null(store.Get(c));
        using var restarted = new JobStore(opts, clock);
        Assert.Null(restarted.Get(b, "b"));
    }
    [Fact]
    public void Expiring_running_records_does_not_bypass_active_limit()
    {
        var clock = new Clock();
        using var store = new JobStore(new() { JobMaxEntries = 1, JobTtlSeconds = 1 }, clock);
        var a = store.Create();
        clock.Now = clock.Now.AddSeconds(2);
        Assert.Throws<ApiException>(() => store.Create());
        store.Finish(a);
        Assert.NotNull(store.Create());
    }
    [Fact]
    public void Metadata_has_a_size_budget_and_signed_queries_are_not_retained()
    {
        using var store = new JobStore(new(), TimeProvider.System);
        var id = store.Create(input: "https://example.com/a?secret=value");
        store.Finish(id, new() { ["warnings"] = new string('x', JobStore.MaxMetadataBytes + 1) });
        var j = store.Get(id)!;
        Assert.DoesNotContain("secret", j.ToJsonString());
        Assert.True((bool)j["result"]!["metadataTruncated"]!);
    }
    [Fact]
    public async Task Synchronous_conversion_has_retrievable_metadata_but_no_content()
    {
        using var f = new ApiFactory(); var c = f.CreateClient();
        var response = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt" });
        var json = await response.Json();
        var id = (string)json["id"]!;
        Assert.Equal(id, response.Header("X-Job-Id"));
        var job = await (await c.GetAsync("/v1/jobs/" + id)).Json();
        Assert.Equal("succeeded", (string)job["status"]!);
        Assert.Null(job["result"]!["outputs"]![0]!["content"]);
        Assert.Equal(404, (int)(await c.GetAsync("/v1/jobs/gone")).StatusCode);
    }
}
