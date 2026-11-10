using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using SmartSubtitleConverterApi.Binary;
using SmartSubtitleConverterApi.Conversion;
using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Tests;

/// <summary>Binary broadcast formats (milestone plan): our layer only; libse's fidelity is trusted upstream.</summary>
public class BinaryFormatTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _c;
    public BinaryFormatTests(ApiFactory f) => _c = f.CreateClient();

    /// <summary>
    /// Latin text every Tier 1 table can hold (accents, two-line cues). The second line has common English words so
    /// PAC's code-page probe can recognise the language (a file without any dictionary word needs inputCodePage).
    /// </summary>
    internal static readonly string Srt = string.Join("\r\n", Enumerable.Range(0, 8).Select(i =>
        $"{i + 1}\r\n00:00:{1 + i * 3:00},000 --> 00:00:{3 + i * 3:00},000\r\n" + (i % 2 == 0 ? $"Café número {i}" : $"Line {i}: you know what we are\r\nsecond à la carte") + "\r\n"));

    public static TheoryData<string, string, bool> Writable => new()
    {
        { "ebustl", ".stl", false }, { "pacscreenelectronics", ".pac", false }, { "pacunicodeunipac", ".fpc", false }, { "cavena890", ".890", false },
        { "capmakerplus", ".cap", false }, { "cheetahcaption", ".cap", true }, { "cheetahcaptionold", ".cap", true },
    };

    internal static async Task<HttpResponseMessage> ToBinary(HttpClient c, string target, string? profile = null, string srt = "")
    {
        var fields = new List<(string, string)> { ("target", target) };
        if (profile != null) fields.Add(("profile", profile));
        return await c.PostAsync("/v1/convert", Samples.Form(srt.Length > 0 ? srt : Srt, "movie.srt", [.. fields]));
    }

    [Theory]
    [MemberData(nameof(Writable))]
    public async Task Round_trip_srt_binary_srt(string id, string ext, bool lossy)
    {
        var r = await ToBinary(_c, id);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/octet-stream", r.Content.Headers.ContentType!.MediaType);
        Assert.Equal("binary", r.Header("X-Output-Kind"));
        Assert.Contains("movie" + ext, r.Header("Content-Disposition"));
        var bin = await r.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bin);
        Assert.Equal(lossy, r.Header("X-Warnings").Contains("lossy-format"));

        var back = await _c.PostAsync("/v1/convert", Samples.Form(bin, "movie" + ext, ("target", "srt"), ("profile", "{\"source\":{\"format\":\"" + id + "\"}}")));
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(id, back.Header("X-Detected-Format"));
        Assert.Equal("binary", back.Header("X-Detected-Encoding"));
        Assert.Equal("text", back.Header("X-Output-Kind"));
        var sub = new Subtitle();
        new SubRip().LoadSubtitle(sub, (await back.Content.ReadAsStringAsync()).SplitToLines(), "x.srt");
        var want = new Subtitle();
        new SubRip().LoadSubtitle(want, Srt.SplitToLines(), "x.srt");
        Assert.Equal(want.Paragraphs.Count, sub.Paragraphs.Count);
        for (var i = 0; i < want.Paragraphs.Count; i++)
        {
            Assert.InRange(Math.Abs(want.Paragraphs[i].StartTime.TotalMilliseconds - sub.Paragraphs[i].StartTime.TotalMilliseconds), 0, 42);
            if (!lossy)
            {
                Assert.InRange(Math.Abs(want.Paragraphs[i].EndTime.TotalMilliseconds - sub.Paragraphs[i].EndTime.TotalMilliseconds), 0, 42);
                Assert.Equal(want.Paragraphs[i].Text.Replace("\r\n", "\n"), HtmlUtil.RemoveHtmlTags(sub.Paragraphs[i].Text, true).Replace("\r\n", "\n"));
            }
        }
    }

    [Theory]
    [InlineData("cheetahcaption", "Æble grød")]
    [InlineData("cheetahcaptionold", "Café")]
    public async Task Lossy_targets_report_replaced_characters(string id, string text)
    {
        var srt = $"1\r\n00:00:01,000 --> 00:00:02,000\r\n{text}\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nplain\r\n";
        var j = await (await _c.PostAsJsonAsync("/v1/convert/json", new { content = srt, fileName = "a.srt", target = id })).Json();
        var codes = j["warnings"]!.AsArray().Select(w => (string)w!["code"]!).ToList();
        Assert.Contains("lossy-format", codes);
        Assert.Contains("characters-replaced", codes);
        Assert.True((int)j["warnings"]!.AsArray().First(w => (string)w!["code"]! == "characters-replaced")!["count"]! > 0);
    }

    [Theory]
    [InlineData("ebustl", "x.bin")]
    [InlineData("ebustl", null)]
    [InlineData("pacscreenelectronics", "x.pac")]
    [InlineData("pacunicodeunipac", "x.fpc")]
    [InlineData("cavena890", "x.890")]
    [InlineData("capmakerplus", "x.cap")]
    [InlineData("cheetahcaption", "x.cap")]
    public async Task Binary_input_is_auto_detected(string id, string? name)
    {
        var bin = await (await ToBinary(_c, id)).Content.ReadAsByteArrayAsync();
        var d = await _c.PostAsJsonAsync("/v1/detect", new { content = Convert.ToBase64String(bin), contentEncoding = "base64", fileName = name });
        Assert.Equal(HttpStatusCode.OK, d.StatusCode);
        var j = await d.Json();
        Assert.Equal(id, (string)j["id"]!);
        Assert.Equal("binary", (string)j["kind"]!);
        Assert.Equal("binary", (string)j["encoding"]!);
        Assert.Equal("n/a", (string)j["encodingConfidence"]!);
        Assert.Equal(8, (int)j["paragraphs"]!);
        Assert.InRange(j["candidates"]!.AsArray().Count, 1, 5);
        // and through the conversion path, with no source.format
        var r = await _c.PostAsync("/v1/convert", Samples.Form(bin, name ?? "upload", ("target", "srt")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(id, r.Header("X-Detected-Format"));
    }

    [Fact]
    public async Task Detect_reports_ebu_code_page_and_header()
    {
        var bin = await (await ToBinary(_c, "ebustl", """{"formatOptions":{"ebuStl":{"codePage":"865","frameRate":30,"language":"da"}}}""")).Content.ReadAsByteArrayAsync();
        var j = await (await _c.PostAsync("/v1/detect", Samples.Form(bin, "x.stl"))).Json();
        Assert.Equal("865", (string)j["codePage"]!);
        var fd = j["formatDetails"]!;
        Assert.Equal("latin", (string)fd["characterTable"]!);
        Assert.Equal(30, (double)fd["frameRate"]!);
        Assert.Equal("07", (string)fd["languageCode"]!);
        Assert.Equal("open", (string)fd["displayStandard"]!);
        var r = await _c.PostAsync("/v1/convert", Samples.Form(bin, "x.stl", ("target", "srt")));
        Assert.Equal("865", r.Header("X-Detected-Code-Page"));
    }

    [Fact]
    public async Task Text_formats_with_binary_extensions_stay_text()
    {
        var microDvd = "{25}{50}Hello there\r\n{75}{100}Second|line\r\n";
        var r = await _c.PostAsync("/v1/convert", Samples.Form(microDvd, "movie.sub", ("target", "srt")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("microdvd", r.Header("X-Detected-Format"));
        Assert.NotEqual("binary", r.Header("X-Detected-Encoding"));
        var srt = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "movie.stl", ("target", "vtt")));
        Assert.Equal("subrip", srt.Header("X-Detected-Format"));
    }

    // ---------------- JSON base64 rules ----------------

    [Fact]
    public async Task Json_text_content_with_binary_source_is_422()
    {
        var r = await _c.PostAsJsonAsync("/v1/convert/json", new { content = "STL garbage", target = "srt", profile = new { source = new { format = "ebustl" } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var j = await r.Json();
        Assert.Equal("invalid-binary-input", (string)j["code"]!);
        Assert.Contains("base64", (string)j["hint"]!);
    }

    [Fact]
    public async Task Json_binary_output_is_always_base64()
    {
        var r = await _c.PostAsJsonAsync("/v1/convert/json", new { content = Srt, fileName = "a.srt", target = "ebustl", responseEncoding = "text" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("binary", r.Header("X-Output-Kind"));
        var j = await r.Json();
        var o = j["outputs"]![0]!;
        Assert.Equal("base64", (string)o["contentEncoding"]!);
        Assert.Equal("binary", (string)o["kind"]!);
        Assert.Equal("cp850", (string)o["encoding"]!);
        Assert.Equal("application/octet-stream", (string)o["mediaType"]!);
        Assert.Equal("a.stl", (string)o["fileName"]!);
        var bytes = Convert.FromBase64String((string)o["content"]!);
        Assert.Equal("850STL25.01", Encoding.ASCII.GetString(bytes, 0, 11));
        Assert.Contains(j["warnings"]!.AsArray(), w => (string)w!["code"]! == "encoding-ignored");

        var back = await (await _c.PostAsJsonAsync("/v1/convert/json", new { content = (string)o["content"]!, contentEncoding = "base64", target = "srt" })).Json();
        Assert.Equal("ebustl", (string)back["detected"]!["id"]!);
        Assert.Equal("binary", (string)back["detected"]!["kind"]!);
        Assert.Equal("text", (string)back["outputs"]![0]!["contentEncoding"]!);
        Assert.Contains("Café número 0", (string)back["outputs"]![0]!["content"]!);
    }

    [Fact]
    public async Task Encoding_options_are_ignored_for_binary_with_a_warning()
    {
        var j = await (await _c.PostAsJsonAsync("/v1/convert/json", new { content = Srt, target = "pac", profile = new { encoding = new { output = "windows-1252" } } })).Json();
        Assert.Contains(j["warnings"]!.AsArray(), w => (string)w!["code"]! == "encoding-ignored");
        Assert.Equal("pac-latin", (string)j["outputs"]![0]!["encoding"]!);
    }

    // ---------------- zip ----------------

    [Fact]
    public async Task Truncated_binary_with_source_format_is_invalid_binary_input()
    {
        var stl = await (await ToBinary(_c, "ebustl")).Content.ReadAsByteArrayAsync();
        // The default (ERRORS_VERBOSE=false, H9) carries no debug block; verbose mode shows it.
        var quiet = await (await _c.PostAsync("/v1/convert", Samples.Form(stl[..600], "x.stl", ("target", "srt"), ("profile", """{"source":{"format":"ebustl"}}""")))).Json();
        Assert.Equal("invalid-binary-input", (string)quiet["code"]!);
        Assert.Null(quiet["debug"]);
        using var verbose = ApiFactory.With(new() { ["ERRORS_VERBOSE"] = "true" });
        var r = await verbose.CreateClient().PostAsync("/v1/convert", Samples.Form(stl[..600], "x.stl", ("target", "srt"), ("profile", """{"source":{"format":"ebustl"}}""")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var j = await r.Json();
        Assert.Equal("invalid-binary-input", (string)j["code"]!);
        Assert.Equal("ebustl", (string)j["debug"]!["format"]!);
        Assert.Equal(600, (int)j["debug"]!["byteLength"]!);
        Assert.DoesNotContain("bytes\":", j["debug"]!.ToJsonString().Replace("byteLength", ""));
    }

    [Theory]
    [InlineData("avidstl")]
    [InlineData("ayato")]
    [InlineData("spt")]
    public async Task Read_only_targets_are_format_not_writable(string target)
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "x.srt", ("target", target)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("format-not-writable", (string)(await r.Json())["code"]!);
    }

    [Theory]
    [InlineData("""{"formatOptions":{"ebuStl":{"frameRate":26}}}""", "ebustl", "/formatOptions/ebuStl/frameRate")]
    [InlineData("""{"target":{"fps":25},"formatOptions":{"ebuStl":{"frameRate":30}}}""", "ebustl", "/formatOptions/ebuStl/frameRate")]
    [InlineData("""{"formatOptions":{"ebuStl":{"originalProgrammeTitle":"This title is far too long for the GSI field"}}}""", "ebustl", "/formatOptions/ebuStl/originalProgrammeTitle")]
    [InlineData("""{"formatOptions":{"ebuStl":{"language":"xx"}}}""", "ebustl", "/formatOptions/ebuStl/language")]
    [InlineData("""{"formatOptions":{"cavena890":{"title":"Thirty characters is too many!"}}}""", "cavena890", "/formatOptions/cavena890/title")]
    public async Task Semantic_option_errors_are_format_option_invalid(string profile, string target, string pointer)
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "x.srt", ("target", target), ("profile", profile)));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var j = await r.Json();
        Assert.Equal("format-option-invalid", (string)j["code"]!);
        Assert.Equal(pointer, (string)j["errors"]![0]!["pointer"]!);
    }

    [Theory]
    [InlineData("""{"formatOptions":{"ebuStl":{"codePage":"1252"}}}""")]
    [InlineData("""{"formatOptions":{"pac":{"codePage":"klingon"}}}""")]
    [InlineData("""{"formatOptions":{"pac":{"codePage":13}}}""")]
    [InlineData("""{"formatOptions":{"ebuStl":{"bogus":true}}}""")]
    public async Task Schema_option_errors_stay_invalid_profile(string profile)
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "x.srt", ("target", "ebustl"), ("profile", profile)));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid-profile", (string)(await r.Json())["code"]!);
    }

    [Fact]
    public async Task Option_block_for_another_target_is_ignored_with_warning()
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(Samples.Srt, "x.srt", ("target", "vtt"), ("profile", """{"formatOptions":{"ebuStl":{"codePage":"850"}}}""")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("format-option-ignored", r.Header("X-Warnings"));
    }

    // ---------------- options reach the bytes ----------------

    [Fact]
    public async Task Ebu_options_land_in_the_gsi()
    {
        var bin = await (await ToBinary(_c, "ebustl", """
            {"formatOptions":{"ebuStl":{"codePage":"865","frameRate":30,"displayStandard":"teletext1","characterTable":"latin","language":"fr",
             "originalProgrammeTitle":"Mon film","countryOfOrigin":"BEL","timecodeStartOfProgramme":"10:00:00:00","maxRows":11}}}
            """)).Content.ReadAsByteArrayAsync();
        var gsi = Encoding.ASCII.GetString(bin, 0, 1024);
        Assert.Equal("865", gsi[..3]);
        Assert.Equal("STL30.01", gsi[3..11]);
        Assert.Equal("1", gsi[11..12]);
        Assert.Equal("00", gsi[12..14]);
        Assert.Equal("0F", gsi[14..16]);
        Assert.Equal("Mon film".PadRight(32), gsi[16..48]);
        Assert.Equal("BEL", gsi[274..277]);
        Assert.Equal("10000000", gsi[256..264]);
        Assert.Equal("11", gsi[253..255]);
    }

    [Fact]
    public async Task Ebu_defaults_and_auto_language()
    {
        var bin = await (await ToBinary(_c, "ebustl", srt: "1\r\n00:00:01,000 --> 00:00:02,000\r\nThe quick brown fox jumps over the lazy dog and this is English.\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nWhere are you going with that?\r\n")).Content.ReadAsByteArrayAsync();
        var gsi = Encoding.ASCII.GetString(bin, 0, 16);
        Assert.Equal("850STL25.010000", gsi[..15]);
        Assert.Equal("09", Encoding.ASCII.GetString(bin, 14, 2));
    }

    [Fact]
    public async Task Ebu_to_ebu_keeps_source_header_and_applies_explicit_options()
    {
        var first = await (await ToBinary(_c, "ebustl", """{"formatOptions":{"ebuStl":{"codePage":"865","originalProgrammeTitle":"Source title"}}}""")).Content.ReadAsByteArrayAsync();
        var same = await (await _c.PostAsync("/v1/convert", Samples.Form(first, "a.stl", ("target", "ebustl")))).Content.ReadAsByteArrayAsync();
        Assert.Equal("865", Encoding.ASCII.GetString(same, 0, 3));
        Assert.Equal("Source title", Encoding.ASCII.GetString(same, 16, 32).Trim());
        var over = await (await _c.PostAsync("/v1/convert", Samples.Form(first, "a.stl", ("target", "ebustl"), ("profile", """{"formatOptions":{"ebuStl":{"publisher":"ACME"}}}""")))).Content.ReadAsByteArrayAsync();
        Assert.Equal("Source title", Encoding.ASCII.GetString(over, 16, 32).Trim());
        var fresh = await (await _c.PostAsync("/v1/convert", Samples.Form(first, "a.stl", ("target", "ebustl"), ("profile", """{"formatOptions":{"ebuStl":{"headerFromSource":false}}}""")))).Content.ReadAsByteArrayAsync();
        Assert.Equal("850", Encoding.ASCII.GetString(fresh, 0, 3));
    }

    [Fact]
    public async Task Pac_code_page_is_written_and_read()
    {
        var greek = "1\r\n00:00:01,000 --> 00:00:02,000\r\nΚαλημέρα κόσμε\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nΔεύτερη γραμμή\r\n";
        var r = await _c.PostAsJsonAsync("/v1/convert/json", new { content = greek, target = "pac", profile = new { formatOptions = new { pac = new { codePage = "greek" } } } });
        var o = (await r.Json())["outputs"]![0]!;
        Assert.Equal("pac-greek", (string)o["encoding"]!);
        var back = await (await _c.PostAsJsonAsync("/v1/convert/json", new { content = (string)o["content"]!, contentEncoding = "base64", fileName = "g.pac", target = "srt",
            profile = new { formatOptions = new { pac = new { inputCodePage = 1 } } } })).Json();
        Assert.Contains("Καλημέρα κόσμε", (string)back["outputs"]![0]!["content"]!);
        Assert.Equal("pac-greek", (string)back["detected"]!["codePage"]!);
        Assert.Equal("forced", (string)back["detected"]!["formatDetails"]!["codePageConfidence"]!);
    }

    [Fact]
    public async Task Cavena_language_and_title_reach_the_bytes()
    {
        var r = await ToBinary(_c, "cavena890", """{"formatOptions":{"cavena890":{"language":"da","title":"My Title","translator":"Wauter"}}}""");
        var bin = await r.Content.ReadAsByteArrayAsync();
        var ascii = Encoding.ASCII.GetString(bin);
        Assert.Contains("My Title", ascii);
        Assert.Contains("Wauter", ascii);
        var d = await (await _c.PostAsync("/v1/detect", Samples.Form(bin, "x.890"))).Json();
        Assert.Null(d["codePage"]);
        Assert.Equal(Cavena890.LanguageIdDanish, (int)d["formatDetails"]!["languageId"]!);
    }

    [Fact]
    public async Task Target_fps_drives_frame_based_writers()
    {
        var r = await _c.PostAsJsonAsync("/v1/convert/json", new { content = "1\r\n00:00:01,010 --> 00:00:02,000\r\nHello\r\n", target = "ebustl",
            profile = new { formatOptions = new { ebuStl = new { frameRate = 25 } } } });
        var j = await r.Json();
        Assert.Contains(j["warnings"]!.AsArray(), w => (string)w!["code"]! == "frame-quantised");
    }

    // ---------------- registry ----------------

    [Fact]
    public async Task Formats_kind_and_writable_filters()
    {
        var def = (await _c.GetFromJsonAsync<JsonArray>("/v1/formats"))!;
        Assert.All(def, f => Assert.Equal("text", (string)f!["kind"]!));
        var bin = (await _c.GetFromJsonAsync<JsonArray>("/v1/formats?kind=binary"))!;
        JsonNode F(string id) => bin.First(f => (string)f!["id"]! == id)!;
        Assert.True((bool)F("ebustl")["canWrite"]!);
        Assert.Equal("ebuStl", (string)F("ebustl")["options"]!);
        Assert.Equal("application/octet-stream", (string)F("ebustl")["mediaType"]!);
        Assert.False((bool)F("ebustl")["textBased"]!);
        Assert.False((bool)F("avidstl")["canWrite"]!);
        Assert.False((bool)F("ayato")["canWrite"]!);
        Assert.False((bool)F("playcaptionsfreeeditor")["canRead"]!);
        Assert.True((bool)F("cheetahcaption")["lossy"]!);
        Assert.Contains("fpc", F("pacunicodeunipac")["aliases"]!.AsArray().Select(a => (string)a!));
        Assert.Contains("890", F("cavena890")["aliases"]!.AsArray().Select(a => (string)a!));
        var writable = (await _c.GetFromJsonAsync<JsonArray>("/v1/formats?kind=binary&writable=true"))!.Select(f => (string)f!["id"]!).Order().ToList();
        Assert.Equal(["capmakerplus", "cavena890", "cheetahcaption", "cheetahcaptionold", "ebustl", "pacscreenelectronics", "pacunicodeunipac"], writable);
        var img = (await _c.GetFromJsonAsync<JsonArray>("/v1/formats?kind=image,container"))!;
        Assert.Contains(img, f => (string)f!["id"]! == "bluraysup" && !(bool)f["canRead"]!);
        var all = (await _c.GetFromJsonAsync<JsonArray>("/v1/formats?textOnly=false"))!;
        Assert.DoesNotContain(all, f => (string)f!["kind"]! is "image" or "container");
        Assert.Contains(all, f => (string)f!["kind"]! == "binary");
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync("/v1/formats?kind=vinyl")).StatusCode);
    }

    // ---------------- robustness ----------------

    [Fact]
    public async Task Garbage_never_gives_500()
    {
        var rnd = new Random(42);
        foreach (var (id, ext, _) in Writable.Select(r => ((string)r[0], (string)r[1], (bool)r[2])))
        {
            var good = await (await ToBinary(_c, id)).Content.ReadAsByteArrayAsync();
            var truncated = good[..(good.Length / 2)];
            var flipped = (byte[])good.Clone();
            for (var i = 0; i < flipped.Length / 20; i++) flipped[rnd.Next(flipped.Length)] ^= (byte)(1 << rnd.Next(8));
            var random = new byte[good.Length];
            rnd.NextBytes(random);
            foreach (var bytes in new[] { truncated, flipped, random })
            {
                foreach (var profile in new[] { "{\"source\":{\"format\":\"" + id + "\"}}", "{}" })
                {
                    var r = await _c.PostAsync("/v1/convert", Samples.Form(bytes, "x" + ext, ("target", "srt"), ("profile", profile)));
                    Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity, $"{id} {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
                    if (r.StatusCode != HttpStatusCode.OK)
                        Assert.Contains((string)(await r.Json())["code"]!, new[] { "invalid-binary-input", "format-not-detected", "input-limits-exceeded", "ambiguous-encoding" });
                }
            }
        }
        Assert.Equal(0, BinaryIO.CountEntries());
    }

    [Fact]
    public async Task Jobs_do_not_leak_binary_settings()
    {
        await ToBinary(_c, "ebustl", """{"formatOptions":{"ebuStl":{"readFrameRate":30,"teletext":{"useBox":false,"marginBottom":5}}}}""");
        await ToBinary(_c, "cavena890", """{"formatOptions":{"cavena890":{"title":"Leak?","language":"ru"}}}""");
        await ToBinary(_c, "pac", """{"formatOptions":{"pac":{"codePage":"greek","verticalTop":3}}}""");
        var pac = (string)(await (await _c.PostAsJsonAsync("/v1/convert/json", new { content = Srt, target = "pac" })).Json())["outputs"]![0]!["encoding"]!;
        Assert.Equal(0, Ebu.OverrideReadFrameRate);
        var defaults = new Nikse.SubtitleEdit.Core.Settings.SubtitleSettings();
        var now = Configuration.Settings.SubtitleSettings;
        Assert.Equal(defaults.EbuStlTeletextUseBox, now.EbuStlTeletextUseBox);
        Assert.Equal(defaults.EbuStlMarginBottom, now.EbuStlMarginBottom);
        Assert.Equal(defaults.CurrentCavena89Title, now.CurrentCavena89Title);
        Assert.Equal(defaults.PacVerticalTop, now.PacVerticalTop);
        Assert.Equal("pac-latin", pac);
        var cav = await (await _c.PostAsync("/v1/convert", Samples.Form(Srt, "m.srt", ("target", "cavena890")))).Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("Leak?", Encoding.ASCII.GetString(cav));
    }

    [Fact]
    public async Task Temp_files_are_removed_after_each_job()
    {
        var bin = await (await ToBinary(_c, "cavena890")).Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, (await _c.PostAsync("/v1/convert", Samples.Form(bin, "x.890", ("target", "srt")))).StatusCode);
        Assert.Equal(0, BinaryIO.CountEntries());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _c.PostAsync("/v1/convert", Samples.Form(bin[..100], "x.890", ("target", "srt"), ("profile", """{"source":{"format":"cavena890"}}""")))).StatusCode);
        Assert.Equal(0, BinaryIO.CountEntries());
        Assert.True(Directory.Exists(BinaryIO.Root.FullName));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(BinaryIO.Root.FullName));
        Assert.StartsWith(Path.GetTempPath().TrimEnd('/'), BinaryIO.Root.FullName);
    }

    [Fact]
    public async Task Temp_files_are_removed_when_a_job_times_out()
    {
        using var f = ApiFactory.With(new() { ["CONVERSION_TIMEOUT_SECONDS"] = "1" });
        var q = f.Services.GetRequiredService<ConversionQueue>();
        using var stuck = new ManualResetEventSlim();
        string? path = null;
        var ex = await Assert.ThrowsAsync<ApiException>(() => q.RunAsync(() =>
        {
            path = new TempFile(BinaryIO.Current!, [1, 2, 3], ".stl").Path;
            stuck.Wait();
            return "never";
        }, default));
        Assert.Equal(504, ex.Status);
        Assert.NotNull(path);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        stuck.Set();
        for (var i = 0; i < 200 && q.IsStuck; i++) await Task.Delay(10); // the stuck conversion returns; the same worker resumes (H8)
        Assert.Equal("ok", (await q.RunAsync(() => "ok", default)).Result);
        Assert.Equal(0, BinaryIO.CountEntries());
    }

    [Fact]
    public void Startup_sweep_removes_stale_roots_and_jobs()
    {
        var stale = Directory.CreateTempSubdirectory($"{BinaryIO.RootPrefix}999999-");
        File.WriteAllBytes(Path.Combine(stale.FullName, "x.stl"), [1]);
        var job = Directory.CreateDirectory(Path.Combine(BinaryIO.Root.FullName, "job-stale"));
        BinaryIO.SweepAtStartup();
        Assert.False(stale.Exists && Directory.Exists(stale.FullName));
        Assert.False(Directory.Exists(job.FullName));
        Assert.True(Directory.Exists(BinaryIO.Root.FullName));
    }

    [Fact]
    public void Temp_names_never_use_the_callers_name()
    {
        using var job = new JobTemp();
        using var t = new TempFile(job, [0], ".stl");
        Assert.Matches("^[0-9a-f]{32}\\.stl$", Path.GetFileName(t.Path));
        Assert.True(ConversionEngine.LooksBinary([0x31, 0x00, 0x32], null));
        Assert.False(ConversionEngine.LooksBinary(Encoding.UTF8.GetBytes(Samples.Srt), null));
        Assert.False(ConversionEngine.LooksBinary([0xFF, 0xFE, 0x31, 0x00], null));
    }
}

