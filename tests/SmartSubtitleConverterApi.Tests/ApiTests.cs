using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SmartSubtitleConverterApi.Tests;

public class ApiTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _c;
    public ApiTests(ApiFactory f) => _c = f.CreateClient();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zip_is_rejected_as_single_file(bool json)
    {
        var bytes = Samples.Zip(("a.srt", Encoding.UTF8.GetBytes(Samples.Srt)));
        var r = json ? await _c.PostAsJsonAsync("/v1/convert/json", new { content = Convert.ToBase64String(bytes), contentEncoding = "base64", target = "srt" })
            : await _c.PostAsync("/v1/convert", Samples.Form(bytes, "a.zip", ("target", "srt")));
        Assert.Equal(422, (int)r.StatusCode);
        Assert.Contains("ZIP input is not supported", (string)(await r.Json())["detail"]!);
    }

    [Fact]
    public async Task Additional_targets_return_json_not_zip()
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "m.srt", ("target", "vtt"), ("profile", """{"target":{"additional":["ass","ttml"]}}""")));
        Assert.Equal(3, (await r.Json())["outputs"]!.AsArray().Count);
    }

    [Fact]
    public async Task Srt_to_vtt_returns_file_with_headers()
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "movie.srt", ("target", "vtt")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("text/vtt", r.Content.Headers.ContentType!.ToString());
        Assert.Equal("movie.vtt", r.Content.Headers.ContentDisposition!.FileName?.Trim('"'));
        Assert.Equal("subrip", r.Header("X-Detected-Format"));
        Assert.Equal("utf-8", r.Header("X-Detected-Encoding"));
        Assert.Equal("2", r.Header("X-Cue-Count"));
        Assert.Equal("2", r.Header("X-Cue-Count-In"));
        Assert.Equal("0", r.Header("X-Warnings-Count"));
        Assert.Equal("no-store", r.Header("Cache-Control"));
        Assert.Equal("nosniff", r.Header("X-Content-Type-Options"));
        Assert.NotEmpty(r.Header("X-Request-Id"));
        Assert.Equal("5.1.0", r.Header("X-Libse-Version"));
        Assert.Contains("WEBVTT", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Preset_with_overrides_and_X_Profile()
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "m.srt", ("preset", "srt-to-vtt"), ("profile", """{"timing":{"offsetMs":1000}}""")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("srt-to-vtt", r.Header("X-Profile"));
        var bytes = await r.Content.ReadAsByteArrayAsync();
        Assert.NotEqual(0xEF, bytes[0]); // utf-8-no-bom from the preset
        Assert.Contains("00:00:02.000 --> 00:00:03.500", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Incoming_request_id_is_honoured_and_invalid_one_replaced()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/v1/formats");
        req.Headers.Add("X-Request-Id", "trace-abc.123");
        Assert.Equal("trace-abc.123", (await _c.SendAsync(req)).Header("X-Request-Id"));
        var bad = new HttpRequestMessage(HttpMethod.Get, "/v1/formats");
        bad.Headers.Add("X-Request-Id", "bad id with spaces");
        Assert.Equal(26, (await _c.SendAsync(bad)).Header("X-Request-Id").Length); // ULID
    }

    [Fact]
    public async Task Encodings_utf16_input_cp1252_fallback_and_output_selection()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Samples.Srt.Replace("Second", "Sécond"))).ToArray();
        var r1 = await _c.PostAsync("/v1/convert", Samples.Form(utf16, "u.srt", ("target", "srt")));
        Assert.Equal("utf-16", r1.Header("X-Detected-Encoding"));
        var out1 = await r1.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], out1[..3]); // default output utf-8 with BOM
        Assert.Contains("Sécond", Encoding.UTF8.GetString(out1));

        var cp1252 = Encoding.GetEncoding(1252).GetBytes(Samples.Srt.Replace("Second", "Sécond"));
        var r2 = await _c.PostAsync("/v1/convert", Samples.Form(cp1252, "w.srt", ("target", "srt"), ("profile", """{"encoding":{"inputFallback":"windows-1252","output":"utf-8-no-bom"}}""")));
        Assert.Equal("windows-1252", r2.Header("X-Detected-Encoding"));
        var out2 = await r2.Content.ReadAsByteArrayAsync();
        Assert.NotEqual(0xEF, out2[0]);
        Assert.Contains("Sécond", Encoding.UTF8.GetString(out2));

        var r3 = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt.Replace("Second", "Sécond"), "x.srt", ("target", "srt"), ("profile", """{"encoding":{"output":"iso-8859-1"}}""")));
        Assert.Contains("charset", (await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "x.srt", ("target", "vtt")))).Content.Headers.ContentType!.ToString());
        Assert.Contains((byte)0xE9, await r3.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Non_ascii_file_name_uses_rfc6266()
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "film-été.srt", ("target", "vtt")));
        var cd = r.Content.Headers.ContentDisposition!;
        Assert.Equal("film-été.vtt", cd.FileNameStar);
    }

    [Theory]
    [InlineData("", "empty-input", 400)]
    [InlineData("\u0001\u0002 garbage that is not a subtitle \u0000", "format-not-detected", 422)]
    public async Task Bad_inputs_give_problem_json(string body, string code, int status)
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(body, "x.srt", ("target", "vtt")));
        Assert.Equal(status, (int)r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);
        var j = await r.Json();
        Assert.Equal(code, (string)j["code"]!);
        foreach (var k in new[] { "type", "title", "detail", "hint", "requestId" }) Assert.NotNull(j[k]);
        Assert.Equal(r.Header("X-Request-Id"), (string)j["requestId"]!);
    }

    [Theory]
    [InlineData("vtt2", 400, "invalid-profile", "webvtt")]
    [InlineData("bluraysup", 422, "format-not-supported", "image-based")]
    [InlineData("avidstl", 422, "format-not-writable", "writable=true")]
    public async Task Unknown_or_unsupported_target(string target, int status, string code, string hint)
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "x.srt", ("target", target)));
        Assert.Equal(status, (int)r.StatusCode);
        var j = await r.Json();
        Assert.Equal(code, (string)j["code"]!);
        Assert.Contains(hint, j.ToJsonString());
    }

    [Fact]
    public async Task Missing_target_unknown_preset_missing_file_wrong_media_type_unknown_route()
    {
        Assert.Equal("invalid-request", (string)(await (await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt))).Json())["code"]!);
        var nf = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "x.srt", ("preset", "nope")));
        Assert.Equal(HttpStatusCode.NotFound, nf.StatusCode);
        Assert.Equal("preset-not-found", (string)(await nf.Json())["code"]!);
        var noFile = new MultipartFormDataContent { { new StringContent("vtt"), "target" } };
        Assert.Equal("invalid-request", (string)(await (await _c.PostAsync("/v1/convert", noFile)).Json())["code"]!);
        var json = await _c.PostAsync("/v1/convert", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, json.StatusCode);
        Assert.Equal("unsupported-media-type", (string)(await json.Json())["code"]!);
        var route = await _c.GetAsync("/v1/nope");
        Assert.Equal(HttpStatusCode.NotFound, route.StatusCode);
        Assert.Equal("not-found", (string)(await route.Json())["code"]!);
    }

    [Fact]
    public async Task Bad_profile_json_is_invalid_profile()
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "x.srt", ("target", "vtt"), ("profile", "{oops")));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid-profile", (string)(await r.Json())["code"]!);
    }

    [Fact]
    public async Task Json_route_text_and_base64()
    {
        var r = await _c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, fileName = "m.srt", target = "vtt", profile = new { timing = new { offsetMs = 500 } } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Json();
        Assert.Equal("text", (string)j["outputs"]![0]!["contentEncoding"]!);
        Assert.Equal("m.vtt", (string)j["outputs"]![0]!["fileName"]!);
        Assert.Contains("00:00:01.500", (string)j["outputs"]![0]!["content"]!);
        Assert.Equal("subrip", (string)j["detected"]!["id"]!);
        Assert.Equal(500, (int)j["profile"]!["timing"]!["offsetMs"]!);

        var b = await _c.PostAsJsonAsync("/v1/convert/json", new { content = Convert.ToBase64String(Encoding.UTF8.GetBytes(Samples.Ttml)), contentEncoding = "base64", target = "srt", responseEncoding = "base64" });
        var bj = await b.Json();
        Assert.Equal("base64", (string)bj["outputs"]![0]!["contentEncoding"]!);
        Assert.Contains("One", Encoding.UTF8.GetString(Convert.FromBase64String((string)bj["outputs"]![0]!["content"]!)));

        var bad = await _c.PostAsJsonAsync("/v1/convert/json", new { content = "!!", contentEncoding = "base64", target = "srt" });
        Assert.Equal("invalid-request", (string)(await bad.Json())["code"]!);
    }

    [Fact]
    public async Task Detect_multipart_and_json_and_not_detected()
    {
        var m = await (await _c.PostAsync("/v1/detect", Samples.Form(Samples.Ttml, "x.xml"))).Json();
        Assert.Equal("timedtext10", (string)m["id"]!);
        var j = await (await _c.PostAsJsonAsync("/v1/detect", new { content = Samples.Srt })).Json();
        Assert.Equal("subrip", (string)j["id"]!);
        Assert.Equal(2, (int)j["paragraphs"]!);
        var nd = await _c.PostAsJsonAsync("/v1/detect", new { content = "<tt><p>no namespace</p></tt>" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nd.StatusCode);
        Assert.Contains("TTML namespace", (string)(await nd.Json())["hint"]!);
    }

    [Fact]
    public async Task Formats_profiles_validate_schema_endpoints()
    {
        var formats = (await _c.GetFromJsonAsync<System.Text.Json.Nodes.JsonArray>("/v1/formats"))!;
        Assert.True(formats.Count > 200);
        Assert.Contains(formats, f => !(bool)f!["textBased"]!); // binary formats are listed by default
        var textFormats = (await _c.GetFromJsonAsync<System.Text.Json.Nodes.JsonArray>("/v1/formats?textOnly=true"))!;
        Assert.True(textFormats.Count > 200);
        Assert.All(textFormats, f => Assert.True((bool)f!["textBased"]!));
        var srt = (await _c.GetFromJsonAsync<System.Text.Json.Nodes.JsonArray>("/v1/formats?q=srt"))!;
        Assert.Contains(srt, f => (string)f!["id"]! == "subrip");

        var profiles = await (await _c.GetAsync("/v1/profiles")).Json();
        Assert.Contains(profiles["profiles"]!.AsArray(), p => (string)p!["name"]! == "netflix-en-vtt");
        var one = await (await _c.GetAsync("/v1/profiles/netflix-en-vtt")).Json();
        Assert.Equal("clean-srt", (string)one["raw"]!["extends"]!);
        Assert.Null(one["resolved"]!["extends"]);
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync("/v1/profiles/nope")).StatusCode);

        var v = await (await _c.PostAsync("/v1/profiles/validate", new StringContent("""{"extends":"nope","rules":{"maxLines":0}}""", Encoding.UTF8, "application/json"))).Json();
        Assert.False((bool)v["valid"]!);
        var ok = await (await _c.PostAsync("/v1/profiles/validate", new StringContent("""{"extends":"clean-srt"}""", Encoding.UTF8, "application/json"))).Json();
        Assert.True((bool)ok["valid"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostAsync("/v1/profiles/validate", new StringContent("{oops", Encoding.UTF8, "application/json"))).StatusCode);

        var schema = await (await _c.GetAsync("/v1/schemas/profile.json")).Json();
        Assert.Equal("object", (string)schema["type"]!);
    }

    [Fact]
    public async Task Health_ready_docs()
    {
        var h = await (await _c.GetAsync("/healthz")).Json();
        Assert.Equal("5.1.0", (string)h["libse"]!);
        for (var i = 0; i < 50; i++)
        {
            var ready = await _c.GetAsync("/readyz");
            if (ready.IsSuccessStatusCode) break;
            await Task.Delay(100);
        }
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/readyz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/docs/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/openapi/v1.json")).StatusCode);
    }
}
