using System.Net.Http.Json;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartSubtitleConverterApi.Infrastructure;
using SmartSubtitleConverterApi.Storage;
namespace SmartSubtitleConverterApi.Tests;

/// Browse and read by location (STORAGE_LIST_ENABLED): gate, locations, folder listing pages and cursors, file reads.
public sealed class StorageListTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "se-list-" + Guid.NewGuid());
    private readonly string outside = Path.Combine(Path.GetTempPath(), "se-list-outside-" + Guid.NewGuid());
    public StorageListTests() { Directory.CreateDirectory(root); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "secret.srt"), Samples.Srt); }
    public void Dispose() { Directory.Delete(root, true); Directory.Delete(outside, true); }

    private ApiFactory Api(bool list = true, Dictionary<string, string?>? extra = null, Action<IServiceCollection>? configure = null)
    {
        var s = new Dictionary<string, string?> { ["STORAGE_FILE_ROOTS"] = root, ["STORAGE_LIST_ENABLED"] = list ? "true" : "false" };
        foreach (var (k, v) in extra ?? []) s[k] = v;
        return ApiFactory.With(s, configure);
    }
    private static string Q(string v) => Uri.EscapeDataString(v);
    private static async Task<(int Status, System.Text.Json.Nodes.JsonObject Body)> Get(HttpClient c, string url)
    {
        var r = await c.GetAsync(url); return ((int)r.StatusCode, await r.Json());
    }
    private static string Code(System.Text.Json.Nodes.JsonObject j) => (string)j["code"]!;

    /// Walks every page; returns item names (relative to the listed folder) in order.
    private static async Task<List<string>> Walk(HttpClient c, string location, string? prefix = null, Func<int, Task>? between = null)
    {
        var names = new List<string>(); string? cursor = null; var page = 0;
        do
        {
            var url = $"/v1/storage/list?location={Q(location)}" + (prefix != null ? $"&prefix={Q(prefix)}" : "") + (cursor != null ? $"&cursor={cursor}" : "");
            var (status, j) = await Get(c, url);
            Assert.True(status == 200, j.ToJsonString());
            var items = j["items"]!.AsArray();
            Assert.Equal(items.Count, (int)j["returned"]!); Assert.True(items.Count <= StorageListing.PageSize);
            names.AddRange(items.Select(i => (string)i!["name"]!));
            cursor = (bool)j["truncated"]! ? (string)j["next"]! : null;
            if (!(bool)j["truncated"]!) Assert.Null(j["next"]);
            if (between != null && cursor != null) await between(++page);
        } while (cursor != null);
        return names;
    }

    [Fact]
    public async Task Gate_off_hides_locations_listing_and_file_reads_but_job_files_still_work()
    {
        using var f = Api(list: false, new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["HTTP_ALLOWED_HOSTS"] = "mam.example.com" }); var c = f.CreateClient();
        File.WriteAllText(Path.Combine(root, "a.srt"), Samples.Srt);
        foreach (var url in new[] { "/v1/storage/locations", $"/v1/storage/list?location={Q(root)}", $"/v1/storage/file?location={Q(Path.Combine(root, "a.srt"))}", "/v1/storage/list", "/v1/storage/list?location=s3%3A%2F%2Fsubs%2F" })
        {
            var (status, j) = await Get(c, url);
            Assert.Equal(403, status); Assert.Equal("storage-list-disabled", Code(j)); Assert.Equal("Storage listing disabled", (string)j["title"]!);
            Assert.Contains("STORAGE_LIST_ENABLED=true", (string)j["hint"]!);
            // No allowlist leaks through the refusal.
            Assert.DoesNotContain(root, j.ToJsonString()); Assert.DoesNotContain("subs", j.ToJsonString()); Assert.DoesNotContain("mam.example.com", j.ToJsonString());
        }
        var limits = await (await c.GetAsync("/v1/limits")).Json();
        Assert.False((bool)limits["storage"]!["list"]!); Assert.Empty(limits["storage"]!["listSchemes"]!.AsArray());
        // Reading a job's stored output (by job id) is not gated.
        var output = Path.Combine(root, "out.vtt");
        var conv = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, output, target = "vtt" });
        Assert.Equal(200, (int)conv.StatusCode);
        var file = await c.GetAsync($"/v1/jobs/{conv.Header("X-Job-Id")}/output");
        Assert.Equal(200, (int)file.StatusCode); Assert.Equal(File.ReadAllBytes(output), await file.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Gate_is_enforced_per_request_with_api_keys_too()
    {
        using var f = Api(list: false, new() { ["API_KEYS"] = "k1" }); var c = f.CreateClient();
        Assert.Equal(401, (int)(await c.GetAsync("/v1/storage/locations")).StatusCode);
        c.DefaultRequestHeaders.Authorization = new("Bearer", "k1");
        Assert.Equal(403, (int)(await c.GetAsync("/v1/storage/locations")).StatusCode);
        using var on = Api(list: true, new() { ["API_KEYS"] = "k1" }); var c2 = on.CreateClient();
        Assert.Equal(401, (int)(await c2.GetAsync("/v1/storage/locations")).StatusCode); // the gate never bypasses auth
    }

    [Fact]
    public async Task Locations_report_file_roots_s3_buckets_and_prefixes_and_https_as_not_listable_without_secrets()
    {
        using var f = Api(extra: new() { ["S3_ALLOWED_BUCKETS"] = "subs,tmp", ["S3_ALLOWED_PREFIXES"] = "in/,out/show", ["HTTP_ALLOWED_HOSTS"] = "mam.example.com",
            ["S3_ENDPOINT"] = "http://minio.internal:9000", ["API_KEYS"] = "k-secret" });
        var c = f.CreateClient(); c.DefaultRequestHeaders.Authorization = new("Bearer", "k-secret");
        var (status, j) = await Get(c, "/v1/storage/locations");
        Assert.Equal(200, status);
        var locs = j["locations"]!.AsArray().Select(l => ((string)l!["uri"]!, (string)l["scheme"]!, (bool)l["listable"]!, (bool)l["readable"]!, (string?)l["prefix"])).ToList();
        Assert.Equal(new[]
        {
            (new Uri(root + "/").AbsoluteUri, "file", true, true, (string?)null),
            ("s3://subs/in/", "s3", true, true, null), ("s3://subs/out/", "s3", true, true, "show"),
            ("s3://tmp/in/", "s3", true, true, null), ("s3://tmp/out/", "s3", true, true, "show"),
            ("https://mam.example.com/", "https", false, false, null),
        }, locs);
        var text = j.ToJsonString();
        Assert.DoesNotContain("minio.internal", text); Assert.DoesNotContain("k-secret", text);
        var limits = await (await c.GetAsync("/v1/limits")).Json();
        Assert.True((bool)limits["storage"]!["list"]!);
        Assert.Equal(new[] { "file", "s3" }, limits["storage"]!["listSchemes"]!.AsArray().Select(s => (string)s!).ToArray());
    }

    [Fact]
    public async Task Filesystem_walk_returns_every_entry_once_in_utf8_order_skipping_hidden_symlinks_and_specials()
    {
        var expected = new List<string>();
        for (var i = 0; i < 450; i++) { var n = $"show_{i:000}.srt"; File.WriteAllText(Path.Combine(root, n), "x" + i); expected.Add(n); }
        foreach (var n in new[] { "Émission.srt", "zz-日本.srt", "Zebra.srt", "émission.srt" }) { File.WriteAllText(Path.Combine(root, n), Samples.Srt); expected.Add(n); }
        Directory.CreateDirectory(Path.Combine(root, "season1")); File.WriteAllText(Path.Combine(root, "season1", "ep1.srt"), Samples.Srt); expected.Add("season1/");
        File.WriteAllText(Path.Combine(root, ".hidden.srt"), "x");
        File.CreateSymbolicLink(Path.Combine(root, "link.srt"), Path.Combine(outside, "secret.srt"));
        Directory.CreateSymbolicLink(Path.Combine(root, "linkdir"), outside);
        System.Diagnostics.Process.Start("mkfifo", Path.Combine(root, "pipe.srt"))!.WaitForExit();
        expected.Sort(StorageListing.Utf8Order);
        using var f = Api(); var c = f.CreateClient();
        foreach (var location in new[] { root, root + "/", new Uri(root + "/").AbsoluteUri })
        {
            var names = await Walk(c, location);
            Assert.Equal(expected, names);
        }
        var (_, first) = await Get(c, $"/v1/storage/list?location={Q(root)}");
        Assert.Equal(200, (int)first["returned"]!); Assert.True((bool)first["truncated"]!);
        Assert.Equal(new Uri(root + "/").AbsoluteUri, (string)first["location"]!);
        var item = first["items"]!.AsArray().First(i => (string)i!["name"]! == "show_000.srt")!;
        Assert.Equal("file", (string)item["type"]!); Assert.Equal(2, (long)item["size"]!); Assert.NotNull(item["modified"]);
        Assert.Equal(new Uri(Path.Combine(root, "show_000.srt")).AbsoluteUri, (string)item["uri"]!);
        // Prefix filters by name start; '/' descends one folder.
        Assert.Equal(Enumerable.Range(100, 100).Select(i => $"show_{i:000}.srt"), await Walk(c, root, "show_1"));
        Assert.Equal(new[] { "ep1.srt" }, await Walk(c, root, "season1/"));
        Assert.Equal(new[] { "ep1.srt" }, await Walk(c, root, "season1/e"));
        Assert.Empty(await Walk(c, root, "nothing-matches"));
        var (_, folder) = await Get(c, $"/v1/storage/list?location={Q(root)}&prefix=season");
        Assert.Equal("folder", (string)folder["items"]![0]!["type"]!); Assert.Null(folder["items"]![0]!["size"]);
        Assert.Equal(new Uri(Path.Combine(root, "season1") + "/").AbsoluteUri, (string)folder["items"]![0]!["uri"]!);
    }

    [Fact]
    public async Task Filesystem_walk_under_mutation_returns_stable_entries_exactly_once()
    {
        for (var i = 0; i < 500; i++) File.WriteAllText(Path.Combine(root, $"f{i:000}.srt"), "x");
        using var f = Api(); var c = f.CreateClient();
        var names = await Walk(c, root, between: page =>
        {
            if (page != 1) return Task.CompletedTask;
            File.Delete(Path.Combine(root, "f010.srt")); // already returned
            File.Delete(Path.Combine(root, "f300.srt")); // not yet returned
            File.WriteAllText(Path.Combine(root, "f005b.srt"), "x"); // sorts before the cursor
            File.WriteAllText(Path.Combine(root, "f400b.srt"), "x"); // sorts after the cursor
            return Task.CompletedTask;
        });
        Assert.Equal(names.Count, names.Distinct().Count());
        var stable = Enumerable.Range(0, 500).Where(i => i is not (10 or 300)).Select(i => $"f{i:000}.srt");
        Assert.All(stable, n => Assert.Single(names, n));
        Assert.Contains("f010.srt", names); Assert.DoesNotContain("f300.srt", names);
        Assert.DoesNotContain("f005b.srt", names); Assert.Contains("f400b.srt", names);
    }

    [Fact]
    public async Task Listing_rejects_outside_traversal_symlinked_folders_https_and_bad_prefixes()
    {
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
        using var f = Api(extra: new() { ["HTTP_ALLOWED_HOSTS"] = "mam.example.com" }); var c = f.CreateClient();
        async Task Expect(int status, string code, string location, string? prefix = null)
        {
            var (s, j) = await Get(c, $"/v1/storage/list?location={Q(location)}" + (prefix != null ? $"&prefix={Q(prefix)}" : ""));
            Assert.True(status == s && code == Code(j), $"{location} {prefix}: {s} {j.ToJsonString()}");
            Assert.DoesNotContain("secret.srt", j.ToJsonString());
        }
        await Expect(422, "storage-location-rejected", outside);
        await Expect(422, "storage-location-rejected", root + "/../" + Path.GetFileName(outside));
        await Expect(422, "storage-location-rejected", new Uri(outside).AbsoluteUri);
        await Expect(422, "storage-location-rejected", "file://host" + root);
        await Expect(422, "storage-location-rejected", root + "/escape");
        await Expect(422, "storage-location-rejected", root + "/sub/./");
        await Expect(422, "storage-location-rejected", "relative/folder");
        await Expect(404, "storage-not-found", root + "/missing");
        await Expect(422, "storage-not-listable", "https://mam.example.com/folder/");
        await Expect(422, "storage-location-rejected", "s3://subs/"); // S3 disabled: bucket not allowlisted
        await Expect(400, "invalid-request", root, "../" + Path.GetFileName(outside));
        await Expect(400, "invalid-request", root, "/etc");
        await Expect(400, "invalid-request", root, "sub\\x");
        await Expect(422, "storage-location-rejected", root, "escape/");
        var (status, missing) = await Get(c, "/v1/storage/list");
        Assert.Equal(400, status); Assert.Equal("invalid-request", Code(missing));
    }

    [Fact]
    public async Task Cursors_are_bound_to_location_and_prefix_and_malformed_ones_are_rejected()
    {
        for (var i = 0; i < 260; i++) File.WriteAllText(Path.Combine(root, $"a{i:000}.srt"), "x");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        using var f = Api(); var c = f.CreateClient();
        var (_, page) = await Get(c, $"/v1/storage/list?location={Q(root)}");
        var next = (string)page["next"]!;
        var (_, p2) = await Get(c, $"/v1/storage/list?location={Q(root)}&cursor={next}");
        Assert.Equal("a200.srt", (string)p2["items"]![0]!["name"]!);
        string Forge(object o) => Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(o))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var binding = StorageListing.Binding(StorageListing.ParseFolder(root), null);
        foreach (var (url, why) in new[]
        {
            ($"/v1/storage/list?location={Q(root)}&prefix=a&cursor={next}", "other prefix"),
            ($"/v1/storage/list?location={Q(root + "/sub")}&cursor={next}", "other folder"),
            ($"/v1/storage/list?location={Q(root)}&cursor=not%20a%20cursor", "spaces"),
            ($"/v1/storage/list?location={Q(root)}&cursor={next[..(next.Length / 2)]}", "half"),
            ($"/v1/storage/list?location={Q(root)}&cursor=Zm9v", "random"),
            ($"/v1/storage/list?location={Q(root)}&cursor={Forge(new { v = 2, b = binding, a = "a100" })}", "version"),
            ($"/v1/storage/list?location={Q(root)}&cursor={Forge(new { v = 1, b = binding, t = "token" })}", "s3 cursor on a folder"),
            ($"/v1/storage/list?location={Q(root)}&cursor={Forge(new { v = 1, b = binding })}", "empty"),
            ($"/v1/storage/list?location={Q(root)}&cursor={new string('A', 5000)}", "too long"),
        })
        {
            var (status, j) = await Get(c, url);
            Assert.True(status == 400 && Code(j) == "storage-cursor-invalid", $"{why}: {status} {j.ToJsonString()}");
            Assert.Equal("Storage cursor invalid", (string)j["title"]!);
        }
        // A forged name cursor only moves within the same allowed folder.
        var (ok, forged) = await Get(c, $"/v1/storage/list?location={Q(root)}&cursor={Forge(new { v = 1, b = binding, a = "a249.srt" })}");
        Assert.Equal(200, ok); Assert.Equal("a250.srt", (string)forged["items"]![0]!["name"]!);
    }

    [Fact]
    public async Task File_reads_return_exact_bytes_inside_the_allowlist_only()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'1', 0x0A, 0x00, 0xFF };
        File.WriteAllBytes(Path.Combine(root, "exact.stl"), bytes);
        Directory.CreateDirectory(Path.Combine(root, "dir"));
        File.CreateSymbolicLink(Path.Combine(root, "link.srt"), Path.Combine(outside, "secret.srt"));
        Directory.CreateSymbolicLink(Path.Combine(root, "linkdir"), outside);
        using var f = Api(extra: new() { ["HTTP_ALLOWED_HOSTS"] = "mam.example.com", ["MAX_UPLOAD_BYTES"] = "64" }); var c = f.CreateClient();
        var r = await c.GetAsync($"/v1/storage/file?location={Q(new Uri(Path.Combine(root, "exact.stl")).AbsoluteUri)}");
        Assert.Equal(200, (int)r.StatusCode); Assert.Equal(bytes, await r.Content.ReadAsByteArrayAsync());
        Assert.Contains("exact.stl", r.Header("Content-Disposition"));
        File.WriteAllText(Path.Combine(root, "big.srt"), new string('x', 65));
        foreach (var (location, status, code) in new[]
        {
            (Path.Combine(outside, "secret.srt"), 422, "storage-location-rejected"),
            (root + "/../" + Path.GetFileName(outside) + "/secret.srt", 422, "storage-location-rejected"),
            (Path.Combine(root, "link.srt"), 422, "storage-location-rejected"),
            (Path.Combine(root, "linkdir", "secret.srt"), 422, "storage-location-rejected"),
            (Path.Combine(root, "dir"), 422, "extension-not-allowed"),
            (Path.Combine(root, "dir") + "/", 422, "storage-location-rejected"),
            (Path.Combine(root, "missing.srt"), 404, "storage-not-found"),
            (Path.Combine(root, "big.srt"), 413, "payload-too-large"),
            ("https://mam.example.com/show.srt", 422, "storage-not-listable"),
            ("https://169.254.169.254/latest/meta-data", 422, "storage-not-listable"),
            ("s3://subs/in/show.srt", 422, "storage-location-rejected"),
            ("file://host/etc/passwd", 422, "storage-location-rejected"),
        })
        {
            var (s, j) = await Get(c, $"/v1/storage/file?location={Q(location)}");
            Assert.True(s == status && Code(j) == code, $"{location}: {s} {j.ToJsonString()}");
            Assert.DoesNotContain("Hello", j.ToJsonString());
        }
    }

    /// In-memory bucket honouring ListObjectsV2 prefix, delimiter, MaxKeys and continuation tokens, in UTF-8 key order.
    private sealed class FakeBucket(IEnumerable<string> keys) : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://unused.invalid", AuthenticationRegion = "us-east-1" })
    {
        public readonly SortedSet<string> Keys = new(keys, StorageListing.Utf8Order);
        public readonly List<ListObjectsV2Request> Requests = [];
        public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var after = request.ContinuationToken == null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(request.ContinuationToken));
            var response = new ListObjectsV2Response { S3Objects = [], CommonPrefixes = [] };
            string? last = null; var n = 0;
            foreach (var key in Keys.Where(k => k.StartsWith(request.Prefix ?? "", StringComparison.Ordinal)))
            {
                var rest = key[(request.Prefix ?? "").Length..];
                var slash = request.Delimiter == null ? -1 : rest.IndexOf(request.Delimiter, StringComparison.Ordinal);
                var entry = slash < 0 ? key : key[..((request.Prefix ?? "").Length + slash + 1)];
                if (after != null && StorageListing.Utf8Compare(entry, after) <= 0) continue;
                if (entry == last) continue;
                if (n == request.MaxKeys) { response.IsTruncated = true; response.NextContinuationToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(last!)); return Task.FromResult(response); }
                if (slash < 0) response.S3Objects.Add(new S3Object { Key = key, Size = key.Length, LastModified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) });
                else response.CommonPrefixes.Add(entry);
                last = entry; n++;
            }
            response.IsTruncated = false;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task S3_walk_uses_continuation_tokens_and_stays_inside_allowed_prefixes()
    {
        var keys = Enumerable.Range(0, 430).Select(i => $"in/show_{i:000}.srt").Concat(["in/", "in/season1/ep1.srt", "in/season1/ep2.srt", "in/Ünïcode.srt", "out/x.srt", "private/secret.srt"]).ToList();
        using var bucket = new FakeBucket(keys);
        var opts = new ApiOptions { S3AllowedBuckets = ["subs"], S3AllowedPrefixes = ["in/"], StorageListEnabled = true };
        using var f = Api(extra: new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_ALLOWED_PREFIXES"] = "in/" }, configure: s => { s.RemoveAll<S3Storage>(); s.AddSingleton(new S3Storage(opts, bucket)); });
        var c = f.CreateClient();
        var names = await Walk(c, "s3://subs/in/");
        var expected = Enumerable.Range(0, 430).Select(i => $"show_{i:000}.srt").Append("season1/").Append("Ünïcode.srt").Order(StorageListing.Utf8Order).ToList();
        Assert.Equal(expected, names); // folder marker "in/" skipped, sub-folder as a common prefix, each once
        Assert.All(bucket.Requests, r => { Assert.Equal("/", r.Delimiter); Assert.Equal(StorageListing.PageSize, r.MaxKeys); Assert.Equal("subs", r.BucketName); });
        Assert.Equal(new[] { null, "token", "token" }, bucket.Requests.Select(r => r.ContinuationToken == null ? null : "token"));
        var (_, page) = await Get(c, $"/v1/storage/list?location={Q("s3://subs/in/")}&prefix={Q("season1/")}");
        Assert.Equal(new[] { "s3://subs/in/season1/ep1.srt", "s3://subs/in/season1/ep2.srt" }, page["items"]!.AsArray().Select(i => (string)i!["uri"]!));
        Assert.Equal(18, (long)page["items"]![0]!["size"]!); Assert.Equal("file", (string)page["items"]![0]!["type"]!);
        var (_, uni) = await Get(c, $"/v1/storage/list?location={Q("s3://subs/in/")}&prefix={Q("Ü")}");
        Assert.Equal("s3://subs/in/%C3%9Cn%C3%AFcode.srt", (string)uni["items"]![0]!["uri"]!);
        var before = bucket.Requests.Count;
        foreach (var (location, prefix) in new[] { ("s3://subs/", (string?)null), ("s3://subs/", "pr"), ("s3://subs/private/", null), ("s3://other/in/", null), ("s3://subs/in/../private/", null), ("s3://user:pw@subs/in/", null), ("s3://subs/in/?x=1", null) })
        {
            var (s, j) = await Get(c, $"/v1/storage/list?location={Q(location)}" + (prefix != null ? $"&prefix={prefix}" : ""));
            Assert.True(s == 422 && Code(j) == "storage-location-rejected", $"{location}: {s} {j.ToJsonString()}");
        }
        Assert.Equal(before, bucket.Requests.Count); // allowlists are checked before any S3 call
        var (_, p1) = await Get(c, $"/v1/storage/list?location={Q("s3://subs/in/")}");
        var (fs, wrong) = await Get(c, $"/v1/storage/list?location={Q(root)}&cursor={(string)p1["next"]!}");
        Assert.Equal(400, fs); Assert.Equal("storage-cursor-invalid", Code(wrong));
    }

    [Fact]
    public void Folder_parsing_and_uris_round_trip()
    {
        Assert.Equal(new StorageListing.Folder("file", null, "/data/in/"), StorageListing.ParseFolder("/data/in"));
        Assert.Equal(new StorageListing.Folder("file", null, "/data/a b/"), StorageListing.ParseFolder("file:///data/a%20b/"));
        Assert.Equal(new StorageListing.Folder("s3", "subs", ""), StorageListing.ParseFolder("s3://subs"));
        Assert.Equal(new StorageListing.Folder("s3", "subs", "in/x/"), StorageListing.ParseFolder("s3://subs/in/x"));
        Assert.Equal("file:///data/a%20b%23c/", StorageListing.FileUri("/data/a b#c/"));
        Assert.Equal("/data/a b#c.srt", StorageLocation.Parse(StorageListing.FileUri("/data/a b#c.srt")).Value);
        Assert.Equal(("subs", "in/a b?.srt"), new S3Storage(new() { S3AllowedBuckets = ["subs"] }).Checked(StorageLocation.Parse(StorageListing.S3Uri("subs", "in/a b?.srt"))));
        Assert.Equal(("/data/", "x"), StorageListing.Split(StorageListing.ParseFolder("/data"), "x"));
        Assert.Equal(("/data/a/b/", "c"), StorageListing.Split(StorageListing.ParseFolder("/data"), "a/b/c"));
    }

    [Fact]
    public async Task File_read_carries_an_etag_and_if_none_match_gives_304_until_the_file_changes()
    {
        var file = Path.Combine(root, "cache.srt");
        File.WriteAllText(file, Samples.Srt);
        using var f = Api(); var c = f.CreateClient();
        var url = $"/v1/storage/file?location={Q(file)}";
        var first = await c.GetAsync(url);
        Assert.Equal(200, (int)first.StatusCode);
        var tag = first.Headers.ETag!.Tag;
        Assert.StartsWith("\"f-", tag);
        var again = new HttpRequestMessage(HttpMethod.Get, url); again.Headers.TryAddWithoutValidation("If-None-Match", tag);
        var notModified = await c.SendAsync(again);
        Assert.Equal(304, (int)notModified.StatusCode);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());
        Assert.Equal(tag, notModified.Headers.ETag!.Tag);
        File.AppendAllText(file, "\n3\n00:00:09,000 --> 00:00:10,000\nMore\n"); // size and mtime change
        var changed = new HttpRequestMessage(HttpMethod.Get, url); changed.Headers.TryAddWithoutValidation("If-None-Match", tag);
        var fresh = await c.SendAsync(changed);
        Assert.Equal(200, (int)fresh.StatusCode);
        Assert.NotEqual(tag, fresh.Headers.ETag!.Tag);
        Assert.Contains("More", await fresh.Content.ReadAsStringAsync());
        // Same-size rewrite: a new inode (rename over) still changes the tag.
        var tag2 = fresh.Headers.ETag!.Tag;
        var tmp = Path.Combine(root, "cache.srt.tmp"); File.WriteAllText(tmp, File.ReadAllText(file).Replace("More", "Mora")); File.Move(tmp, file, true);
        var swapped = new HttpRequestMessage(HttpMethod.Get, url); swapped.Headers.TryAddWithoutValidation("If-None-Match", tag2);
        Assert.Equal(200, (int)(await c.SendAsync(swapped)).StatusCode);
        // The change check still applies every rule: a hidden or non-subtitle location is refused, never 304.
        var hidden = new HttpRequestMessage(HttpMethod.Get, $"/v1/storage/file?location={Q(Path.Combine(root, ".x.srt"))}"); hidden.Headers.TryAddWithoutValidation("If-None-Match", tag);
        Assert.Equal(422, (int)(await c.SendAsync(hidden)).StatusCode);
    }
    [Fact]
    public async Task Browsing_is_on_by_default_within_the_allowlists()
    {
        File.WriteAllText(Path.Combine(root, "a.srt"), Samples.Srt);
        using var f = ApiFactory.With(new() { ["STORAGE_FILE_ROOTS"] = root }); var c = f.CreateClient();
        var (status, j) = await Get(c, $"/v1/storage/list?location={Q(root + "/")}");
        Assert.Equal(200, status); Assert.Contains("a.srt", j.ToJsonString());
        Assert.Equal(422, (await Get(c, $"/v1/storage/list?location={Q(outside + "/")}")).Status);
        Assert.True((bool)(await c.GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>("/v1/limits"))!["storage"]!["list"]!);
    }
}
