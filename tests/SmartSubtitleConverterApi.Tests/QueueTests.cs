using System.Net;
using System.Net.Http.Json;
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
    public async Task Timeout_gives_504_then_no_conversion_runs_beside_the_stuck_one_and_cancelled_job_never_runs()
    {
        using var f = ApiFactory.With(new() { ["CONVERSION_TIMEOUT_SECONDS"] = "1", ["CONVERSION_STUCK_GRACE_SECONDS"] = "600" });
        var q = f.Services.GetRequiredService<ConversionQueue>();
        using var stuck = new ManualResetEventSlim();
        var ex = await Assert.ThrowsAsync<ApiException>(() => q.RunAsync(() => { stuck.Wait(); return "never"; }, default));
        Assert.Equal(504, ex.Status);
        Assert.Equal("conversion-timeout", ex.Code);
        Assert.True(q.IsStuck);
        var refused = await Assert.ThrowsAsync<ApiException>(() => q.RunAsync(() => "must not run", default));
        Assert.Equal(503, refused.Status); Assert.Equal("conversion-engine-stuck", refused.Code); Assert.True(refused.RetryAfterSeconds >= 1);
        stuck.Set();
        await WaitUntil(() => !q.IsStuck);
        Assert.False(q.IsStuck);
        Assert.Equal("ok", (await q.RunAsync(() => "ok", default)).Result); // the same worker resumed

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
    }

    // ---------------- conversion queue: one at a time, FIFO, ids, polling (deterministic: gates, no sleeps as asserts) ----------------

    [Fact]
    public async Task Conversions_run_strictly_one_at_a_time_under_20_parallel_jobs()
    {
        using var f = new ApiFactory();
        var q = f.Services.GetRequiredService<ConversionQueue>();
        using var gate = new ManualResetEventSlim();
        var inside = 0; var max = 0;
        string Work(int i)
        {
            var now = Interlocked.Increment(ref inside);
            lock (q) max = Math.Max(max, now);
            if (i == 0) gate.Wait(); // hold the worker until all others are queued
            Interlocked.Decrement(ref inside);
            return "r" + i;
        }
        var first = q.RunAsync(() => Work(0), default);
        await WaitUntil(() => q.Depth == 0 && Volatile.Read(ref inside) == 1);
        var rest = Enumerable.Range(1, 19).Select(i => q.RunAsync(() => Work(i), default)).ToList();
        Assert.True(q.Depth >= 19); // (the startup self-test may also be queued)
        gate.Set();
        var results = await Task.WhenAll(rest.Prepend(first));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => "r" + i), results.Select(r => r.Result));
        Assert.Equal(1, max);
        Assert.Equal(1, q.MaxConcurrent);
    }

    [Fact]
    public async Task Conversions_run_in_fifo_order()
    {
        using var f = new ApiFactory();
        var q = f.Services.GetRequiredService<ConversionQueue>();
        using var gate = new ManualResetEventSlim();
        var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var first = q.RunAsync(() => { gate.Wait(); order.Enqueue(0); return "0"; }, default);
        await WaitUntil(() => q.Depth == 0);
        var queued = Enumerable.Range(1, 15).Select(i => q.RunAsync(() => { order.Enqueue(i); return i.ToString(); }, default)).ToList();
        gate.Set();
        await Task.WhenAll(queued.Prepend(first));
        Assert.Equal(Enumerable.Range(0, 16), order);
    }

    [Fact]
    public async Task Job_id_is_assigned_before_the_job_reaches_the_worker()
    {
        using var f = new ApiFactory();
        var q = f.Services.GetRequiredService<ConversionQueue>();
        var c = f.CreateClient();
        using var gate = new ManualResetEventSlim();
        var blocker = q.RunAsync(() => { gate.Wait(); return "busy"; }, default);
        await WaitUntil(() => q.Depth == 0);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/convert/json") { Content = JsonContent.Create(new { content = Samples.Srt, target = "vtt" }) };
        req.Headers.Add("Prefer", "wait=0");
        var r = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var id = (string)(await r.Json())["id"]!;
        Assert.Equal(id, r.Header("X-Job-Id"));
        Assert.Equal($"/v1/jobs/{id}", r.Headers.Location!.ToString());
        Assert.Equal("running", (string)(await (await c.GetAsync($"/v1/jobs/{id}")).Json())["status"]!); // known while still queued
        gate.Set();
        await blocker;
        Assert.Equal("succeeded", await PollStatus(c, id));
    }

    [Fact]
    public async Task Many_parallel_wait0_jobs_return_202_and_all_complete_with_their_own_results()
    {
        var root = Directory.CreateTempSubdirectory("se-queue-").FullName;
        try
        {
            using var f = ApiFactory.With(new() { ["STORAGE_FILE_ROOTS"] = root });
            var c = f.CreateClient();
            var q = f.Services.GetRequiredService<ConversionQueue>();
            using var gate = new ManualResetEventSlim();
            var blocker = q.RunAsync(() => { gate.Wait(); return "busy"; }, default); // all jobs queue up behind this
            await WaitUntil(() => q.Depth == 0);
            const int n = 25;
            var posts = Enumerable.Range(0, n).Select(async i =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/convert/json")
                {
                    Content = JsonContent.Create(new { content = $"1\r\n00:00:01,000 --> 00:00:02,000\r\nCue number {i}\r\n", target = "vtt", output = Path.Combine(root, $"out-{i}.vtt") }),
                };
                req.Headers.Add("Prefer", "wait=0");
                var r = await c.SendAsync(req);
                Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
                return (i, Id: (string)(await r.Json())["id"]!);
            }).ToList();
            var accepted = await Task.WhenAll(posts);
            Assert.Equal(n, accepted.Select(a => a.Id).Distinct().Count());
            gate.Set();
            await blocker;
            foreach (var (i, id) in accepted)
            {
                Assert.Equal("succeeded", await PollStatus(c, id));
                var vtt = File.ReadAllText(Path.Combine(root, $"out-{i}.vtt"));
                Assert.Contains($"Cue number {i}", vtt.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()));
            }
            Assert.Equal(1, q.MaxConcurrent);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Job_store_active_limit_gives_503_queue_full_and_frees_on_finish()
    {
        using var jobs = new SmartSubtitleConverterApi.Jobs.JobStore(new ApiOptions { QueueCapacity = 2 }, TimeProvider.System);
        var ids = Enumerable.Range(0, 3).Select(_ => jobs.Create()).ToList(); // QUEUE_CAPACITY + 1 active
        var ex = Assert.Throws<ApiException>(() => jobs.Create());
        Assert.Equal(503, ex.Status); Assert.Equal("queue-full", ex.Code); Assert.Equal(1, ex.RetryAfterSeconds);
        jobs.Finish(ids[0], new System.Text.Json.Nodes.JsonObject());
        Assert.NotNull(jobs.Create());
    }

    // ---------------- H8: a conversion that never returns ----------------

    [Fact]
    public async Task Stuck_conversion_refuses_new_and_queued_work_and_readyz_is_not_ready_until_it_returns()
    {
        using var f = ApiFactory.With(new() { ["CONVERSION_TIMEOUT_SECONDS"] = "1", ["CONVERSION_STUCK_GRACE_SECONDS"] = "600" });
        var q = f.Services.GetRequiredService<ConversionQueue>();
        var c = f.CreateClient();
        await WaitUntil(async () => (await c.GetAsync("/readyz")).IsSuccessStatusCode);
        using var stuck = new ManualResetEventSlim();
        var ranBeside = false;
        var running = q.RunAsync(() => { stuck.Wait(); return "late"; }, default, new ConversionJobInfo("job-stuck-1", "req-1", "hang.srt", 123, null, "target=vtt"));
        await WaitUntil(() => q.Depth == 0);
        var queued = q.RunAsync(() => { ranBeside = true; return "queued"; }, default);
        Assert.Equal(504, (await Assert.ThrowsAsync<ApiException>(() => running)).Status);
        var drained = await Assert.ThrowsAsync<ApiException>(() => queued);
        Assert.Contains(drained.Code, new[] { "conversion-engine-stuck", "conversion-timeout" });

        var r = await c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "x.srt", ("target", "vtt")));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("conversion-engine-stuck", (string)(await r.Json())["code"]!);
        Assert.True(int.Parse(r.Headers.RetryAfter!.ToString()) >= 1);
        var ready = await c.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains("conversion engine stuck", (string)(await ready.Json())["checks"]!["worker"]!);
        Assert.False(ranBeside);

        stuck.Set();
        await WaitUntil(() => !q.IsStuck);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "x.srt", ("target", "vtt")))).StatusCode);
        await WaitUntil(async () => (await c.GetAsync("/readyz")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/readyz")).StatusCode);
        Assert.False(ranBeside);
        Assert.Equal(1, q.MaxConcurrent);
    }

    [Fact]
    public async Task Stuck_conversion_past_the_grace_period_exits_the_process_with_code_70()
    {
        using var f = ApiFactory.With(new() { ["CONVERSION_TIMEOUT_SECONDS"] = "1", ["CONVERSION_STUCK_GRACE_SECONDS"] = "1" });
        var q = f.Services.GetRequiredService<ConversionQueue>();
        var exit = (RecordingExit)f.Services.GetRequiredService<ProcessExit>();
        using var stuck = new ManualResetEventSlim();
        try
        {
            await Assert.ThrowsAsync<ApiException>(() => q.RunAsync(() => { stuck.Wait(); return "never"; }, default, new ConversionJobInfo("job-hang-42", "req-42", "hang.stl", 999)));
            var (code, reason) = await exit.Exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(ConversionQueue.StuckExitCode, code);
            Assert.Contains("job=job-hang-42", reason);
            Assert.Contains("request=req-42", reason);
            Assert.Contains("bytes=999", reason);
        }
        finally { stuck.Set(); }
    }

    [Fact]
    public async Task Stuck_conversion_that_returns_within_the_grace_period_never_exits()
    {
        using var f = ApiFactory.With(new() { ["CONVERSION_TIMEOUT_SECONDS"] = "1", ["CONVERSION_STUCK_GRACE_SECONDS"] = "2" });
        var q = f.Services.GetRequiredService<ConversionQueue>();
        var exit = (RecordingExit)f.Services.GetRequiredService<ProcessExit>();
        using var stuck = new ManualResetEventSlim();
        await Assert.ThrowsAsync<ApiException>(() => q.RunAsync(() => { stuck.Wait(); return "never"; }, default));
        stuck.Set();
        await WaitUntil(() => !q.IsStuck);
        var finished = await Task.WhenAny(exit.Exited.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.NotSame(exit.Exited.Task, finished);
        Assert.Equal("ok", (await q.RunAsync(() => "ok", default)).Result);
    }

    private static async Task<string> PollStatus(HttpClient c, string id)
    {
        for (var i = 0; i < 500; i++)
        {
            var status = (string)(await (await c.GetAsync($"/v1/jobs/{id}")).Json())["status"]!;
            if (status != "running") return status;
            await Task.Delay(20);
        }
        return "running";
    }

    private static async Task WaitUntil(Func<Task<bool>> cond)
    {
        for (var i = 0; i < 250 && !await cond(); i++) await Task.Delay(20);
    }

    private static async Task WaitUntil(Func<bool> cond)
    {
        for (var i = 0; i < 250 && !cond(); i++) await Task.Delay(20);
    }
}
