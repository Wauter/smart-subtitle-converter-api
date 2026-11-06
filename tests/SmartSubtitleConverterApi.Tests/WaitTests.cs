using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartSubtitleConverterApi.Conversion;
namespace SmartSubtitleConverterApi.Tests;
public class WaitTests
{
    [Fact]
    public async Task Zero_wait_and_slow_job_continue_after_accepted()
    {
        using var f = new ApiFactory(); var c = f.CreateClient();
        var queue = f.Services.GetRequiredService<ConversionQueue>();
        using var block = new ManualResetEventSlim();
        var blocking = queue.RunAsync(() => { block.Wait(); return "done"; }, default);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/convert/json") { Content = JsonContent.Create(new { content = Samples.Srt, target = "vtt" }) };
            req.Headers.Add("Prefer", "wait=0");
            var res = await c.SendAsync(req);
            Assert.Equal(202, (int)res.StatusCode);
            Assert.Equal("1", res.Header("Retry-After"));
            var location = res.Header("Location");
            Assert.StartsWith("/v1/jobs/", location);
            Assert.Equal("running", (string)(await (await c.GetAsync(location)).Json())["status"]!);
            block.Set(); await blocking;
            for (var i = 0; i < 100; i++)
            {
                var j = await (await c.GetAsync(location)).Json();
                if ((string)j["status"]! == "succeeded") { Assert.NotNull(j["result"]); return; }
                await Task.Delay(20);
            }
            Assert.Fail("Job did not finish.");
        }
        finally { block.Set(); }
    }
    [Theory]
    [InlineData("wait=-1")][InlineData("wait=61")][InlineData("wait=no")][InlineData("wait=1, wait=2")]
    public async Task Invalid_wait_is_400(string value)
    {
        using var f = new ApiFactory(); var c = f.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/convert/json") { Content = JsonContent.Create(new { content = Samples.Srt, target = "vtt" }) };
        req.Headers.Add("Prefer", value);
        Assert.Equal(400, (int)(await c.SendAsync(req)).StatusCode);
    }
    [Fact]
    public async Task Configured_wait_expires_without_cancelling_job()
    {
        using var f = ApiFactory.With(new() { ["REQUEST_WAIT_SECONDS"] = "1" }); var c = f.CreateClient();
        var queue = f.Services.GetRequiredService<ConversionQueue>();
        using var block = new ManualResetEventSlim();
        var blocking = queue.RunAsync(() => { block.Wait(); return "done"; }, default);
        try
        {
            var res = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt" });
            Assert.Equal(202, (int)res.StatusCode);
            Assert.NotEmpty(res.Header("X-Job-Id"));
        }
        finally { block.Set(); await blocking; }
    }
}
