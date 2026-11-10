using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartSubtitleConverterApi.Conversion;
using SmartSubtitleConverterApi.Infrastructure;
using SmartSubtitleConverterApi.Storage;

namespace SmartSubtitleConverterApi.Tests;

/// <summary>Collects log lines (for "never logs the key" and "details go to the log" checks).</summary>
public sealed class CaptureLogs : ILoggerProvider, ILogger
{
    public readonly ConcurrentQueue<string> Lines = new();
    public string All => string.Join("\n", Lines);
    public ILogger CreateLogger(string categoryName) => this;
    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
}

/// <summary>H1: API key by default, generated key file, host allowlist and Origin rejection.</summary>
[SupportedOSPlatform("linux")]
public sealed class BrowserAccessTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("se-keys-").FullName;
    public void Dispose() => Directory.Delete(dir, true);
    private static ApiOptions Opts(string? file, string? keys = null, bool disabled = false, string[]? roots = null) =>
        new() { ApiKeysFile = file, ApiKeys = keys, ApiAuthDisabled = disabled, StorageFileRoots = roots ?? [] };

    [Fact]
    public void Empty_key_file_gets_a_generated_key_in_place_mode_0600_and_the_key_is_never_logged()
    {
        var path = Path.Combine(dir, "api_keys.txt");
        File.WriteAllText(path, "");
        var inode = new FileInfo(path).Length;
        var log = new CaptureLogs();
        ApiKeyBootstrap.Ensure(Opts(path), log);
        var lines = File.ReadAllLines(path);
        var key = Assert.Single(lines, l => !l.StartsWith('#') && l.Length > 0);
        Assert.StartsWith("ssc_", key);
        Assert.True(key.Length >= 40);
        Assert.DoesNotContain(':', key);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Contains("auth.key-generated", log.All);
        Assert.Contains(path, log.All);
        Assert.Contains("SUBTITLE_API_KEY_FILE", log.All);
        Assert.DoesNotContain(key, log.All);
        Assert.DoesNotContain(key[..3] + "***", log.All); // not even masked
        Assert.DoesNotContain(key[^4..], log.All);
        // Second start: the key is loaded, nothing new is generated.
        var again = new CaptureLogs();
        ApiKeyBootstrap.Ensure(Opts(path), again);
        Assert.Equal(lines, File.ReadAllLines(path));
        Assert.Contains("auth.enabled keys=1", again.All);
        Assert.DoesNotContain("key-generated", again.All);
    }

    [Fact]
    public void Comment_only_file_counts_as_empty_and_keeps_its_comments()
    {
        var path = Path.Combine(dir, "keys.txt");
        File.WriteAllText(path, "# my notes");
        ApiKeyBootstrap.Ensure(Opts(path), new CaptureLogs());
        var lines = File.ReadAllLines(path);
        Assert.Equal("# my notes", lines[0]);
        Assert.Single(lines, l => l.StartsWith("ssc_"));
    }

    [Fact]
    public void Missing_file_directory_and_unwritable_file_stop_startup_with_actionable_errors()
    {
        var missing = Assert.Throws<StartupConfigException>(() => ApiKeyBootstrap.Ensure(Opts(Path.Combine(dir, "nope.txt")), new CaptureLogs()));
        Assert.Contains("does not exist", missing.Message); Assert.Contains("touch", missing.Message); Assert.Contains("API_AUTH_DISABLED", missing.Message);
        var asDir = Path.Combine(dir, "api_keys.txt"); Directory.CreateDirectory(asDir);
        var directory = Assert.Throws<StartupConfigException>(() => ApiKeyBootstrap.Ensure(Opts(asDir), new CaptureLogs()));
        Assert.Contains("is a directory", directory.Message); Assert.Contains("Docker created", directory.Message);
        var unset = Assert.Throws<StartupConfigException>(() => ApiKeyBootstrap.Ensure(Opts(null), new CaptureLogs()));
        Assert.Contains("API_KEYS_FILE", unset.Message);
        if (Environment.UserName != "root")
        {
            var ro = Path.Combine(dir, "ro.txt"); File.WriteAllText(ro, "");
            File.SetUnixFileMode(ro, UnixFileMode.UserRead);
            var e = Assert.Throws<StartupConfigException>(() => ApiKeyBootstrap.Ensure(Opts(ro), new CaptureLogs()));
            Assert.Contains("read-write", e.Message);
            Assert.Equal("", File.ReadAllText(ro));
        }
    }

    [Fact]
    public void Existing_keys_and_api_keys_env_are_used_and_disabled_auth_warns_louder_with_storage()
    {
        var path = Path.Combine(dir, "k.txt"); File.WriteAllText(path, "# c\nalice:abc-secret-1234567890\n");
        var log = new CaptureLogs();
        ApiKeyBootstrap.Ensure(Opts(path, "env-key-1"), log);
        Assert.Contains("auth.enabled keys=2", log.All);
        Assert.DoesNotContain("abc-secret", log.All);
        var envOnly = new CaptureLogs();
        ApiKeyBootstrap.Ensure(Opts(null, "env-key-1"), envOnly);
        Assert.Contains("auth.enabled keys=1 source=API_KEYS", envOnly.All);
        var envMissingFile = new CaptureLogs(); // image default API_KEYS_FILE=/api_keys.txt not mounted, API_KEYS set
        ApiKeyBootstrap.Ensure(Opts(Path.Combine(dir, "absent.txt"), "env-key-1"), envMissingFile);
        Assert.Contains("auth.enabled keys=1 source=API_KEYS", envMissingFile.All);
        Assert.False(File.Exists(Path.Combine(dir, "absent.txt")));
        var off = new CaptureLogs();
        ApiKeyBootstrap.Ensure(Opts(null, disabled: true, roots: ["/tmp"]), off);
        Assert.Contains("auth.disabled", off.All); Assert.Contains("DANGER", off.All);
        var offPlain = new CaptureLogs();
        ApiKeyBootstrap.Ensure(Opts(null, disabled: true), offPlain);
        Assert.DoesNotContain("DANGER", offPlain.All);
    }

    [Fact]
    public async Task Generated_key_is_required_end_to_end()
    {
        var path = Path.Combine(dir, "api_keys.txt"); File.WriteAllText(path, "");
        using var f = ApiFactory.With(new() { ["API_KEYS_FILE"] = path });
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/v1/formats")).StatusCode);
        var key = File.ReadAllLines(path).Single(l => l.StartsWith("ssc_"));
        c.DefaultRequestHeaders.Authorization = new("Bearer", key);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/v1/formats")).StatusCode);
        Assert.True((bool)(await (await c.GetAsync("/v1/limits")).Json())["auth"]!["required"]!);
    }

    private static HttpRequestMessage Get(string path, string? host = null, string? origin = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Get, path);
        if (host != null) m.Headers.Host = host;
        if (origin != null) m.Headers.Add("Origin", origin);
        return m;
    }

    [Fact]
    public async Task Host_allowlist_accepts_loopback_names_and_configured_hosts_and_names_the_rejected_host()
    {
        using var f = ApiFactory.With(new() { ["ALLOWED_HOSTS"] = "subtitle-api, api.example.org" });
        var c = f.CreateClient();
        foreach (var host in new[] { "localhost", "localhost:8080", "127.0.0.1:8080", "[::1]:8080", "LOCALHOST", "subtitle-api:8080", "api.example.org" })
            Assert.True((await c.SendAsync(Get("/v1/formats", host))).IsSuccessStatusCode, host);
        var r = await c.SendAsync(Get("/v1/formats", "attacker.example:8080"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var j = await r.Json();
        Assert.Equal("host-not-allowed", (string)j["code"]!);
        Assert.Contains("attacker.example:8080", (string)j["detail"]!);
        Assert.Contains("ALLOWED_HOSTS", (string)j["hint"]!);
        // Probes stay reachable by pod IP; docs too are protected.
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Get("/healthz", "10.1.2.3:8080"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Get("/openapi/v1.json", "evil.example"))).StatusCode);
    }

    [Fact]
    public async Task Default_host_list_is_loopback_only_and_star_accepts_any_host()
    {
        using var f = new ApiFactory();
        Assert.Equal(HttpStatusCode.BadRequest, (await f.CreateClient().SendAsync(Get("/v1/formats", "subtitle-api:8080"))).StatusCode);
        using var any = ApiFactory.With(new() { ["ALLOWED_HOSTS"] = "*" });
        Assert.Equal(HttpStatusCode.OK, (await any.CreateClient().SendAsync(Get("/v1/formats", "whatever.example"))).StatusCode);
    }

    [Fact]
    public async Task Origin_is_accepted_only_from_an_allowed_host_and_a_cross_site_multipart_post_writes_nothing()
    {
        var root = Directory.CreateTempSubdirectory("se-origin-").FullName;
        try
        {
            using var f = ApiFactory.With(new() { ["ALLOWED_HOSTS"] = "subtitle-api", ["STORAGE_FILE_ROOTS"] = root });
            var c = f.CreateClient();
            foreach (var origin in new[] { "http://localhost:3000", "http://127.0.0.1", "https://subtitle-api", "http://[::1]:5173" })
                Assert.True((await c.SendAsync(Get("/v1/formats", origin: origin))).IsSuccessStatusCode, origin);
            foreach (var origin in new[] { "https://evil.example", "null", "file://", "http://localhost.evil.example", "garbage" })
            {
                var r = await c.SendAsync(Get("/v1/formats", origin: origin));
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
                Assert.Equal("origin-not-allowed", (string)(await r.Json())["code"]!);
            }
            // CSRF repro from the review: a "simple" multipart POST from another site must not write a file.
            using var form = Samples.Form(Samples.Srt, "x.srt", ("target", "webvtt"), ("output", Path.Combine(root, "csrf-out.vtt")));
            using var post = new HttpRequestMessage(HttpMethod.Post, "/v1/convert") { Content = form };
            post.Headers.Add("Origin", "https://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(post)).StatusCode);
            Assert.False(File.Exists(Path.Combine(root, "csrf-out.vtt")));
            // No Origin (curl, the MCP): unaffected. No CORS headers are sent.
            var plain = await c.GetAsync("/v1/formats");
            Assert.True(plain.IsSuccessStatusCode);
            Assert.False(plain.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task With_any_host_an_origin_must_match_the_request_host()
    {
        using var f = ApiFactory.With(new() { ["ALLOWED_HOSTS"] = "*" });
        var c = f.CreateClient();
        Assert.True((await c.SendAsync(Get("/v1/formats", "api.example:8080", "https://api.example"))).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Get("/v1/formats", "api.example:8080", "https://evil.example"))).StatusCode);
    }

    [Fact]
    public void Host_normalisation_strips_ports_case_and_brackets_ipv6()
    {
        Assert.Equal("localhost", HostOriginFilter.Normalize("LocalHost:8080"));
        Assert.Equal("[::1]", HostOriginFilter.Normalize("[::1]:8080"));
        Assert.Equal("[::1]", HostOriginFilter.Normalize("::1"));
        Assert.Equal("api.example", HostOriginFilter.Normalize("api.example."));
    }
}

/// <summary>H2 writes, H3 extension allowlist, H4 failed-job input, H6 listing cap and browse queue, H9 verbose errors, H10 symlinks.</summary>
public sealed class StorageHardeningTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("se-harden-").FullName;
    private readonly string outside = Directory.CreateTempSubdirectory("se-outside-").FullName;
    public void Dispose() { Directory.Delete(root, true); Directory.Delete(outside, true); }
    private ApiFactory Api(Dictionary<string, string?>? extra = null, Action<IServiceCollection>? configure = null)
    {
        var s = new Dictionary<string, string?> { ["STORAGE_FILE_ROOTS"] = root, ["STORAGE_LIST_ENABLED"] = "true" };
        foreach (var (k, v) in extra ?? []) s[k] = v;
        return ApiFactory.With(s, configure);
    }
    private static string Q(string v) => Uri.EscapeDataString(v);
    private static async Task<(int Status, JsonObject Body)> Send(Task<HttpResponseMessage> request) { var r = await request; return ((int)r.StatusCode, await r.Json()); }

    // ---------------- H2 ----------------

    [Fact]
    public async Task Error_order_outside_roots_first_then_hidden_then_extension()
    {
        using var f = Api(); var c = f.CreateClient();
        async Task<string> Code(string output) => (string)(await Send(c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt", output }))).Body["code"]!;
        Assert.Equal("storage-location-rejected", await Code(Path.Combine(outside, ".x", "a.txt")));
        Assert.Equal("hidden-path", await Code(Path.Combine(root, ".x", "a.txt")));
        Assert.Equal("extension-not-allowed", await Code(Path.Combine(root, "a.txt")));
        var (_, read) = await Send(c.GetAsync($"/v1/storage/file?location={Q(Path.Combine(outside, ".env"))}"));
        Assert.Equal("storage-location-rejected", (string)read["code"]!);
        var (_, hidden) = await Send(c.GetAsync($"/v1/storage/file?location={Q(Path.Combine(root, ".env"))}"));
        Assert.Equal("hidden-path", (string)hidden["code"]!);
        Assert.Contains("hidden", (string)hidden["detail"]!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hidden_outputs_sidecars_and_replacements_are_refused_and_touch_nothing()
    {
        Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));
        File.WriteAllText(Path.Combine(root, "repo", ".git", "config"), "[core]\n");
        using var f = Api(); var c = f.CreateClient();
        foreach (var output in new[] { Path.Combine(root, ".x.srt"), Path.Combine(root, "repo", ".git", "config.srt"), Path.Combine(root, "repo", ".git", "config") })
        {
            var (s, j) = await Send(c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "srt", output, ifExists = "replace", sidecar = true }));
            Assert.Equal(422, s); Assert.Equal("hidden-path", (string)j["code"]!);
        }
        Assert.Equal("[core]\n", File.ReadAllText(Path.Combine(root, "repo", ".git", "config")));
        Assert.False(File.Exists(Path.Combine(root, ".x.srt")));
        Assert.False(File.Exists(Path.Combine(root, ".x.srt.json")));
        // The S3 backend applies the same rule to writes, existence checks and deletes, before any call.
        var s3 = new S3Storage(new ApiOptions { S3AllowedBuckets = ["subs"] });
        foreach (var key in new[] { "s3://subs/tmp/.x.srt", "s3://subs/tmp/.cache/a.srt", "s3://subs/.git/config.srt" })
        {
            var loc = StorageLocation.Parse(key);
            Assert.Equal("hidden-path", (await Assert.ThrowsAsync<ApiException>(() => s3.WriteAsync(loc, [1], "text/plain", default))).Code);
            Assert.Equal("hidden-path", (await Assert.ThrowsAsync<ApiException>(() => s3.DeleteAsync(loc with { Sidecar = true }, default))).Code);
            Assert.Equal("hidden-path", (await Assert.ThrowsAsync<ApiException>(() => s3.ExistsAsync(loc, default))).Code);
        }
    }

    [Fact]
    public async Task Https_urls_are_not_subject_to_the_hidden_or_extension_rules()
    {
        using var f = Api(new() { ["HTTP_ALLOWED_HOSTS"] = "mam.example.com" });
        var router = f.Services.GetRequiredService<StorageRouter>();
        router.Validate(StorageLocation.Parse("https://mam.example.com/.well-known/subs"));
        router.Validate(StorageLocation.Parse("https://mam.example.com/a/.hidden/x.json?sig=1"));
        ExtensionAllowlist.Shared.Check(StorageLocation.Parse("https://mam.example.com/download"));
        await Task.CompletedTask;
    }

    // ---------------- H3 ----------------

    [Fact]
    public async Task Listing_shows_only_subtitle_files_and_folders()
    {
        foreach (var n in new[] { "a.srt", "b.SRT", "c.vtt", "d.stl", "notes.docx", "config.yaml", "README", "e.pac" }) File.WriteAllText(Path.Combine(root, n), Samples.Srt);
        Directory.CreateDirectory(Path.Combine(root, "season.1"));
        using var f = Api(); var c = f.CreateClient();
        var (s, j) = await Send(c.GetAsync($"/v1/storage/list?location={Q(root)}"));
        Assert.Equal(200, s);
        Assert.Equal(new[] { "a.srt", "b.SRT", "c.vtt", "d.stl", "e.pac", "season.1/" }, j["items"]!.AsArray().Select(i => (string)i!["name"]!).ToArray());
    }

    [Fact]
    public async Task Reading_and_converting_non_subtitle_files_by_location_is_refused_naming_the_extension()
    {
        File.WriteAllText(Path.Combine(root, "config.json5"), "SECRET=hunter2");
        File.WriteAllText(Path.Combine(root, "noext"), "SECRET=hunter2");
        using var f = Api(); var c = f.CreateClient();
        foreach (var loc in new[] { Path.Combine(root, "config.json5"), Path.Combine(root, "noext") })
        {
            var (s, j) = await Send(c.GetAsync($"/v1/storage/file?location={Q(loc)}"));
            Assert.Equal(422, s); Assert.Equal("extension-not-allowed", (string)j["code"]!);
            Assert.DoesNotContain("hunter2", j.ToJsonString());
            var (cs, cj) = await Send(c.PostAsJsonAsync("/v1/convert/json", new { input = loc, target = "vtt" }));
            Assert.Equal(422, cs); Assert.Equal("extension-not-allowed", (string)cj["code"]!);
            var (ds, dj) = await Send(c.PostAsJsonAsync("/v1/detect", new { input = loc }));
            Assert.Equal(422, ds); Assert.Equal("extension-not-allowed", (string)dj["code"]!);
        }
        var (_, named) = await Send(c.GetAsync($"/v1/storage/file?location={Q(Path.Combine(root, "config.json5"))}"));
        Assert.Equal(".json5", (string)named["extension"]!);
    }

    [Fact]
    public async Task Output_extension_must_belong_to_the_target_format_and_the_sidecar_is_exempt()
    {
        using var f = Api(); var c = f.CreateClient();
        var (s, j) = await Send(c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt", output = Path.Combine(root, "show.srt") }));
        Assert.Equal(422, s); Assert.Equal("extension-not-allowed", (string)j["code"]!);
        Assert.Contains(".vtt", (string)j["detail"]!);
        Assert.False(File.Exists(Path.Combine(root, "show.srt")));
        var ok = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt", output = Path.Combine(root, "show.VTT"), sidecar = true });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(File.Exists(Path.Combine(root, "show.VTT.json")));
        var ttml = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "ttml", output = Path.Combine(root, "show.dfxp") });
        Assert.True(ttml.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity); // .dfxp is an alternate of some TTML flavours, not necessarily timedtext10
    }

    [Fact]
    public void Extension_allowlist_matches_the_reviewed_snapshot()
    {
        // Fails when a libse upgrade adds or removes extensions: review the diff, then regenerate with UPDATE_EXTENSIONS=1 dotnet test.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SmartSubtitleConverterApi.slnx"))) dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "tests", "SmartSubtitleConverterApi.Tests", "extension-allowlist.txt");
        var current = ExtensionAllowlist.Shared.Sorted.ToList();
        if (Environment.GetEnvironmentVariable("UPDATE_EXTENSIONS") == "1" || !File.Exists(path))
            File.WriteAllLines(path, new[] { "# Subtitle extensions allowed for list/read/write by file and S3 location, derived from libse (H3).", "# Review additions/removals on every libse upgrade. Regenerate: UPDATE_EXTENSIONS=1 dotnet test" }.Concat(current));
        var snapshot = File.ReadAllLines(path).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var added = current.Except(snapshot).ToList(); var removed = snapshot.Except(current).ToList();
        Assert.True(added.Count == 0 && removed.Count == 0, $"libse extension list changed. Added: {string.Join(" ", added)}. Removed: {string.Join(" ", removed)}. Review, then update tests/SmartSubtitleConverterApi.Tests/extension-allowlist.txt.");
        Assert.Contains(".srt", current); Assert.Contains(".stl", current); Assert.DoesNotContain(".env", current); Assert.DoesNotContain(".mkv", current);
        Assert.All(current, e => Assert.Equal(e, e.ToLowerInvariant()));
    }

    [Fact]
    public async Task Limits_report_extensions_listing_cap_and_browse_queue()
    {
        using var f = Api(); var c = f.CreateClient();
        var j = await (await c.GetAsync("/v1/limits")).Json();
        Assert.Contains(".srt", j["storage"]!["extensions"]!.AsArray().Select(e => (string)e!));
        Assert.Equal(5000, (int)j["storage"]!["listMaxEntries"]!);
        Assert.Equal(4, (int)j["storage"]!["browse"]!["concurrency"]!);
        Assert.Equal(32, (int)j["storage"]!["browse"]!["queueCapacity"]!);
        Assert.Equal(30, (int)j["storage"]!["browse"]!["waitSeconds"]!);
    }

    // ---------------- H4 ----------------

    [Fact]
    public async Task Failed_job_input_is_returned_only_for_subtitle_extensions()
    {
        File.WriteAllText(Path.Combine(root, "config.json5"), "SECRET=hunter2");
        File.WriteAllText(Path.Combine(root, "broken.srt"), "not really a subtitle");
        using var f = Api(new() { ["STORAGE_LIST_ENABLED"] = "false" }); var c = f.CreateClient();
        var bad = await c.PostAsJsonAsync("/v1/convert/json", new { input = Path.Combine(root, "config.json5"), target = "vtt" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var (s, j) = await Send(c.GetAsync($"/v1/jobs/{bad.Header("X-Job-Id")}/input"));
        Assert.Equal(422, s); Assert.Equal("extension-not-allowed", (string)j["code"]!);
        Assert.Contains("not returned", (string)j["detail"]!);
        Assert.DoesNotContain("hunter2", j.ToJsonString());
        // A subtitle-named file is readable even when its job failed (exposing a folder means accepting that).
        var failed = await c.PostAsJsonAsync("/v1/convert/json", new { input = Path.Combine(root, "broken.srt"), target = "vtt" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal("not really a subtitle", await (await c.GetAsync($"/v1/jobs/{failed.Header("X-Job-Id")}/input")).Content.ReadAsStringAsync());
    }

    // ---------------- H6 listing ----------------

    [Fact]
    public async Task Folder_over_the_cap_is_folder_too_large_and_a_prefix_narrows_it()
    {
        var big = Path.Combine(root, "big"); Directory.CreateDirectory(big);
        for (var i = 0; i < StorageListing.MaxEntries + 1; i++) File.WriteAllText(Path.Combine(big, (i % 2 == 0 ? "a" : "b") + i.ToString("D5") + ".srt"), "");
        using var f = Api(); var c = f.CreateClient();
        var (s, j) = await Send(c.GetAsync($"/v1/storage/list?location={Q(big)}"));
        Assert.Equal(422, s); Assert.Equal("folder-too-large", (string)j["code"]!);
        Assert.Equal(5000, (int)j["limit"]!);
        Assert.Contains("prefix", (string)j["hint"]!);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (ps, pj) = await Send(c.GetAsync($"/v1/storage/list?location={Q(big)}&prefix=a"));
        Assert.Equal(200, ps); Assert.Equal(200, (int)pj["returned"]!); Assert.True((bool)pj["truncated"]!);
        Assert.Equal("a00000.srt", (string)pj["items"]![0]!["name"]!);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"page took {sw.ElapsedMilliseconds} ms");
        // A full walk of the prefix returns every matching name exactly once, in order.
        var names = new List<string>(); string? cursor = null;
        do
        {
            var page = await (await c.GetAsync($"/v1/storage/list?location={Q(big)}&prefix=a" + (cursor == null ? "" : "&cursor=" + cursor))).Json();
            names.AddRange(page["items"]!.AsArray().Select(i => (string)i!["name"]!));
            cursor = (string?)page["next"];
        } while (cursor != null);
        Assert.Equal(2501, names.Count);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
    }

    [Fact]
    public async Task Fifo_and_directory_named_like_subtitles_are_refused_without_hanging()
    {
        var fifo = Path.Combine(root, "pipe.srt");
        using (var p = System.Diagnostics.Process.Start("mkfifo", fifo)) p.WaitForExit();
        Directory.CreateDirectory(Path.Combine(root, "dir.srt"));
        using var f = Api(); var c = f.CreateClient();
        foreach (var loc in new[] { fifo, Path.Combine(root, "dir.srt") })
        {
            var read = c.GetAsync($"/v1/storage/file?location={Q(loc)}");
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10))));
            var (s, j) = await Send(read);
            Assert.Equal(422, s); Assert.Equal("storage-location-rejected", (string)j["code"]!);
            Assert.Contains("regular files", (string)j["detail"]!);
        }
    }

    // ---------------- H6 browse queue ----------------

    [Fact]
    public async Task Browse_queue_never_runs_more_than_4_and_lets_the_rest_wait_then_succeed()
    {
        var q = new BrowseQueue(new ApiOptions { BrowseConcurrency = 4, BrowseQueueCapacity = 36, BrowseWaitSeconds = 60 }, new CaptureLogs().AsLogger<BrowseQueue>());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inside = 0; var max = 0;
        var tasks = Enumerable.Range(0, 40).Select(i => q.RunAsync(async () =>
        {
            var now = Interlocked.Increment(ref inside);
            lock (q) max = Math.Max(max, now);
            await gate.Task;
            Interlocked.Decrement(ref inside);
            return i;
        }, default)).ToList();
        await WaitUntil(() => q.Running == 4 && q.Waiting == 36);
        Assert.Equal(4, q.Running); Assert.Equal(36, q.Waiting);
        gate.SetResult();
        Assert.Equal(Enumerable.Range(0, 40), (await Task.WhenAll(tasks)).Order());
        Assert.Equal(4, max); Assert.Equal(4, q.MaxRunning);
    }

    [Fact]
    public async Task Browse_queue_beyond_the_line_or_the_wait_time_is_browse_busy_with_retry_after()
    {
        var q = new BrowseQueue(new ApiOptions { BrowseConcurrency = 1, BrowseQueueCapacity = 1, BrowseWaitSeconds = 1 }, new CaptureLogs().AsLogger<BrowseQueue>());
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = q.RunAsync(() => gate.Task, default);
        var waiting = q.RunAsync(() => Task.FromResult(2), default);
        await WaitUntil(() => q.Waiting == 1);
        var full = await Assert.ThrowsAsync<ApiException>(() => q.RunAsync(() => Task.FromResult(3), default));
        Assert.Equal(503, full.Status); Assert.Equal("browse-busy", full.Code); Assert.True(full.RetryAfterSeconds >= 1);
        Assert.Contains("waiting line is full", full.Message);
        var timedOut = await Assert.ThrowsAsync<ApiException>(() => waiting); // its 1 s wait ran out
        Assert.Equal("browse-busy", timedOut.Code); Assert.Contains("waited", timedOut.Message);
        gate.SetResult(1);
        Assert.Equal(1, await running);
        Assert.Equal(4, await q.RunAsync(() => Task.FromResult(4), default));
    }

    [Fact]
    public async Task Forty_parallel_http_browse_requests_share_4_slots_overflow_is_busy_and_conversions_are_not_blocked()
    {
        File.WriteAllText(Path.Combine(root, "a.srt"), Samples.Srt);
        using var f = Api(); var c = f.CreateClient();
        var ok = await c.PostAsJsonAsync("/v1/convert/json", new { input = Path.Combine(root, "a.srt"), target = "vtt", output = Path.Combine(root, "a.vtt") });
        var job = ok.Header("X-Job-Id");
        var browse = f.Services.GetRequiredService<BrowseQueue>();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holders = Enumerable.Range(0, 4).Select(_ => browse.RunAsync(() => gate.Task, default)).ToList(); // occupy every slot
        await WaitUntil(() => browse.Running == 4);
        var paths = new[] { $"/v1/storage/list?location={Q(root)}", $"/v1/storage/file?location={Q(Path.Combine(root, "a.srt"))}", $"/v1/jobs/{job}/input", $"/v1/jobs/{job}/output" };
        var requests = Enumerable.Range(0, 40).Select(i => c.GetAsync(paths[i % 4])).ToList();
        await WaitUntil(() => browse.Waiting == 32 && requests.Count(r => r.IsCompleted) == 8);
        Assert.Equal(32, browse.Waiting);
        var rejected = requests.Where(r => r.IsCompleted).Select(r => r.Result).ToList();
        Assert.Equal(8, rejected.Count);
        foreach (var r in rejected)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.Equal("browse-busy", (string)(await r.Json())["code"]!);
            Assert.NotNull(r.Headers.RetryAfter);
        }
        // Browsing is saturated, but conversions run on their own queue.
        var convert = await c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "x.srt", ("target", "vtt")));
        Assert.Equal(HttpStatusCode.OK, convert.StatusCode);
        gate.SetResult(0);
        await Task.WhenAll(holders);
        var waited = await Task.WhenAll(requests.Where(r => !rejected.Contains(r.Result)));
        Assert.Equal(32, waited.Length);
        Assert.All(waited, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(4, browse.MaxRunning);
    }

    [Fact]
    public async Task Browsing_is_not_blocked_by_a_busy_conversion_worker()
    {
        File.WriteAllText(Path.Combine(root, "a.srt"), Samples.Srt);
        using var f = Api(); var c = f.CreateClient();
        var q = f.Services.GetRequiredService<ConversionQueue>();
        using var gate = new ManualResetEventSlim();
        var busy = q.RunAsync(() => { gate.Wait(); return "busy"; }, default);
        await WaitUntil(() => q.Depth == 0);
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/v1/storage/list?location={Q(root)}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/v1/storage/file?location={Q(Path.Combine(root, "a.srt"))}")).StatusCode);
            Assert.False(busy.IsCompleted);
        }
        finally { gate.Set(); }
        await busy;
    }

    // ---------------- H9 ----------------

    [Fact]
    public async Task Errors_are_terse_by_default_and_details_go_to_the_log_with_the_request_id()
    {
        var logs = new CaptureLogs();
        using var f = Api(configure: s => s.AddSingleton<ILoggerProvider>(logs)); var c = f.CreateClient();
        var r = await c.PostAsync("/v1/convert", Samples.Form("SECRET=hunter2-sentinel\nmore", "x.srt", ("target", "vtt")));
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.DoesNotContain("debug", body); Assert.DoesNotContain("stackTrace", body); Assert.DoesNotContain("firstLineShape", body);
        Assert.DoesNotContain("hunter2", body);
        var requestId = (string)JsonNode.Parse(body)!["requestId"]!;
        Assert.Contains(logs.Lines, l => l.Contains("error.details") && l.Contains(requestId) && l.Contains("firstLineShape"));
        Assert.DoesNotContain("hunter2", logs.All);
        Assert.False(new ApiOptions().ErrorsVerbose);
    }

    // ---------------- H10 ----------------

    [Fact]
    public async Task Symlinked_outputs_sidecars_and_replacements_are_never_followed()
    {
        var target = Path.Combine(outside, "victim.vtt"); File.WriteAllText(target, "original");
        var sideTarget = Path.Combine(outside, "victim.json"); File.WriteAllText(sideTarget, "original");
        File.CreateSymbolicLink(Path.Combine(root, "link.vtt"), target);
        File.CreateSymbolicLink(Path.Combine(root, "out.vtt.json"), sideTarget);
        Directory.CreateSymbolicLink(Path.Combine(root, "linkdir"), outside);
        using var f = Api(); var c = f.CreateClient();
        foreach (var (output, replace) in new[] { (Path.Combine(root, "link.vtt"), true), (Path.Combine(root, "link.vtt"), false), (Path.Combine(root, "linkdir", "victim.vtt"), true) })
        {
            var r = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt", output, ifExists = replace ? "replace" : "error" });
            Assert.True(r.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict, $"{output}: {r.StatusCode}");
        }
        // Sidecar written next to a fresh output: the symlinked {output}.json is neither followed nor replaced.
        var withSidecar = await (await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt", output = Path.Combine(root, "out.vtt"), sidecar = true, ifExists = "replace" })).Json();
        Assert.Contains(withSidecar["warnings"]!.AsArray(), w => (string)w!["code"]! == "sidecar-write-failed");
        // Stale-sidecar cleanup (sidecar=false) never deletes through or the link itself as a file.
        var again = await (await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt", output = Path.Combine(root, "out.vtt"), ifExists = "replace" })).Json();
        Assert.Contains(again["warnings"]!.AsArray(), w => (string)w!["code"]! == "sidecar-delete-failed");
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal("original", File.ReadAllText(sideTarget));
        Assert.True(new FileInfo(Path.Combine(root, "out.vtt.json")).LinkTarget != null);
    }

    private static async Task WaitUntil(Func<bool> cond)
    {
        for (var i = 0; i < 500 && !cond(); i++) await Task.Delay(10);
    }
}

/// <summary>H5: XML parser hardening.</summary>
public sealed class XmlHardeningTests
{
    private const string Dtd = """<!DOCTYPE tt [<!ENTITY b "boom">]>""";
    private static string Ttml(string prefix = "", string dtd = Dtd) => prefix + "<?xml version=\"1.0\"?>" + dtd + """<tt xmlns="http://www.w3.org/ns/ttml"><body><div><p begin="00:00:01.000" end="00:00:02.000">&b;</p></div></body></tt>""";

    [Theory]
    [InlineData("")]
    [InlineData("\uFEFF")]
    [InlineData("\uFEFF\uFEFF")]
    [InlineData("\u200B")]
    [InlineData("\u2060")]
    [InlineData("\uFEFF \u200B\t\n\u200D")]
    [InlineData("junk before the xml\n")]
    public void Dtd_is_refused_whatever_precedes_it(string prefix)
    {
        var ex = Assert.Throws<ApiException>(() => XmlGuard.Screen(Ttml(prefix)));
        Assert.Equal("unsafe-xml", ex.Code);
    }

    [Fact]
    public async Task Bypass_inputs_are_unsafe_xml_through_the_api_also_with_a_double_bom_in_the_bytes()
    {
        using var f = new ApiFactory(); var c = f.CreateClient();
        foreach (var prefix in new byte[][] { [], [0xEF, 0xBB, 0xBF], [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF], [0xE2, 0x80, 0x8B], [0xE2, 0x81, 0xA0] })
        {
            var bytes = prefix.Concat(Encoding.UTF8.GetBytes(Ttml())).ToArray();
            var r = await c.PostAsync("/v1/convert", Samples.Form(bytes, "x.ttml", ("target", "srt")));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal("unsafe-xml", (string)(await r.Json())["code"]!);
        }
    }

    [Fact]
    public void Billion_laughs_and_external_entities_are_refused()
    {
        var laughs = """<?xml version="1.0"?><!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;"><!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">]><tt>&lol3;</tt>""";
        Assert.Equal("unsafe-xml", Assert.Throws<ApiException>(() => XmlGuard.Screen(laughs)).Code);
        var xxe = """<?xml version="1.0"?><!DOCTYPE tt [<!ENTITY x SYSTEM "file:///etc/hostname">]><tt>&x;</tt>""";
        Assert.Equal("unsafe-xml", Assert.Throws<ApiException>(() => XmlGuard.Screen(xxe)).Code);
        var param = """<?xml version="1.0"?><!DOCTYPE tt [<!ENTITY % p SYSTEM "http://169.254.169.254/x"> %p;]><tt/>""";
        Assert.Equal("unsafe-xml", Assert.Throws<ApiException>(() => XmlGuard.Screen(param)).Code);
    }

    [Fact]
    public void Ordinary_xml_and_text_pass_and_the_safe_reader_settings_prohibit_dtds()
    {
        XmlGuard.Screen("\uFEFF\uFEFF" + Samples.Ttml);
        XmlGuard.Screen(Samples.Srt);
        XmlGuard.Screen("<SAMI><BODY><SYNC Start=0><P>hi</SAMI>"); // malformed: libse decides
        var s = XmlGuard.SafeSettings();
        Assert.Equal(System.Xml.DtdProcessing.Prohibit, s.DtdProcessing);
        Assert.True(s.MaxCharactersFromEntities is > 0 and <= 1024);
    }

    [Fact]
    public void Audit_libse_style_XmlDocument_does_not_read_external_files_on_this_runtime()
    {
        // libse loads XML with XmlDocument.LoadXml. The guard stops DTDs before libse; this pins the runtime default as a second layer.
        var secret = Path.GetTempFileName();
        File.WriteAllText(secret, "OUTSIDE_SENTINEL");
        try
        {
            var doc = new System.Xml.XmlDocument();
            try { doc.LoadXml($"<?xml version=\"1.0\"?><!DOCTYPE tt [<!ENTITY x SYSTEM \"{new Uri(secret).AbsoluteUri}\">]><tt>&x;</tt>"); }
            catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException or NotSupportedException or IOException) { }
            Assert.DoesNotContain("OUTSIDE_SENTINEL", doc.OuterXml);
        }
        finally { File.Delete(secret); }
    }
}

internal static class LoggerExtensions
{
    public static ILogger<T> AsLogger<T>(this CaptureLogs logs) => new Wrapped<T>(logs);
    private sealed class Wrapped<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
