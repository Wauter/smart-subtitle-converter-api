using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SubtitleEditConverterApi.Conversion;
using SubtitleEditConverterApi.Endpoints;
using SubtitleEditConverterApi.Infrastructure;

namespace SubtitleEditConverterApi.Tests;

/// <summary>API fixes A1–A11 from the MCP spec answers.</summary>
public class ApiFixesTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _c = f.CreateClient();
    private static readonly byte[] Srt = Encoding.UTF8.GetBytes(Samples.Srt);

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SubtitleEditConverterApi.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    private async Task<JsonNode> Spec() => JsonNode.Parse(await _c.GetStringAsync("/openapi/v1.json"))!;

    [Fact] // A3
    public async Task Every_error_code_is_in_errors_md_and_the_openapi_enum()
    {
        var md = File.ReadAllText(RepoFile("docs/errors.md"));
        var spec = await Spec();
        var enumValues = spec["components"]!["schemas"]!["ApiProblem"]!["properties"]!["code"]!["enum"]!.AsArray().Select(v => (string)v!).ToHashSet();
        Assert.NotEmpty(OpenApiContract.AllCodes);
        foreach (var code in OpenApiContract.AllCodes)
        {
            Assert.Contains($"<a id=\"{code}\"></a>`{code}`", md);
            Assert.Contains(code, enumValues);
        }
        Assert.Equal(OpenApiContract.AllCodes.Length, enumValues.Count);
    }

    [Fact] // A1, A2, A4
    public async Task Spec_declares_problems_security_headers_and_optional_fields()
    {
        var spec = await Spec();
        var schemas = spec["components"]!["schemas"]!;
        Assert.Null(schemas["ProblemDetails"]);
        Assert.Contains("ApiProblem", schemas["ManifestEntry"]!["properties"]!["error"]!.ToJsonString());
        Assert.Equal(["content"], schemas["ConvertJsonRequest"]!["required"]!.AsArray().Select(v => (string)v!));
        Assert.Equal("bearer", (string)spec["components"]!["securitySchemes"]!["bearer"]!["scheme"]!);
        foreach (var (path, item) in spec["paths"]!.AsObject())
        foreach (var (method, op) in item!.AsObject())
        {
            var responses = op!["responses"]!.AsObject();
            foreach (var (status, resp) in responses)
            {
                if (int.Parse(status) >= 400)
                    Assert.Equal("#/components/schemas/ApiProblem", (string)resp!["content"]!["application/problem+json"]!["schema"]!["$ref"]!);
            }
            Assert.Contains(op["parameters"]!.AsArray(), p => (string)p!["name"]! == "X-Request-Id" && (string)p["in"]! == "header");
            if (!path.StartsWith("/v1")) continue;
            Assert.True(responses.ContainsKey("401"), $"{method} {path} lacks 401");
            Assert.True(responses.ContainsKey("429"), $"{method} {path} lacks 429");
            Assert.Equal(2, op["security"]!.AsArray().Count);
        }
        var convert = spec["paths"]!["/v1/convert"]!["post"]!["responses"]!;
        Assert.NotNull(convert["503"]!["headers"]!["Retry-After"]);
        Assert.NotNull(convert["504"]);
        Assert.NotNull(convert["200"]!["headers"]!["X-Detected-Code-Page"]);
        Assert.NotNull(spec["paths"]!["/v1/formats"]!["get"]!["responses"]!["400"]);
    }

    [Fact] // A5
    public async Task Binding_failures_are_problem_json()
    {
        var r = await _c.PostAsync("/v1/convert/json", new StringContent("""{"content":"x","target":"srt","failFast":"yes"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var p = await r.Json();
        Assert.Equal("invalid-request", (string)p["code"]!);
        Assert.Equal("/failFast", (string)p["errors"]![0]!["pointer"]!);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);

        r = await _c.PostAsync("/v1/convert/json", new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal("invalid-request", (string)(await r.Json())["code"]!);

        r = await _c.PostAsync("/v1/convert/json", new StringContent("""{"content":"x"}""", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.StatusCode);
        Assert.Equal("unsupported-media-type", (string)(await r.Json())["code"]!);

        r = await _c.GetAsync("/v1/formats?textOnly=maybe");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        p = await r.Json();
        Assert.Equal("invalid-request", (string)p["code"]!);
        Assert.Equal("/query/textOnly", (string)p["errors"]![0]!["pointer"]!);
    }

    [Fact] // A7
    public async Task Dot_dot_is_rejected_only_as_a_path_segment()
    {
        var ok = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Zip(("a..b.srt", Srt)), "in.zip", ("target", "webvtt")));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using (var z = new ZipArchive(await ok.Content.ReadAsStreamAsync()))
            Assert.Contains(z.Entries, e => e.FullName == "a..b.vtt");
        var bad = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Zip(("x/../../evil.srt", Srt)), "in.zip", ("target", "webvtt")));
        Assert.Equal("unsafe-zip", (string)(await bad.Json())["code"]!);
    }

    private static readonly byte[] Vtt = Encoding.UTF8.GetBytes("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHi\n");

    [Theory] // ambiguous-batch-names, replaces A8 renaming
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entries_with_the_same_folder_and_stem_are_rejected_before_conversion(bool json)
    {
        // failFast=false (default) still rejects; nothing is converted.
        var zip = Samples.Zip(("ep1.srt", Srt), ("EP1.vtt", Vtt), ("ep2.srt", Srt), ("s2/ep1.srt", Srt));
        var r = json
            ? await _c.PostAsJsonAsync("/v1/convert/json", new { content = Convert.ToBase64String(zip), contentEncoding = "base64", target = "subrip", failFast = false })
            : await _c.PostAsync("/v1/convert", Samples.Form(zip, "in.zip", ("target", "subrip"), ("failFast", "false")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);
        var p = await r.Json();
        Assert.Equal("ambiguous-batch-names", (string)p["code"]!);
        Assert.Equal(["ep1.srt", "EP1.vtt"], p["errors"]!.AsArray().Select(e => (string)e!["pointer"]!));
        Assert.Contains("'EP1.vtt'", (string)p["errors"]![0]!["message"]!);
        var groups = p["groups"]!.AsArray();
        Assert.Single(groups);
        Assert.Equal(["ep1.srt", "EP1.vtt"], groups[0]!.AsArray().Select(n => (string)n!));
        Assert.NotNull(p["requestId"]);
    }

    [Fact]
    public async Task Same_stem_in_different_folders_is_fine()
    {
        var zip = Samples.Zip(("s1/ep1.srt", Srt), ("s2/ep1.vtt", Vtt), ("s1/ep1-forced.srt", Srt));
        var r = await _c.PostAsync("/v1/convert", Samples.Form(zip, "in.zip", ("target", "subrip")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var z = new ZipArchive(await r.Content.ReadAsStreamAsync());
        var names = z.Entries.Select(e => e.FullName).Where(n => n != "manifest.json").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["s1/ep1-forced.srt", "s1/ep1.srt", "s2/ep1.srt"], names);
        using var m = new StreamReader(z.GetEntry("manifest.json")!.Open());
        var manifest = JsonNode.Parse(m.ReadToEnd())!;
        Assert.Equal(names, manifest["entries"]!.AsArray().SelectMany(e => e!["outputs"]!.AsArray().Select(o => (string)o!)).Order(StringComparer.Ordinal).ToArray());

        var j = await (await _c.PostAsJsonAsync("/v1/convert/json", new { content = Convert.ToBase64String(zip), contentEncoding = "base64", target = "subrip" })).Json();
        Assert.Equal(names, j["outputs"]!.AsArray().Select(o => (string)o!["fileName"]!).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("out.{ext}", "out.vtt")]       // template without {stem}
    [InlineData("manifest.json", "manifest.json")] // reserved name
    public async Task Colliding_output_paths_fail_the_request(string template, string output)
    {
        var zip = Samples.Zip(("a.srt", Srt), ("b.srt", Srt));
        var profile = "{\"target\":{\"format\":\"webvtt\",\"fileName\":\"" + template + "\"}}";
        var r = await _c.PostAsync("/v1/convert", Samples.Form(zip, "in.zip", ("profile", profile)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var p = await r.Json();
        Assert.Equal("ambiguous-batch-names", (string)p["code"]!);
        Assert.Equal([output], p["outputs"]!.AsArray().Select(o => (string)o!));
        Assert.Contains("a.srt", p["errors"]!.AsArray().Select(e => (string)e!["pointer"]!));
        Assert.Contains("b.srt", p["errors"]!.AsArray().Select(e => (string)e!["pointer"]!));
        Assert.Contains($"'{output}'", (string)p["errors"]![0]!["message"]!);
    }

    [Fact] // A9
    public async Task Skipped_entries_are_listed_and_errors_name_the_entry()
    {
        var zip = Samples.Zip(("inner.zip", Samples.Zip(("a.srt", Srt))), ("__MACOSX/._a.srt", [0, 5, 22]), ("a.srt", Srt), ("bad.srt", Encoding.UTF8.GetBytes("nonsense")));
        var r = await _c.PostAsync("/v1/convert", Samples.Form(zip, "in.zip", ("target", "webvtt")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("2", r.Header("X-Entries-Skipped"));
        Assert.Equal("1", r.Header("X-Entries-Failed"));
        using var z = new ZipArchive(await r.Content.ReadAsStreamAsync());
        using var m = new StreamReader(z.GetEntry("manifest.json")!.Open());
        var entries = JsonNode.Parse(m.ReadToEnd())!["entries"]!.AsArray();
        var nested = entries.Single(e => (string)e!["source"]! == "inner.zip")!;
        Assert.Equal("skipped", (string)nested["status"]!);
        Assert.Equal("nested-zip", (string)nested["reason"]!);
        Assert.Equal("macos-metadata", (string)entries.Single(e => (string)e!["source"]! == "__MACOSX/._a.srt")!["reason"]!);
        var bad = entries.Single(e => (string)e!["source"]! == "bad.srt")!;
        Assert.Equal("bad.srt", (string)bad["error"]!["entry"]!);
        Assert.Null(bad["error"]!["requestId"]);

        var j = await _c.PostAsJsonAsync("/v1/convert/json", new { content = Convert.ToBase64String(zip), contentEncoding = "base64", target = "webvtt", failFast = true });
        var p = await j.Json();
        Assert.Equal("format-not-detected", (string)p["code"]!);
        Assert.Equal("bad.srt", (string)p["entry"]!);
        Assert.Equal("/content/entries/1", (string)p["errors"]![0]!["pointer"]!); // processed entries: a.srt=0, bad.srt=1
    }

    [Fact] // A11
    public async Task Timeout_names_the_phase()
    {
        using var tf = ApiFactory.With(new() { ["CONVERSION_TIMEOUT_SECONDS"] = "1" });
        var q = tf.Services.GetRequiredService<ConversionQueue>();
        using var stuck = new ManualResetEventSlim();
        var running = q.RunAsync(() => { stuck.Wait(); return "never"; }, default);
        var queued = q.RunAsync(() => "late", default);
        var ex1 = await Assert.ThrowsAsync<ApiException>(() => running);
        Assert.Equal("running", ex1.Extensions["phase"]);
        try
        {
            await queued;
        }
        catch (ApiException ex2)
        {
            Assert.Equal("queue", ex2.Extensions["phase"]);
        }
        stuck.Set();
    }
}
