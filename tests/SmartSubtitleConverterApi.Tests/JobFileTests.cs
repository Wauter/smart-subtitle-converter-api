using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartSubtitleConverterApi.Endpoints;
using SmartSubtitleConverterApi.Infrastructure;
using SmartSubtitleConverterApi.Jobs;
using SmartSubtitleConverterApi.Storage;
namespace SmartSubtitleConverterApi.Tests;

public sealed class JobFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "job-files-" + Guid.NewGuid());
    public JobFileTests() { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "in.srt"), Samples.Srt); }
    public void Dispose() => Directory.Delete(root, true);
    private ApiFactory Factory(Dictionary<string, string?>? extra = null) {
        var settings = extra ?? new(); settings["STORAGE_FILE_ROOTS"] = root; return ApiFactory.With(settings);
    }
    private static async Task<string> Code(HttpResponseMessage r, int status) {
        Assert.Equal(status, (int)r.StatusCode); var p = await r.Json(); var code = (string)p["code"]!;
        // QA p7: job/storage problems used to fall back to the generic "Internal error" title.
        Assert.NotEqual("Internal error", (string)p["title"]!); Assert.Equal(ErrorCodes.Title(code), (string)p["title"]!);
        return code;
    }
    [Fact]
    public async Task Storage_output_reports_the_stored_file_name_not_the_generated_name()
    {
        using var f = Factory(); var c = f.CreateClient();
        var output = Path.Combine(root, "delivery 145152.vtt");
        var r = await c.PostAsJsonAsync("/v1/convert/json", new { input = Path.Combine(root, "in.srt"), output, target = "vtt" });
        Assert.Equal(200, (int)r.StatusCode); var body = await r.Json();
        Assert.Equal("delivery 145152.vtt", (string)body["outputs"]![0]!["fileName"]!);
        var job = await (await c.GetAsync("/v1/jobs/" + r.Header("X-Job-Id"))).Json();
        Assert.Equal("delivery 145152.vtt", (string)job["result"]!["outputs"]![0]!["fileName"]!);
        var inline = await (await c.PostAsJsonAsync("/v1/convert/json", new { input = Path.Combine(root, "in.srt"), target = "vtt" })).Json();
        Assert.Equal("in.vtt", (string)inline["outputs"]![0]!["fileName"]!);
    }

    [Theory]
    [InlineData("s3://subs/qa-p6/test2-145152.srt", "test2-145152.srt")]
    [InlineData("s3://subs/out/a%20b.srt", "a b.srt")]
    [InlineData("https://store.example.com/out/show.pac?signature=x", "show.pac")]
    [InlineData("file:///data/out/show.vtt", "show.vtt")]
    [InlineData("/data/out/show.vtt", "show.vtt")]
    public void Stored_name_is_the_last_location_segment(string output, string expected) => Assert.Equal(expected, ConvertEndpoints.StoredName(output));

    [Fact]
    public async Task Any_valid_key_reads_metadata_and_current_files_but_auth_still_applies()
    {
        using var f = Factory(new() { ["API_KEYS"] = "alice:test-alice,bob:test-bob" });
        var a = f.CreateClient(); a.DefaultRequestHeaders.Authorization = new("Bearer", "test-alice");
        var output = Path.Combine(root, "out.vtt");
        var result = await a.PostAsJsonAsync("/v1/convert/json", new { input = Path.Combine(root, "in.srt"), output, target = "vtt" });
        Assert.Equal(200, (int)result.StatusCode); var id = result.Header("X-Job-Id");
        var b = f.CreateClient(); b.DefaultRequestHeaders.Authorization = new("Bearer", "test-bob");
        var job = await (await b.GetAsync("/v1/jobs/" + id)).Json(); Assert.Equal("succeeded", (string)job["status"]!);
        Assert.DoesNotContain("Hello", job.ToJsonString());
        var input = await b.GetAsync("/v1/jobs/" + id + "/input");
        Assert.Equal(Samples.Srt, await input.Content.ReadAsStringAsync()); Assert.Contains("attachment", input.Content.Headers.ContentDisposition!.ToString());
        // H7: job file reads carry an ETag; If-None-Match with it is 304 without the content.
        var cond = new HttpRequestMessage(HttpMethod.Get, "/v1/jobs/" + id + "/input"); cond.Headers.TryAddWithoutValidation("If-None-Match", input.Headers.ETag!.Tag);
        var notModified = await b.SendAsync(cond);
        Assert.Equal(304, (int)notModified.StatusCode); Assert.Empty(await notModified.Content.ReadAsByteArrayAsync()); Assert.Equal(id, notModified.Header("X-Job-Id"));
        Assert.True((bool)job["inputReplayable"]!); Assert.True((bool)job["outputReplayable"]!);
        File.WriteAllText(output, "current bytes, not an archived result");
        var read = await b.GetAsync("/v1/jobs/" + id + "/output");
        Assert.Equal("text/vtt", read.Content.Headers.ContentType!.MediaType); Assert.Equal("no-store", read.Headers.CacheControl!.ToString());
        Assert.Equal("current bytes, not an archived result", await read.Content.ReadAsStringAsync());
        var anonymous = f.CreateClient();
        foreach (var path in new[] { "", "/input", "/output" }) Assert.Equal(401, (int)(await anonymous.GetAsync("/v1/jobs/" + id + path)).StatusCode);
        File.Delete(output); var gone = await b.GetAsync("/v1/jobs/" + id + "/output");
        Assert.Equal("job-file-gone", await Code(gone, 404)); Assert.Equal(new Uri(output).AbsoluteUri, (string)(await gone.Json())["location"]!);
    }
    [Fact]
    public async Task Upload_and_response_only_jobs_explain_unstored_files()
    {
        using var f = Factory(); var c = f.CreateClient();
        var r = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt" }); var id = r.Header("X-Job-Id");
        Assert.Equal("job-input-not-stored", await Code(await c.GetAsync($"/v1/jobs/{id}/input"), 409));
        var notStored = await c.GetAsync($"/v1/jobs/{id}/output");
        Assert.Equal("job-output-not-stored", await Code(notStored, 409));
        var problem = await notStored.Json();
        Assert.Equal("Job output not stored", (string)problem["title"]!);
        Assert.Contains("returned in the convert response and not stored", (string)problem["detail"]!);
        Assert.Contains("convert with an output location", (string)problem["hint"]!);
        var inputProblem = await (await c.GetAsync($"/v1/jobs/{id}/input")).Json();
        Assert.Equal("Job input not stored", (string)inputProblem["title"]!);
        Assert.Equal("job-not-found", await Code(await c.GetAsync("/v1/jobs/unknown"), 404));
        Assert.Equal("Job not found", (string)(await (await c.GetAsync("/v1/jobs/unknown")).Json())["title"]!);
        Assert.Equal("job-not-found", await Code(await c.GetAsync("/v1/jobs/unknown/output"), 404));
    }
    [Fact]
    public async Task Running_input_is_readable_output_is_not_ready_and_failed_output_is_not_stored()
    {
        using var f = Factory(); var c = f.CreateClient(); var jobs = f.Services.GetRequiredService<JobStore>();
        var output = Path.Combine(root, "old.vtt"); File.WriteAllText(output, "old");
        var id = jobs.Create(Path.Combine(root, "in.srt"), output);
        Assert.Equal(200, (int)(await c.GetAsync($"/v1/jobs/{id}/input")).StatusCode);
        Assert.Equal("job-not-ready", await Code(await c.GetAsync($"/v1/jobs/{id}/output"), 409));
        jobs.Finish(id, error: new() { ["code"] = "output-exists" });
        var failed = await c.GetAsync($"/v1/jobs/{id}/output");
        Assert.Equal("job-output-not-stored", await Code(failed, 409));
        Assert.Contains("the conversion failed", (string)(await failed.Json())["detail"]!);
        File.Delete(Path.Combine(root, "in.srt"));
        Assert.Equal("job-file-gone", await Code(await c.GetAsync($"/v1/jobs/{id}/input"), 404));
    }
    [Fact]
    public async Task Query_redacted_https_locations_never_attempt_replay()
    {
        using var f = Factory(); var c = f.CreateClient(); var jobs = f.Services.GetRequiredService<JobStore>();
        var id = jobs.Create("https://mam.example.com/in.srt?signature=throwaway", "https://mam.example.com/out.vtt?signature=throwaway"); jobs.Finish(id);
        Assert.DoesNotContain("signature", (await (await c.GetAsync($"/v1/jobs/{id}")).Json()).ToJsonString());
        foreach (var which in new[] { "input", "output" }) {
            var r = await c.GetAsync($"/v1/jobs/{id}/{which}"); Assert.Equal("job-location-not-replayable", await Code(r, 409));
            var j = await r.Json(); Assert.StartsWith("https://mam.example.com/", (string)j["location"]!); Assert.DoesNotContain("?", j.ToJsonString());
        }
    }
    private sealed class Transport : HttpMessageHandler {
        public int Calls; public bool Missing;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) {
            Calls++; Assert.Equal(HttpMethod.Get, req.Method);
            return Task.FromResult(new HttpResponseMessage(Missing ? System.Net.HttpStatusCode.NotFound : System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Samples.Srt)) });
        }
    }
    [Fact]
    public async Task Stable_https_locations_use_the_existing_transport_and_missing_files_are_distinct()
    {
        var transport = new Transport();
        using var f = ApiFactory.With(new() { ["HTTP_ALLOWED_HOSTS"] = "mam.example.com" }, services => {
            services.RemoveAll<HttpStorage>(); services.AddSingleton(new HttpStorage(new() { HttpAllowedHosts = ["mam.example.com"] }, new HttpClient(transport)));
        });
        var c = f.CreateClient(); var jobs = f.Services.GetRequiredService<JobStore>(); var id = jobs.Create("https://mam.example.com/in.srt");
        Assert.Equal(Samples.Srt, await (await c.GetAsync($"/v1/jobs/{id}/input")).Content.ReadAsStringAsync());
        transport.Missing = true;
        Assert.Equal("job-file-gone", await Code(await c.GetAsync($"/v1/jobs/{id}/input"), 404)); Assert.Equal(2, transport.Calls);
    }
    [Fact]
    public async Task Replay_rechecks_current_allowlists_and_size_cap()
    {
        using var off = new ApiFactory(); var c = off.CreateClient(); var jobs = off.Services.GetRequiredService<JobStore>(); var id = jobs.Create(Path.Combine(root, "in.srt"));
        Assert.Equal("storage-location-rejected", await Code(await c.GetAsync($"/v1/jobs/{id}/input"), 422));
        using var f = Factory(new() { ["MAX_UPLOAD_BYTES"] = "64" }); c = f.CreateClient(); jobs = f.Services.GetRequiredService<JobStore>(); id = jobs.Create(Path.Combine(root, "in.srt"));
        File.WriteAllText(Path.Combine(root, "in.srt"), new string('x', 128));
        Assert.Equal("payload-too-large", await Code(await c.GetAsync($"/v1/jobs/{id}/input"), 413));
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    [Fact]
    public async Task Expiry_eviction_and_restart_are_not_conversion_failures()
    {
        var clock = new Clock();
        using var f = ApiFactory.With(new() { ["JOB_TTL_SECONDS"] = "1", ["JOB_MAX_ENTRIES"] = "1" }, services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); });
        var c = f.CreateClient(); var jobs = f.Services.GetRequiredService<JobStore>(); var id = jobs.Create(); jobs.Finish(id); var newer = jobs.Create(); jobs.Finish(newer);
        Assert.Equal("job-not-found", await Code(await c.GetAsync($"/v1/jobs/{id}/input"), 404));
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Equal("job-not-found", await Code(await c.GetAsync($"/v1/jobs/{newer}"), 404));
        using var restarted = new ApiFactory(); Assert.Equal("job-not-found", await Code(await restarted.CreateClient().GetAsync($"/v1/jobs/{newer}/output"), 404));
    }
    [Theory][InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task Error_preserves_files_and_replace_updates_subtitle_and_sidecar(bool atomic, bool multipart)
    {
        using var f = Factory(new() { ["STORAGE_FILE_ATOMIC"] = atomic.ToString() }); var c = f.CreateClient(); var output = Path.Combine(root, "out.vtt"); File.WriteAllText(output, "old"); File.WriteAllText(output + ".json", "old metadata");
        var collision = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, output, target = "vtt" });
        Assert.Equal("output-exists", await Code(collision, 409)); Assert.Contains("error | replace", (string)(await collision.Json())["hint"]!); Assert.Equal("old", File.ReadAllText(output));
        HttpResponseMessage replaced;
        if (multipart) {
            using var form = new MultipartFormDataContent { { new StringContent(Path.Combine(root, "in.srt")), "input" }, { new StringContent(output), "output" }, { new StringContent("vtt"), "target" }, { new StringContent("true"), "sidecar" }, { new StringContent("replace"), "ifExists" } };
            replaced = await c.PostAsync("/v1/convert", form);
        } else replaced = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, output, target = "vtt", sidecar = true, ifExists = "replace" });
        Assert.Equal(200, (int)replaced.StatusCode); Assert.Contains("WEBVTT", File.ReadAllText(output)); Assert.Contains("stats", File.ReadAllText(output + ".json"));
        Assert.DoesNotContain(Directory.GetFiles(root), p => p.EndsWith(".partial"));
    }
    [Theory][InlineData("archive")][InlineData("delete")][InlineData("Replace")][InlineData("")]
    public async Task Invalid_policies_are_rejected_in_both_request_shapes(string policy)
    {
        using var f = Factory(); var c = f.CreateClient();
        Assert.Equal("invalid-request", await Code(await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt", ifExists = policy }), 422));
        using var form = new MultipartFormDataContent { { new StringContent(Path.Combine(root, "in.srt")), "input" }, { new StringContent("vtt"), "target" }, { new StringContent(policy), "ifExists" } };
        Assert.Equal("invalid-request", await Code(await c.PostAsync("/v1/convert", form), 422));
    }
    [Fact]
    public async Task Replacement_rejects_symlinks_and_cancelled_staging_preserves_old_file()
    {
        var real = Path.Combine(root, "real.vtt"); var link = Path.Combine(root, "link.vtt"); File.WriteAllText(real, "old"); File.CreateSymbolicLink(link, real);
        using var f = Factory(); var c = f.CreateClient();
        Assert.Equal("storage-location-rejected", await Code(await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt", output = link, ifExists = "replace" }), 422)); Assert.Equal("old", File.ReadAllText(real));
        var storage = new FileStorage(new() { StorageFileRoots = [root] }); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.WriteAsync(StorageLocation.Parse(real), "new"u8.ToArray(), "text/plain", cancel.Token, replace: true));
        Assert.Equal("old", File.ReadAllText(real)); Assert.DoesNotContain(Directory.GetFiles(root), p => p.EndsWith(".partial"));
    }
}
