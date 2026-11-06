using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SmartSubtitleConverterApi.Conversion;
using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Tests;

public class QueueTests
{
    [Fact]
    public async Task Parallel_requests_with_conflicting_rules_match_serial_results()
    {
        using var f = new ApiFactory();
        var c = f.CreateClient();
        var text = "1\r\n00:00:01,000 --> 00:00:05,000\r\nThis is a fairly long line of subtitle text that will need to be split somewhere.\r\n";
        async Task<string> Run(int max) =>
            await (await c.PostAsync("/v1/convert", Samples.Form(text, "x.srt", ("target", "srt"), ("profile", $$"""{"rules":{"maxLineLength":{{max}}},"operations":[{"id":"SplitLongLines"}]}""")))).Content.ReadAsStringAsync();

        var serial20 = await Run(30);
        var serial60 = await Run(100);
        Assert.NotEqual(serial20, serial60);
        var parallel = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Run(i % 2 == 0 ? 30 : 100)));
        for (var i = 0; i < parallel.Length; i++) Assert.Equal(i % 2 == 0 ? serial20 : serial60, parallel[i]);
    }

    [Fact]
    public async Task Full_queue_rejects_with_503_and_retry_after()
    {
        using var f = ApiFactory.With(new() { ["QUEUE_CAPACITY"] = "1" });
        var q = f.Services.GetRequiredService<ConversionQueue>();
        var c = f.CreateClient();
        using var gate = new ManualResetEventSlim();
        var running = q.RunAsync(() => { gate.Wait(); return "a"; }, default);
        await WaitUntil(() => q.Depth == 0);
        var queued = q.RunAsync(() => "b", default); // fills the single slot
        var r = await c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "x.srt", ("target", "vtt")));
        gate.Set();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("queue-full", (string)(await r.Json())["code"]!);
        Assert.True(int.Parse(r.Headers.RetryAfter!.ToString()) >= 1);
        Assert.Equal("a", (await running).Result);
        Assert.Equal("b", (await queued).Result);
    }

    [Fact]
    public async Task Timeout_gives_504_and_worker_recovers_and_cancelled_job_never_runs()
    {
        using var f = ApiFactory.With(new() { ["CONVERSION_TIMEOUT_SECONDS"] = "1" });
        var q = f.Services.GetRequiredService<ConversionQueue>();
        using var stuck = new ManualResetEventSlim();
        var ex = await Assert.ThrowsAsync<ApiException>(() => q.RunAsync(() => { stuck.Wait(); return "never"; }, default));
        Assert.Equal(504, ex.Status);
        Assert.Equal("conversion-timeout", ex.Code);
        Assert.Equal("ok", (await q.RunAsync(() => "ok", default)).Result); // fresh worker took over

        using var block = new ManualResetEventSlim();
        var blocker = q.RunAsync(() => { block.Wait(); return "x"; }, default);
        await WaitUntil(() => q.Depth == 0);
        var ran = false;
        using var cts = new CancellationTokenSource();
        var cancelled = q.RunAsync(() => { ran = true; return "y"; }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        block.Set();
        await blocker;
        await q.RunAsync(() => "flush", default);
        Assert.False(ran);
        stuck.Set();
    }

    private static async Task WaitUntil(Func<bool> cond)
    {
        for (var i = 0; i < 100 && !cond(); i++) await Task.Delay(20);
    }
}
