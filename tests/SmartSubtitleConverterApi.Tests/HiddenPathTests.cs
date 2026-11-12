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

/// Hidden-path policy (QA p9 issue 1) and decoded control characters (p9 issue 2), through the real endpoints:
/// dotfiles, anything inside a dot-named folder, hidden allowed roots, encoded components and symlinks to hidden targets are never
/// read, listed or used as conversion input, by any route; ordinary dotted names still work; decoded NULs are a 422, never a 500.
public sealed class HiddenPathTests : IDisposable
{
    private const string Sentinel = "HIDDEN_SENTINEL_P9";
    private static readonly string Hidden = "1\n00:00:01,000 --> 00:00:02,000\n" + Sentinel + "\n";
    private readonly string root = Path.Combine(Path.GetTempPath(), "se-hidden-" + Guid.NewGuid());
    private readonly string hiddenRoot = Path.Combine(Path.GetTempPath(), ".se-hiddenroot-" + Guid.NewGuid());
    public HiddenPathTests()
    {
        Directory.CreateDirectory(root); Directory.CreateDirectory(hiddenRoot);
        File.WriteAllText(Path.Combine(root, "plain.srt"), Samples.Srt);
        File.WriteAllText(Path.Combine(root, "show.en.v2.srt"), Samples.Srt);
        Directory.CreateDirectory(Path.Combine(root, "v1.2")); File.WriteAllText(Path.Combine(root, "v1.2", "ep.srt"), Samples.Srt);
        File.WriteAllText(Path.Combine(root, ".env"), Hidden);
        File.WriteAllText(Path.Combine(root, ".hidden.srt"), Hidden);
        Directory.CreateDirectory(Path.Combine(root, ".hiddendir", "deeper"));
        File.WriteAllText(Path.Combine(root, ".hiddendir", "in-hidden.srt"), Hidden);
        File.WriteAllText(Path.Combine(root, ".hiddendir", "deeper", "x.srt"), Hidden);
        Directory.CreateDirectory(Path.Combine(root, "sub", ".git")); File.WriteAllText(Path.Combine(root, "sub", ".git", "config.srt"), Hidden);
        Directory.CreateDirectory(Path.Combine(root, ".secret-root")); File.WriteAllText(Path.Combine(root, ".secret-root", "r.srt"), Hidden);
        File.CreateSymbolicLink(Path.Combine(root, "alias.srt"), Path.Combine(root, ".hidden.srt"));
        File.WriteAllText(Path.Combine(hiddenRoot, "in-hidden-root.srt"), Hidden);
    }
    public void Dispose() { Directory.Delete(root, true); Directory.Delete(hiddenRoot, true); }

    private ApiFactory Api(bool list = true, Dictionary<string, string?>? extra = null, Action<IServiceCollection>? configure = null)
    {
        // A nested hidden root and a hidden standalone root: neither may become a way in.
        var s = new Dictionary<string, string?> { ["STORAGE_FILE_ROOTS"] = string.Join(Path.PathSeparator, root, Path.Combine(root, ".secret-root"), hiddenRoot), ["STORAGE_LIST_ENABLED"] = list ? "true" : "false" };
        foreach (var (k, v) in extra ?? []) s[k] = v;
        return ApiFactory.With(s, configure);
    }
    private static string Q(string v) => Uri.EscapeDataString(v);
    private static string FileUri(string path) => new Uri(path).AbsoluteUri;
    private static async Task<(int Status, string Body)> Send(Task<HttpResponseMessage> request) { var r = await request; return ((int)r.StatusCode, await r.Content.ReadAsStringAsync()); }
    private static void Rejected((int Status, string Body) r, string what, string code = "hidden-path", int status = 422)
    {
        Assert.True(r.Status == status && r.Body.Contains($"\"{code}\""), $"{what}: {r.Status} {r.Body}");
        Assert.DoesNotContain(Sentinel, r.Body);
        Assert.DoesNotContain("Exception", r.Body); Assert.DoesNotContain("Null character", r.Body);
    }

    /// Every hidden variant: dotfiles, inside a hidden folder (and deeper), a hidden folder below a visible one, the nested and the standalone
    /// hidden root, percent-encoded dots, and a missing hidden name (same answer: no existence oracle).
    private IEnumerable<string> HiddenFiles() =>
    [
        Path.Combine(root, ".env"), Path.Combine(root, ".hidden.srt"), Path.Combine(root, ".hiddendir", "in-hidden.srt"),
        Path.Combine(root, ".hiddendir", "deeper", "x.srt"), Path.Combine(root, "sub", ".git", "config.srt"),
        Path.Combine(root, ".secret-root", "r.srt"), Path.Combine(hiddenRoot, "in-hidden-root.srt"), Path.Combine(root, ".missing.srt"),
        FileUri(Path.Combine(root, ".env")), FileUri(root) + "/%2Ehidden.srt", FileUri(root) + "/%2ehiddendir/in-hidden.srt", FileUri(root) + "/sub/%2Egit/config.srt",
    ];

    [Fact]
    public async Task Storage_file_reads_refuse_every_hidden_variant_and_still_read_ordinary_dotted_names()
    {
        using var f = Api(); var c = f.CreateClient();
        foreach (var location in HiddenFiles()) Rejected(await Send(c.GetAsync($"/v1/storage/file?location={Q(location)}")), location);
        // In-root symlink to a hidden target: the API never follows symlinks.
        Rejected(await Send(c.GetAsync($"/v1/storage/file?location={Q(Path.Combine(root, "alias.srt"))}")), "alias", "storage-location-rejected");
        foreach (var ok in new[] { Path.Combine(root, "plain.srt"), Path.Combine(root, "show.en.v2.srt"), Path.Combine(root, "v1.2", "ep.srt"), FileUri(Path.Combine(root, "v1.2", "ep.srt")) })
        {
            var (s, body) = await Send(c.GetAsync($"/v1/storage/file?location={Q(ok)}"));
            Assert.True(s == 200 && body == Samples.Srt, $"{ok}: {s}");
        }
    }

    [Fact]
    public async Task Listings_skip_hidden_entries_and_refuse_hidden_folders_directly_by_prefix_or_encoded()
    {
        using var f = Api(); var c = f.CreateClient();
        var (s, body) = await Send(c.GetAsync($"/v1/storage/list?location={Q(root)}"));
        Assert.Equal(200, s);
        var names = System.Text.Json.Nodes.JsonNode.Parse(body)!["items"]!.AsArray().Select(i => (string)i!["name"]!).ToList();
        Assert.Equal(new[] { "plain.srt", "show.en.v2.srt", "sub/", "v1.2/" }, names);
        var (_, sub) = await Send(c.GetAsync($"/v1/storage/list?location={Q(Path.Combine(root, "sub"))}"));
        Assert.Empty(System.Text.Json.Nodes.JsonNode.Parse(sub)!["items"]!.AsArray());
        foreach (var (location, prefix) in new (string, string?)[]
        {
            (Path.Combine(root, ".hiddendir"), null), (Path.Combine(root, ".hiddendir", "deeper") + "/", null), (FileUri(root) + "/%2Ehiddendir/", null),
            (Path.Combine(root, "sub", ".git"), null), (root, ".hiddendir/"), (root, ".hiddendir/deeper/"), (root, "sub/.git/"),
            (Path.Combine(root, ".secret-root"), null), (hiddenRoot, null), (Path.Combine(root, ".nope"), null),
        })
            Rejected(await Send(c.GetAsync($"/v1/storage/list?location={Q(location)}" + (prefix != null ? $"&prefix={Q(prefix)}" : ""))), $"{location} {prefix}");
        // A name prefix starting with '.' is refused up front (it could only ever match hidden names).
        Rejected(await Send(c.GetAsync($"/v1/storage/list?location={Q(root)}&prefix=.h")), "prefix .h");
        Rejected(await Send(c.GetAsync($"/v1/storage/list?location={Q(root)}&prefix={Q("sub/.g")}")), "prefix sub/.g");
    }

    [Fact]
    public async Task Locations_leave_out_hidden_roots_and_prefixes()
    {
        using var f = Api(extra: new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_ALLOWED_PREFIXES"] = "tmp/,tmp/.cache/,.trash/,tmp/.c" }); var c = f.CreateClient();
        var (s, body) = await Send(c.GetAsync("/v1/storage/locations"));
        Assert.Equal(200, s);
        var uris = System.Text.Json.Nodes.JsonNode.Parse(body)!["locations"]!.AsArray().Select(l => (string)l!["uri"]!).ToList();
        Assert.Equal(new[] { FileUri(root) + "/", "s3://subs/tmp/" }, uris);
        Assert.DoesNotContain(".secret-root", body); Assert.DoesNotContain(".se-hiddenroot", body); Assert.DoesNotContain(".cache", body); Assert.DoesNotContain(".trash", body);
    }

    [Fact]
    public async Task Conversion_detect_and_job_file_routes_cannot_bypass_the_policy_even_with_listing_off()
    {
        using var f = Api(list: false); var c = f.CreateClient();
        foreach (var location in HiddenFiles().Append(Path.Combine(root, "alias.srt")))
        {
            var code = location.EndsWith("alias.srt") ? "storage-location-rejected" : "hidden-path";
            Rejected(await Send(c.PostAsJsonAsync("/v1/convert/json", new { input = location, target = "vtt" })), "convert/json " + location, code);
            Rejected(await Send(c.PostAsJsonAsync("/v1/convert/json", new { input = location, target = "vtt", output = Path.Combine(root, "out-" + Guid.NewGuid().ToString("N") + ".vtt") })), "convert/json+output " + location, code);
            using var form = new MultipartFormDataContent { { new StringContent(location), "input" }, { new StringContent("vtt"), "target" } };
            Rejected(await Send(c.PostAsync("/v1/convert", form)), "convert multipart " + location, code);
            Rejected(await Send(c.PostAsJsonAsync("/v1/detect", new { input = location })), "detect " + location, code);
        }
        // Control: an ordinary input still converts, and its job files read back.
        var ok = await c.PostAsJsonAsync("/v1/convert/json", new { input = Path.Combine(root, "show.en.v2.srt"), target = "vtt", output = Path.Combine(root, "show.en.v2.vtt") });
        Assert.Equal(200, (int)ok.StatusCode);
        Assert.Equal(Samples.Srt, await (await c.GetAsync($"/v1/jobs/{ok.Header("X-Job-Id")}/input")).Content.ReadAsStringAsync());
        Assert.Equal(200, (int)(await c.GetAsync($"/v1/jobs/{ok.Header("X-Job-Id")}/output")).StatusCode);
        // Writes follow the same rule (H2): no dot-named output, nothing inside a dot folder, no replacing .git/config.
        foreach (var output in new[] { Path.Combine(root, ".written.vtt"), Path.Combine(root, ".hiddendir", "x.vtt"), Path.Combine(root, "sub", ".git", "config.vtt"), FileUri(root) + "/%2Eenc.vtt", Path.Combine(hiddenRoot, "o.vtt") })
        {
            Rejected(await Send(c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt", output })), "hidden output " + output);
            Rejected(await Send(c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt", output, ifExists = "replace", sidecar = true })), "hidden replace " + output);
        }
        Assert.False(File.Exists(Path.Combine(root, ".written.vtt"))); Assert.False(File.Exists(Path.Combine(root, ".hiddendir", "x.vtt")));
    }

    [Fact]
    public async Task Decoded_control_characters_are_a_stable_422_on_every_route_never_a_500()
    {
        using var f = Api(extra: new() { ["S3_ALLOWED_BUCKETS"] = "subs" }); var c = f.CreateClient();
        var plain = FileUri(Path.Combine(root, "plain.srt"));
        var locations = new[] { plain + "%00.txt", plain + "%00", FileUri(root) + "/%00/plain.srt", plain + "%0A.srt", plain + "%1F", "s3://subs/in/a.srt%00.txt", "s3://subs/in%00/a.srt" };
        foreach (var location in locations)
        {
            Rejected(await Send(c.GetAsync($"/v1/storage/file?location={Q(location)}")), "file " + location, "storage-location-rejected");
            Rejected(await Send(c.PostAsJsonAsync("/v1/convert/json", new { input = location, target = "vtt" })), "convert " + location, "storage-location-rejected");
            Rejected(await Send(c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, target = "vtt", output = location.Replace("plain.srt", "out.vtt") })), "convert output " + location, "storage-location-rejected");
            using var form = new MultipartFormDataContent { { new StringContent(location), "input" }, { new StringContent("vtt"), "target" } };
            Rejected(await Send(c.PostAsync("/v1/convert", form)), "multipart " + location, "storage-location-rejected");
            Rejected(await Send(c.PostAsJsonAsync("/v1/detect", new { input = location })), "detect " + location, "storage-location-rejected");
        }
        // Raw NULs (JSON escapes, query bytes) and NUL in folders and prefixes.
        Rejected(await Send(c.PostAsJsonAsync("/v1/convert/json", new { input = Path.Combine(root, "plain.srt") + "\0.txt", target = "vtt" })), "raw NUL input", "storage-location-rejected");
        Rejected(await Send(c.GetAsync($"/v1/storage/file?location={Q(Path.Combine(root, "plain.srt") + "\0.txt")}")), "raw NUL file", "storage-location-rejected");
        foreach (var folder in new[] { FileUri(root) + "/%00/", root + "/\0/", "s3://subs/in%00/" })
            Rejected(await Send(c.GetAsync($"/v1/storage/list?location={Q(folder)}")), "list " + folder, "storage-location-rejected");
        Rejected(await Send(c.GetAsync($"/v1/storage/list?location={Q(root)}&prefix={Q("a\0b")}")), "prefix NUL", "invalid-request", 400);
        Rejected(await Send(c.GetAsync($"/v1/storage/list?location={Q(root)}&prefix=a%00b")), "prefix %00", "invalid-request", 400);
    }

    /// S3 keys: the same policy per '/' segment; reads and listings refused before any S3 call; dotted object names fine.
    private sealed class Bucket : AmazonS3Client
    {
        public Bucket() : base(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://unused.invalid", AuthenticationRegion = "us-east-1" }) { }
        public readonly List<string> Calls = [];
        public readonly Dictionary<string, string> Objects = new() { ["in/show.v2.srt"] = Samples.Srt, ["in/.env"] = Hidden, ["in/.cache/x.srt"] = Hidden, ["in/sub/.git/c.srt"] = Hidden };
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("get " + request.Key);
            var bytes = Encoding.UTF8.GetBytes(Objects[request.Key]);
            return Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream(bytes), ContentLength = bytes.Length });
        }
        public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken = default)
        {
            Calls.Add("list " + request.Prefix);
            var response = new ListObjectsV2Response { S3Objects = [], CommonPrefixes = [], IsTruncated = false };
            foreach (var key in Objects.Keys.Where(k => k.StartsWith(request.Prefix, StringComparison.Ordinal)).Order(StorageListing.Utf8Order))
            {
                var rest = key[request.Prefix.Length..]; var slash = rest.IndexOf('/');
                if (slash < 0) response.S3Objects.Add(new S3Object { Key = key, Size = 1 });
                else if (!response.CommonPrefixes.Contains(key[..(request.Prefix.Length + slash + 1)])) response.CommonPrefixes.Add(key[..(request.Prefix.Length + slash + 1)]);
            }
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task S3_hidden_keys_are_skipped_in_listings_and_refused_for_reads_and_conversions_before_any_call()
    {
        var bucket = new Bucket();
        var opts = new ApiOptions { S3AllowedBuckets = ["subs"], S3AllowedPrefixes = ["in/"], StorageListEnabled = true };
        using var f = Api(extra: new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_ALLOWED_PREFIXES"] = "in/" }, configure: s => { s.RemoveAll<S3Storage>(); s.AddSingleton(new S3Storage(opts, bucket)); });
        var c = f.CreateClient();
        var (s, body) = await Send(c.GetAsync($"/v1/storage/list?location={Q("s3://subs/in/")}"));
        Assert.Equal(200, s);
        Assert.Equal(new[] { "show.v2.srt", "sub/" }, System.Text.Json.Nodes.JsonNode.Parse(body)!["items"]!.AsArray().Select(i => (string)i!["name"]!));
        var (_, sub) = await Send(c.GetAsync($"/v1/storage/list?location={Q("s3://subs/in/sub/")}"));
        Assert.Empty(System.Text.Json.Nodes.JsonNode.Parse(sub)!["items"]!.AsArray());
        bucket.Calls.Clear();
        foreach (var key in new[] { "s3://subs/in/.env", "s3://subs/in/.cache/x.srt", "s3://subs/in/%2Ecache/x.srt", "s3://subs/in/sub/.git/c.srt", "s3://subs/in/.missing" })
        {
            Rejected(await Send(c.GetAsync($"/v1/storage/file?location={Q(key)}")), key);
            Rejected(await Send(c.PostAsJsonAsync("/v1/convert/json", new { input = key, target = "vtt" })), "convert " + key);
        }
        foreach (var (location, prefix) in new (string, string?)[] { ("s3://subs/in/.cache/", null), ("s3://subs/in/%2Ecache/", null), ("s3://subs/in/", ".cache/"), ("s3://subs/in/sub/.git/", null) })
            Rejected(await Send(c.GetAsync($"/v1/storage/list?location={Q(location)}" + (prefix != null ? $"&prefix={Q(prefix)}" : ""))), $"{location} {prefix}");
        Assert.Empty(bucket.Calls);
        var (ok, text) = await Send(c.GetAsync($"/v1/storage/file?location={Q("s3://subs/in/show.v2.srt")}"));
        Assert.Equal(200, ok); Assert.Equal(Samples.Srt, text);
    }

    [Fact]
    public void Policy_is_per_component_and_dots_inside_names_are_fine()
    {
        Assert.True(HiddenPaths.IsHidden("/data/.env")); Assert.True(HiddenPaths.IsHidden("/data/.git/x")); Assert.True(HiddenPaths.IsHidden("in/.cache/"));
        Assert.True(HiddenPaths.IsHidden("/home/u/.subs/a.srt")); Assert.True(HiddenPaths.IsHidden(".trash/"));
        Assert.False(HiddenPaths.IsHidden("/data/show.en.srt")); Assert.False(HiddenPaths.IsHidden("/data/v1.2/a..b.srt")); Assert.False(HiddenPaths.IsHidden("in/a.b/c."));
        Assert.False(HiddenPaths.IsHidden("/data/sub/")); Assert.False(HiddenPaths.IsHidden(""));
    }
}
