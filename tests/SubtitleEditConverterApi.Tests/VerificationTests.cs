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

    private const string Polish =
        "1\r\n00:00:01,000 --> 00:00:02,500\r\nZażółć gęślą jaźń\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\nŁódź jest piękna\r\n\r\n3\r\n00:00:06,000 --> 00:00:08,000\r\nDziękuję bardzo\r\n";
    private const string Chinese =
        "1\r\n00:00:01,000 --> 00:00:02,500\r\n你好世界\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\n我们明天见\r\n";

    private async Task<JsonObject> Convert(string srt, string target, object? profile = null, string fileName = "in.srt")
    {
        var r = await _c.PostAsJsonAsync("/v1/convert/json", new { content = srt, fileName, target, profile });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Json();
    }

    private static string[] Strs(JsonNode? a) => a!.AsArray().Select(n => (string)n!).ToArray();

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

    // ---------- v1.2.1: remedy facts, no guessing ----------

    private static JsonNode Remedy(JsonObject j) => Warning(j, "characters-replaced")["details"]!["remedy"]!;
    private static string Srt(string text) => $"1\r\n00:00:01,000 --> 00:00:02,000\r\n{text}\r\n";

    [Fact]
    public async Task Polish_to_pac_latin_is_lossless_so_nothing_to_remedy()
    {
        var j = await Convert(Polish, "pac");
        Assert.Equal("lossless", (string)j["outputs"]![0]!["verification"]!["verdict"]!);
        Assert.DoesNotContain("characters-replaced", Codes(j));
    }

    [Fact]
    public async Task Latin_extended_to_cavena_states_the_shared_table_and_suggests_nothing()
    {
        var w = Warning(await Convert(Polish, "cavena890"), "characters-replaced");
        var hint = (string)w["hint"]!;
        Assert.StartsWith("Lost characters are Latin extended (U+", hint);
        Assert.Contains("share one character table", hint);
        Assert.DoesNotContain("ł", w.ToJsonString());
        var r = w["details"]!["remedy"]!;
        Assert.Equal(new[] { "Latin extended" }, Strs(r["scripts"]));
        Assert.Equal("pl", (string)r["detectedLanguage"]!);
        Assert.Equal("formatOptions.cavena890.language", (string)r["option"]!);
        Assert.Equal(Binary.BinaryOptions.CavenaLanguageNames, Strs(r["allowedValues"]));
        Assert.Null(r["suggested"]);
        Assert.Null(r["candidates"]);
        Assert.Equal("fpc", (string)r["unicodeAlternative"]!);
    }

    [Fact]
    public async Task Latin_extended_to_pac_gives_tested_candidates_never_a_language_guess()
    {
        // ğ/Ğ: lost in pac-latin, kept only by the turkish code page (tested), whatever the detected language.
        var r = Remedy(await Convert(Srt("Dağ ve Ğüzel"), "pac"));
        Assert.True((bool)r["candidatesTested"]!);
        Assert.Equal(new[] { "turkish" }, Strs(r["candidates"]));
        Assert.Equal("latin", (string)r["currentValue"]!);
        // Romanian ș/ț: no PAC code page keeps them.
        var ro = Warning(await Convert(Srt("Și țară"), "pac"), "characters-replaced");
        Assert.Null(ro["details"]!["remedy"]!["candidates"]);
        Assert.Null(ro["details"]!["remedy"]!["suggested"]);
        Assert.Contains("no formatOptions.pac.codePage value keeps them (tested; current: latin)", (string)ro["hint"]!);
        // Czech "ťo": each survives alone in pac-latin; the loss is libse's combining, so no code page is offered.
        var cz = Warning(await Convert(Srt("Příliš žluťoučký kůň"), "pac"), "characters-replaced");
        Assert.Null(cz["details"]!["remedy"]!["candidates"]);
        Assert.Contains("switching the code page won't help", (string)cz["hint"]!);
    }

    [Fact]
    public async Task Cjk_lists_every_tested_code_page_when_several_fit()
    {
        var w = Warning(await Convert(Srt("這是測試"), "pac"), "characters-replaced");
        var r = w["details"]!["remedy"]!;
        Assert.Null(r["suggested"]);
        Assert.True((bool)r["candidatesTested"]!);
        Assert.Equal(new[] { "chinese-traditional", "chinese-simplified", "korean", "japanese" }, Strs(r["candidates"]));
        Assert.Contains("choose the formatOptions.pac.codePage that covers them (options: chinese-traditional, chinese-simplified, korean, japanese, tested", (string)w["hint"]!);
        Assert.DoesNotContain("這", w.ToJsonString());
        var cav = Remedy(await Convert(Chinese, "cavena890", new { formatOptions = new { cavena890 = new { language = "en" } } }));
        Assert.Equal("formatOptions.cavena890.language", (string)cav["option"]!);
        Assert.Null(cav["suggested"]); // zh-hant / zh-hans: by script, not guessed
        Assert.Equal(new[] { "zh-hant", "zh-hans" }, Strs(cav["candidates"]));
    }

    [Fact]
    public async Task Greek_maps_to_exactly_one_value_so_it_is_suggested()
    {
        var pac = Remedy(await Convert(Greek, "pac"));
        Assert.Equal("greek", (string)pac["suggested"]!);
        Assert.True((bool)pac["candidatesTested"]!);
        Assert.Equal("el", (string)pac["detectedLanguage"]!);
        var ebu = Warning(await Convert(Greek, "ebustl"), "characters-replaced");
        var r = ebu["details"]!["remedy"]!;
        Assert.Equal("formatOptions.ebuStl.characterTable", (string)r["option"]!);
        Assert.Equal("latin", (string)r["currentValue"]!);
        Assert.Equal("greek", (string)r["suggested"]!);
        Assert.False((bool)r["candidatesTested"]!);
        Assert.Contains("set formatOptions.ebuStl.characterTable to \"greek\"", (string)ebu["hint"]!);
    }

    [Fact]
    public async Task Mixed_scripts_have_no_single_value()
    {
        var w = Warning(await Convert(Srt("Café Καλημέρα"), "pac"), "characters-replaced");
        Assert.Null(w["details"]!["remedy"]!["candidates"]);
        Assert.Contains("no formatOptions.pac.codePage value keeps them", (string)w["hint"]!);
        var only = Remedy(await Convert(Srt("Καλημέρα Привет"), "pac", new { formatOptions = new { pac = new { codePage = "greek" } } }));
        Assert.Equal("greek", (string)only["currentValue"]!);
        Assert.Equal(new[] { "Cyrillic" }, Strs(only["scripts"]));
        Assert.Null(only["candidates"]); // cyrillic would lose the Greek
    }

    [Fact]
    public async Task Cavena_greek_keeps_the_libse_limitation_note_and_suggests_nothing()
    {
        var w = Warning(await Convert(Greek, "cavena890"), "characters-replaced");
        var r = w["details"]!["remedy"]!;
        Assert.Null(r["suggested"]);
        Assert.Contains("libse's Cavena 890 writer doesn't keep Greek", (string)r["note"]!);
        Assert.Contains("libse's Cavena 890 writer doesn't keep Greek", (string)w["hint"]!);
    }

    [Fact]
    public async Task Remedy_stays_out_of_the_x_warnings_header()
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(Greek, "g.srt", ("target", "pac")));
        var h = r.Header("X-Warnings");
        Assert.Contains("characters-replaced", h);
        Assert.DoesNotContain("remedy", h);
        Assert.DoesNotContain("allowedValues", h);
    }
}
