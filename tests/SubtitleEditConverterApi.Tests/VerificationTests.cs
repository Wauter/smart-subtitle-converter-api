using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Nikse.SubtitleEdit.Core.Common;
using SubtitleEditConverterApi.Conversion;

namespace SubtitleEditConverterApi.Tests;

/// <summary>1.2.0: structured read-back verification, actionable warnings, source diagnostics, PAC/FPC ambiguity.</summary>
public class VerificationTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _c;
    public VerificationTests(ApiFactory f) => _c = f.CreateClient();

    private const string Greek =
        "1\r\n00:00:01,013 --> 00:00:02,500\r\nΚαλημέρα κόσμε\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\nΠού είναι ο σταθμός;\r\nΕυχαριστώ πολύ\r\n\r\n3\r\n00:00:06,000 --> 00:00:08,000\r\nΤι κάνεις;\r\n";
    private const string ZeroLength =
        "1\r\n00:00:00,000 --> 00:00:00,000\r\nMETADATA title=x\r\n\r\n2\r\n00:00:01,000 --> 00:00:02,000\r\nHello \uFFFD there\r\n\r\n3\r\n00:00:03,000 --> 00:00:04,000\r\nBye\r\n";
    private const string Latin = "1\r\n00:00:01,013 --> 00:00:02,500\r\nCafé déjà vu\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\nà la carte\r\n";

    private async Task<JsonObject> Convert(string srt, string target, object? profile = null, string fileName = "in.srt")
    {
        var r = await _c.PostAsJsonAsync("/v1/convert/json", new { content = srt, fileName, target, profile });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Json();
    }

    private static JsonObject Warning(JsonObject j, string code) => (JsonObject)j["warnings"]!.AsArray().Single(w => (string)w!["code"]! == code)!;
    private static List<string> Codes(JsonObject j) => j["warnings"]!.AsArray().Select(w => (string)w!["code"]!).ToList();
    private static int[] Ints(JsonNode? a) => a!.AsArray().Select(n => (int)n!).ToArray();

    [Fact]
    public async Task Greek_to_pac_latin_reports_code_points_cues_and_a_remedy_but_never_text()
    {
        var j = await Convert(Greek, "pac");
        var v = j["outputs"]![0]!["verification"]!;
        Assert.Equal("lossy", (string)v["verdict"]!);
        Assert.Equal(3, (int)v["cuesWritten"]!);
        Assert.Equal(3, (int)v["cuesReadBack"]!);
        Assert.Equal(0, (int)v["timing"]!["cuesOverOneFrame"]!);
        Assert.True((double)v["timing"]!["frameMs"]! > 0);
        var rc = v["replacedChars"]!;
        Assert.Equal([1, 2, 3], Ints(rc["cues"]));
        Assert.True((int)rc["total"]! > 40);
        Assert.All(rc["codePoints"]!.AsArray(), cp => Assert.StartsWith("U+03", (string)cp!["codePoint"]!));
        Assert.True(rc["codePoints"]!.AsArray().Count <= Verifier.MaxListed);

        var w = Warning(j, "characters-replaced");
        Assert.Equal((int)rc["total"]!, (int)w["count"]!);
        Assert.Contains("Greek", (string)w["message"]!);
        Assert.Contains("formatOptions.pac.codePage to \"greek\"", (string)w["hint"]!);
        Assert.Contains("fpc", (string)w["hint"]!);
        Assert.Equal([1, 2, 3], Ints(w["details"]!["cues"]));

        // positions and code points only: no cue text in warnings or verification
        var meta = j["warnings"]!.ToJsonString() + v.ToJsonString();
        foreach (var word in new[] { "Καλημέρα", "σταθμός", "κάνεις", "\\u039A" }) Assert.DoesNotContain(word, meta);
    }

    [Fact]
    public async Task The_suggested_code_page_makes_it_quantised_only()
    {
        var j = await Convert(Greek, "pac", new { formatOptions = new { pac = new { codePage = "greek" } } });
        Assert.Equal("quantised", (string)j["outputs"]![0]!["verification"]!["verdict"]!);
        Assert.DoesNotContain("characters-replaced", Codes(j));
        var ebu = await Convert(Greek, "ebustl", new { formatOptions = new { ebuStl = new { characterTable = "greek" } } });
        Assert.Equal("quantised", (string)ebu["outputs"]![0]!["verification"]!["verdict"]!);
    }

    [Fact]
    public async Task Ebu_hint_names_the_character_table()
    {
        var w = Warning(await Convert(Greek, "ebustl"), "characters-replaced");
        Assert.Contains("formatOptions.ebuStl.characterTable to \"greek\"", (string)w["hint"]!);
    }

    [Fact]
    public async Task Multipart_has_the_verdict_header_and_warnings_header_without_hint_or_details()
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(Greek, "g.srt", ("target", "pac")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("lossy", r.Header("X-Verification-Verdict"));
        var header = r.Header("X-Warnings");
        Assert.Contains("characters-replaced", header);
        Assert.DoesNotContain("details", header);
        Assert.DoesNotContain("hint", header);
        var ok = await _c.PostAsync("/v1/convert", Samples.Form(Latin, "l.srt", ("target", "fpc")));
        Assert.Contains(ok.Header("X-Verification-Verdict"), new[] { "quantised", "lossless" });
        var text = await _c.PostAsync("/v1/convert", Samples.Form(Latin, "l.srt", ("target", "vtt")));
        Assert.False(text.Headers.Contains("X-Verification-Verdict"));
    }

    [Fact]
    public async Task Text_targets_have_no_verification()
    {
        var j = await Convert(Latin, "vtt");
        Assert.Null(j["outputs"]![0]!["verification"]);
    }

    [Fact]
    public async Task Zero_length_cue_in_ebu_gets_a_clear_warning_not_just_a_giant_number()
    {
        var j = await Convert(ZeroLength, "ebustl");
        var z = Warning(j, "zero-length-cue");
        Assert.Equal([1], Ints(z["details"]!["cues"]));
        var lossy = j["warnings"]!.AsArray().Where(w => (string)w!["code"]! == "lossy-format").Select(w => w!).ToList();
        var msg = (string)Assert.Single(lossy)["message"]!;
        Assert.Contains("zero-length cue(s) 1", msg);
        Assert.Contains("99:59:59,999", msg);
        Assert.DoesNotContain("359999999", j["warnings"]!.ToJsonString());
        Assert.Contains("ApplyDurationLimits", (string)lossy[0]["hint"]!);
        var v = j["outputs"]![0]!["verification"]!;
        Assert.Equal("lossy", (string)v["verdict"]!);
        Assert.Equal([1], Ints(v["zeroLengthCues"]!["cues"]));
        Assert.Equal(0, (int)v["timing"]!["cuesOverOneFrame"]!); // kept out of the timing figures
        Assert.True((double)v["timing"]!["maxShiftMs"]! <= (double)v["timing"]!["frameMs"]!);
        Assert.DoesNotContain("359999999", v.ToJsonString());
    }

    [Fact]
    public async Task Dropped_cues_are_matched_by_start_time()
    {
        var j = await Convert(ZeroLength, "capmakerplus");
        var v = j["outputs"]![0]!["verification"]!;
        Assert.Equal(3, (int)v["cuesWritten"]!);
        Assert.Equal(2, (int)v["cuesReadBack"]!);
        Assert.Equal([1], Ints(v["missingCues"]!["cues"]));
        Assert.Equal(0, (int)v["timing"]!["cuesOverOneFrame"]!); // the other cues still line up
        var w = j["warnings"]!.AsArray().Single(x => (string)x!["code"]! == "lossy-format" && ((string)x["message"]!).Contains("read back"))!;
        Assert.Contains("missing: cue(s) 1", (string)w["message"]!);
        Assert.Contains("zero-length", (string)w["hint"]!);
    }

    [Theory]
    [InlineData("cavena890")]
    [InlineData("cheetahcaptionold")]
    public async Task Cut_lines_are_text_truncated_not_characters_replaced(string target)
    {
        const string srt = "1\r\n00:00:01,000 --> 00:00:03,000\r\nThis is a very long line that goes well beyond what the format can hold in one row\r\n\r\n2\r\n00:00:04,000 --> 00:00:05,000\r\nshort\r\n";
        var j = await Convert(srt, target);
        var w = Warning(j, "text-truncated");
        Assert.Contains(1, Ints(w["details"]!["cues"]));
        Assert.True((int)w["count"]! > 0);
        Assert.DoesNotContain("characters-replaced", Codes(j));
        Assert.Contains(1, Ints(j["outputs"]![0]!["verification"]!["truncatedCues"]!["cues"]));
    }

    [Fact]
    public async Task Replacement_chars_in_the_source_are_reported_once_as_source_replacement_chars()
    {
        var j = await Convert(ZeroLength, "pac");
        var w = Warning(j, "source-replacement-chars");
        Assert.Equal(1, (int)w["count"]!);
        Assert.Equal([2], Ints(w["details"]!["cues"]));
        Assert.DoesNotContain("characters-replaced", Codes(j)); // PAC writes U+FFFD as '?', not an encoding loss of ours
        var text = await Convert(ZeroLength, "vtt");
        Assert.Contains("source-replacement-chars", Codes(text));
        Assert.Contains("zero-length-cue", Codes(text));
    }

    [Fact]
    public async Task Detect_reports_bom_line_endings_and_source_warnings()
    {
        var bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ZeroLength)).ToArray();
        var j = await (await _c.PostAsJsonAsync("/v1/detect", new { content = System.Convert.ToBase64String(bom), contentEncoding = "base64", fileName = "z.srt" })).Json();
        Assert.True((bool)j["bom"]!);
        Assert.Equal("crlf", (string)j["lineEndings"]!);
        var codes = j["warnings"]!.AsArray().Select(w => (string)w!["code"]!).ToList();
        Assert.Equal(["zero-length-cue", "source-replacement-chars"], codes);

        var lf = await (await _c.PostAsJsonAsync("/v1/detect", new { content = Latin.Replace("\r\n", "\n"), fileName = "l.srt" })).Json();
        Assert.False((bool)lf["bom"]!);
        Assert.Equal("lf", (string)lf["lineEndings"]!);
        Assert.Null(lf["warnings"]);

        var bin = await (await BinaryFormatTests.ToBinary(_c, "ebustl")).Content.ReadAsByteArrayAsync();
        var b = await (await _c.PostAsJsonAsync("/v1/detect", new { content = System.Convert.ToBase64String(bin), contentEncoding = "base64", fileName = "x.stl" })).Json();
        Assert.Null(b["bom"]);
        Assert.Null(b["lineEndings"]);
    }

    // ---------------- PAC / FPC ambiguity ----------------

    private async Task<byte[]> Fpc() => (await (await BinaryFormatTests.ToBinary(_c, "fpc")).Content.ReadAsByteArrayAsync());

    [Theory]
    [InlineData(null)]
    [InlineData("x.bin")]
    [InlineData("x.stl")]
    public async Task Pac_family_bytes_without_a_deciding_extension_are_rejected(string? name)
    {
        foreach (var bytes in new[] { await Fpc(), await (await BinaryFormatTests.ToBinary(_c, "pac")).Content.ReadAsByteArrayAsync() })
        {
            var r = await _c.PostAsJsonAsync("/v1/convert/json", new { content = System.Convert.ToBase64String(bytes), contentEncoding = "base64", fileName = name, target = "srt" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            var p = await r.Json();
            Assert.Equal("ambiguous-format", (string)p["code"]!);
            Assert.Equal(["pacscreenelectronics", "pacunicodeunipac"], p["candidates"]!.AsArray().Select(c => (string)c!).ToArray());
            Assert.Contains("source.format", (string)p["hint"]!);
            Assert.Equal("/source/format", (string)p["errors"]![0]!["pointer"]!);
            var d = await _c.PostAsJsonAsync("/v1/detect", new { content = System.Convert.ToBase64String(bytes), contentEncoding = "base64", fileName = name });
            Assert.Equal("ambiguous-format", (string)(await d.Json())["code"]!);
        }
    }

    [Fact]
    public async Task Extension_or_explicit_source_format_decides()
    {
        var fpc = System.Convert.ToBase64String(await Fpc());
        var byName = await (await _c.PostAsJsonAsync("/v1/convert/json", new { content = fpc, contentEncoding = "base64", fileName = "x.fpc", target = "srt" })).Json();
        Assert.Equal("pacunicodeunipac", (string)byName["detected"]!["id"]!);
        var forced = await (await _c.PostAsJsonAsync("/v1/convert/json", new { content = fpc, contentEncoding = "base64", target = "srt", profile = new { source = new { format = "fpc" } } })).Json();
        Assert.Equal("pacunicodeunipac", (string)forced["detected"]!["id"]!);
        var asPac = await (await _c.PostAsJsonAsync("/v1/convert/json", new { content = fpc, contentEncoding = "base64", target = "srt", profile = new { source = new { format = "pac" } } })).Json();
        Assert.Equal("pacscreenelectronics", (string)asPac["detected"]!["id"]!);
    }

    [Fact]
    public async Task Zip_entries_are_checked_by_entry_name()
    {
        var fpc = await Fpc();
        var zip = Samples.Zip(("a.bin", fpc), ("b.fpc", fpc));
        var j = await (await _c.PostAsJsonAsync("/v1/convert/json", new { content = System.Convert.ToBase64String(zip), contentEncoding = "base64", target = "srt" })).Json();
        var entries = j["entries"]!.AsArray();
        var a = entries.Single(e => (string)e!["source"]! == "a.bin")!;
        Assert.Equal("error", (string)a["status"]!);
        Assert.Equal("ambiguous-format", (string)a["error"]!["code"]!);
        var b = entries.Single(e => (string)e!["source"]! == "b.fpc")!;
        Assert.Equal("ok", (string)b["status"]!);
        var ff = await _c.PostAsJsonAsync("/v1/convert/json", new { content = System.Convert.ToBase64String(zip), contentEncoding = "base64", target = "srt", failFast = true });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ff.StatusCode);
        var p = await ff.Json();
        Assert.Equal("ambiguous-format", (string)p["code"]!);
        Assert.Equal("a.bin", (string)p["entry"]!);
        Assert.Equal(2, p["candidates"]!.AsArray().Count);
    }

    [Fact]
    public async Task Zip_manifest_carries_verification_per_output()
    {
        var zip = Samples.Zip(("s/g.srt", Encoding.UTF8.GetBytes(Greek)), ("s/l.srt", Encoding.UTF8.GetBytes(Latin)));
        var j = await (await _c.PostAsJsonAsync("/v1/convert/json", new { content = System.Convert.ToBase64String(zip), contentEncoding = "base64", target = "pac" })).Json();
        var g = j["entries"]!.AsArray().Single(e => (string)e!["source"]! == "s/g.srt")!;
        Assert.Equal("lossy", (string)g["verification"]!["s/g.pac"]!["verdict"]!);
        Assert.All(j["outputs"]!.AsArray(), o => Assert.NotNull(o!["verification"]));
    }

    [Fact] // libse 5.1.0's Cavena 890 reader throws on its own Greek output: we say so instead of staying silent
    public async Task Unreadable_output_is_unverified_with_a_reason()
    {
        var j = await Convert("1\r\n00:00:01,000 --> 00:00:02,000\r\nΚαλημέρα\r\n", "cavena890", new { formatOptions = new { cavena890 = new { language = "el" } } });
        var v = j["outputs"]![0]!["verification"]!;
        Assert.Equal("unverified", (string)v["verdict"]!);
        Assert.Contains("couldn't be read back", (string)v["reason"]!);
        Assert.Null(v["cuesReadBack"]);
        Assert.Contains("verification-failed", Codes(j));
    }

    // ---------------- unit ----------------

    [Fact]
    public void Align_pairs_by_start_time_and_skips_missing_and_extra_cues()
    {
        static Subtitle S(params double[] starts)
        {
            var s = new Subtitle();
            foreach (var t in starts) s.Paragraphs.Add(new Paragraph("x", t, t + 500));
            return s;
        }
        Assert.Equal([(1, 0), (2, 1)], Verifier.Align(S(0, 1000, 2000), S(1010, 2000), 40));
        Assert.Equal([(0, 1), (1, 2)], Verifier.Align(S(1000, 2000), S(10, 1000, 2000), 40));
    }

    [Theory]
    [InlineData("abcdefghij", "abcdef", true)]
    [InlineData("abcdefghij", "", true)]
    [InlineData("Line1Line2Line3", "Line1Line2", true)]
    [InlineData("Caf\u00e9", "Caf", true)]
    [InlineData("abcdefghij", "bcdefghij", false)]
    public void Truncation_is_a_prefix_cut(string want, string got, bool truncated) => Assert.Equal(truncated, Verifier.IsTruncation(want, got));

    [Fact]
    public void Line_endings_and_timecodes()
    {
        Assert.Equal("crlf", SourceChecks.LineEndings("a\r\nb\r\n"));
        Assert.Equal("mixed", SourceChecks.LineEndings("a\r\nb\n"));
        Assert.Equal("cr", SourceChecks.LineEndings("a\rb"));
        Assert.Equal("none", SourceChecks.LineEndings("ab"));
        Assert.Equal("99:59:59,999", Verifier.Tc(359999999));
        Assert.Equal("U+1F600", Verifier.U(0x1F600));
    }
}
