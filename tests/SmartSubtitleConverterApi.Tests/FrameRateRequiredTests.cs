using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace SmartSubtitleConverterApi.Tests;

/// <summary>
/// frame-rate-required (Wauter, 2026-11-12): frame-based formats that record no rate need it from the request instead of
/// libse's 23.976 default; the output inherits the source's rate; frame numbers carry over between rate-less formats.
/// </summary>
public class FrameRateRequiredTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _c;
    public FrameRateRequiredTests(ApiFactory f) => _c = f.CreateClient();

    private static readonly string Srt = BinaryFormatTests.Srt; // enough words for the PAC code-page probe; cues 1-3 s, 4-6 s, ...
    private const string SccDropFrame = "Scenarist_SCC V1.0\r\n\r\n00:00:01;15\t9420 9420 94ae 94ae 9452 9452 c845 4c4c 4f80 942f 942f\r\n\r\n00:00:03;00\t942c 942c\r\n";

    private async Task<(HttpStatusCode Status, JsonObject Json)> Post(object body)
    {
        var r = await _c.PostAsJsonAsync("/v1/convert/json", body);
        return (r.StatusCode, await r.Json());
    }

    private async Task<JsonObject> Ok(object body)
    {
        var (s, j) = await Post(body);
        Assert.True(s == HttpStatusCode.OK, $"{s}: {j.ToJsonString()}");
        return j;
    }

    private async Task<JsonObject> Required(object body, string pointer)
    {
        var (s, j) = await Post(body);
        Assert.True(s == HttpStatusCode.UnprocessableEntity, $"{s}: {j.ToJsonString()}");
        Assert.Equal("frame-rate-required", (string)j["code"]!);
        Assert.Equal(pointer, (string)j["errors"]![0]!["pointer"]!);
        Assert.Contains(pointer == "/source/fps" ? "source.fps" : "target.fps", (string)j["hint"]!);
        Assert.Contains("25 for PAL", (string)j["hint"]!);
        return j;
    }

    private async Task<byte[]> Pac(double fps = 25)
    {
        var j = await Ok(new { content = Srt, fileName = "a.srt", target = "pac", profile = new { target = new { fps } } });
        return Convert.FromBase64String((string)j["outputs"]![0]!["content"]!);
    }

    private static object Bin(byte[] b, string fileName, object target, object? profile = null) =>
        new { content = Convert.ToBase64String(b), contentEncoding = "base64", fileName, target, profile };

    private static string Text(JsonObject j, int i = 0) => (string)j["outputs"]![i]!["content"]!;
    private static JsonNode Details(JsonObject j) => j["detected"]!["formatDetails"]!;

    [Fact]
    public async Task Rate_less_source_to_a_time_based_target_needs_source_fps()
    {
        var pac = await Pac();
        var p = await Required(Bin(pac, "a.pac", "srt"), "/source/fps");
        Assert.Contains("PAC", (string)p["detail"]!);
        Assert.Contains("SubRip", (string)p["detail"]!);
        var j = await Ok(Bin(pac, "a.pac", "srt", new { source = new { fps = 25 } }));
        Assert.Contains("00:00:01,000 --> 00:00:03,000", Text(j));
        Assert.Equal("profile", (string)Details(j)["frameRateSource"]!);
        Assert.DoesNotContain("frame-rate-assumed", j["warnings"]!.ToJsonString());
    }

    [Fact]
    public async Task Pac_to_pac_passthrough_needs_no_rate_and_keeps_frame_numbers()
    {
        var pac = await Pac(25);
        var j = await Ok(Bin(pac, "a.pac", "pac"));
        Assert.Equal("unknown", (string)Details(j)["frameRateSource"]!);
        Assert.Null(Details(j)["frameRate"]);
        var copy = Convert.FromBase64String(Text(j));
        var back = await Ok(Bin(copy, "b.pac", "srt", new { source = new { fps = 25 } }));
        Assert.Contains("00:00:01,000 --> 00:00:03,000", Text(back));
        Assert.Contains("00:00:04,000 --> 00:00:06,000", Text(back));
        // another rate-less frame-based format: frame numbers carry over too
        await Ok(Bin(pac, "a.pac", "cavena890"));
    }

    [Fact]
    public async Task Passthrough_with_target_fps_or_millisecond_timing_or_a_time_based_extra_target_needs_source_fps()
    {
        var pac = await Pac();
        Assert.Contains("target.fps is set", (string)(await Required(Bin(pac, "a.pac", "pac", new { target = new { fps = 25 } }), "/source/fps"))["detail"]!);
        Assert.Contains("milliseconds", (string)(await Required(Bin(pac, "a.pac", "pac", new { timing = new { offsetMs = 1000 } }), "/source/fps"))["detail"]!);
        await Required(Bin(pac, "a.pac", "pac", new { target = new { additional = new[] { "srt" } } }), "/source/fps");
        await Required(Bin(pac, "a.pac", "scc"), "/source/fps"); // SCC readers take 29.97: not a passthrough
    }

    [Fact]
    public async Task Time_based_source_to_a_rate_less_target_needs_a_rate()
    {
        var p = await Required(new { content = Srt, fileName = "a.srt", target = "pac" }, "/target/fps");
        Assert.Contains("SubRip", (string)p["detail"]!);
        Assert.Contains("usual SCC rate", (string)(await Required(new { content = Srt, fileName = "a.srt", target = "scc" }, "/target/fps"))["hint"]!);
        await Required(new { content = Srt, fileName = "a.srt", target = "microdvd" }, "/target/fps");
        await Ok(new { content = Srt, fileName = "a.srt", target = "vtt" });
        await Ok(new { content = Srt, fileName = "a.srt", target = "ebustl" }); // its header declares the rate (default 25)
        var df = await Ok(new { content = Srt, fileName = "a.srt", target = "scenaristclosedcaptionsdropframe" }); // 29.97 by definition
        Assert.Contains("00:00:01;00", Text(df));
    }

    [Fact]
    public async Task Target_uses_source_fps_when_target_fps_is_not_set()
    {
        var j = await Ok(new { content = Srt, fileName = "a.srt", target = "microdvd", profile = new { source = new { fps = 25 } } });
        Assert.StartsWith("{25}{75}", Text(j)); // 1 s and 3 s at 25 fps
        var pac = await Ok(new { content = Srt, fileName = "a.srt", target = "pac", profile = new { source = new { fps = 25 } } });
        var back = await Ok(Bin(Convert.FromBase64String(Text(pac)), "a.pac", "srt", new { source = new { fps = 25 } }));
        Assert.Contains("00:00:04,000 --> 00:00:06,000", Text(back));
        // a different target.fps still wins (and rescales from source.fps)
        var k = await Ok(new { content = Srt, fileName = "a.srt", target = "microdvd", profile = new { source = new { fps = 25 }, target = new { fps = 25 } } });
        Assert.StartsWith("{25}{75}", Text(k));
    }

    [Fact]
    public async Task Rate_stored_in_the_source_is_used_for_the_target()
    {
        var stl = Convert.FromBase64String(Text(await Ok(new { content = Srt, fileName = "a.srt", target = "ebustl", profile = new { formatOptions = new { ebuStl = new { frameRate = 30 } } } })));
        var j = await Ok(Bin(stl, "a.stl", "microdvd"));
        Assert.Equal("header", (string)Details(j)["frameRateSource"]!);
        Assert.StartsWith("{30}{90}", Text(j));
        var pac = await Ok(Bin(stl, "a.stl", "pac"));
        var back = await Ok(Bin(Convert.FromBase64String(Text(pac)), "a.pac", "srt", new { source = new { fps = 30 } }));
        Assert.Contains("00:00:01,000 --> 00:00:03,000", Text(back));
        var scc = await Ok(new { content = SccDropFrame, fileName = "a.scc", target = "microdvd" });
        Assert.Equal("inferred", (string)Details(scc)["frameRateSource"]!);
        Assert.StartsWith("{45}", Text(scc)); // 1.5 s at 29.97
    }

    [Fact]
    public async Task Detect_reports_an_unknown_rate_without_a_number()
    {
        var pac = await Pac();
        var r = await _c.PostAsJsonAsync("/v1/detect", new { content = Convert.ToBase64String(pac), contentEncoding = "base64", fileName = "a.pac" });
        var d = (await r.Json())["formatDetails"]!;
        Assert.Equal("unknown", (string)d["frameRateSource"]!);
        Assert.Null(d["frameRate"]);
    }

    [Fact]
    public async Task Formats_list_marks_frame_based_formats()
    {
        var r = await _c.GetAsync("/v1/formats");
        var a = JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsArray();
        var fb = a.ToDictionary(f => (string)f!["id"]!, f => (bool)f!["frameBased"]!);
        foreach (var id in new[] { "pacscreenelectronics", "pacunicodeunipac", "ebustl", "cavena890", "microdvd", "scenaristclosedcaptions" })
            Assert.True(fb[id], id);
        foreach (var id in new[] { "subrip", "webvtt", "cmaftext" })
            Assert.False(fb[id], id);
    }
}
