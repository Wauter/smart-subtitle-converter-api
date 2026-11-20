using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace SmartSubtitleConverterApi.Tests;

/// <summary>
/// Broadcast-tester follow-ups: PAC code-page probe, text-encoding probe, target.fps semantics, SCC drop-frame,
/// source warnings (overlap, long cue, EBU TCP), header defaults, stale sidecar delete and 0644 outputs.
/// </summary>
public class FollowUpTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _c;
    public FollowUpTests(ApiFactory f) { _c = f.CreateClient(); Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }

    private static string Srt(params (string Start, string End, string Text)[] cues) =>
        string.Join("\r\n", cues.Select((q, i) => $"{i + 1}\r\n{q.Start} --> {q.End}\r\n{q.Text}\r\n"));

    private const string Greek =
        "1\r\n00:00:01,000 --> 00:00:02,500\r\nΚαλημέρα κόσμε, τι κάνεις;\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\nΔεν είναι εδώ τώρα.\r\nΓιατί όχι;\r\n\r\n3\r\n00:00:06,000 --> 00:00:08,000\r\nΑυτό είναι για μένα και για σένα.\r\n";
    // Afrikaans: libse detects it (af), and EBU Tech 3264 Appendix 3 has no code for it.
    private const string UnmappedLanguage =
        "1\r\n00:00:01,000 --> 00:00:03,500\r\nEk weet nie wat hier aangaan nie, waar gaan jy heen met al die mense in die huis\r\n\r\n2\r\n00:00:04,000 --> 00:00:06,000\r\nEk weet nie wat hier aangaan nie, waar gaan jy heen met al die mense in die huis\r\n";
    private const string Czech =
        "1\r\n00:00:01,000 --> 00:00:02,500\r\nJá nevím, co se to tady děje.\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\nŘekni mi, že je to pravda.\r\n\r\n3\r\n00:00:06,000 --> 00:00:08,000\r\nTo není možné, čtyři řeky už teď.\r\n";
    private const string Hebrew =
        "1\r\n00:00:01,000 --> 00:00:02,500\r\nזה לא מה שאני רוצה\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\nאבל יש לי את כל הזמן\r\n\r\n3\r\n00:00:06,000 --> 00:00:08,000\r\nגם היא לא כאן\r\n";
    private const string Arabic =
        "1\r\n00:00:01,000 --> 00:00:02,500\r\nهل أنت هنا؟ لا أعرف ما هذا\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\nأنا في البيت مع كل الناس\r\n\r\n3\r\n00:00:06,000 --> 00:00:08,000\r\nلقد كان هذا من أجلك\r\n";
    private const string Thai =
        "1\r\n00:00:01,000 --> 00:00:02,500\r\nคุณเป็นอะไร ไม่ได้ครับ\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\nฉันและเรามีที่นี้\r\n\r\n3\r\n00:00:06,000 --> 00:00:08,000\r\nผมจะได้ค่ะ\r\n";

    private async Task<JsonObject> Json(object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var r = await _c.PostAsJsonAsync("/v1/convert/json", body);
        var j = await r.Json();
        Assert.True(expected == r.StatusCode, $"{r.StatusCode}: {j.ToJsonString()}");
        return j;
    }

    private async Task<byte[]> Binary(string srt, string target, object? profile = null, string fileName = "movie.srt")
    {
        var j = await Json(new { content = srt, fileName, target, profile = Samples.WithFps(profile, target) });
        return Convert.FromBase64String((string)j["outputs"]![0]!["content"]!);
    }

    private Task<JsonObject> Read(byte[] bytes, string fileName, string target = "srt", object? profile = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        Json(new { content = Convert.ToBase64String(bytes), contentEncoding = "base64", fileName, target, profile = Samples.WithFps(profile, target) }, expected);

    private static List<string> Codes(JsonObject j) => j["warnings"]!.AsArray().Select(w => (string)w!["code"]!).ToList();
    private static JsonObject Warning(JsonObject j, string code) => (JsonObject)j["warnings"]!.AsArray().Single(w => (string)w!["code"]! == code)!;
    private static string Text(JsonObject j) => (string)j["outputs"]![0]!["content"]!;
    private static JsonNode Details(JsonObject j) => j["detected"]!["formatDetails"]!;

    private static string SmokeFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "smoke"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "smoke", name);
    }

    // 1. PAC input code page probe

    [Fact]
    public async Task Pac_auto_probes_greek_from_the_smoke_sample()
    {
        var j = await Read(File.ReadAllBytes(SmokeFile("binary/sample-greek.pac")), "show.el.pac");
        Assert.Equal("pac-greek", (string)j["detected"]!["codePage"]!);
        Assert.Equal("probed", (string)Details(j)["codePageConfidence"]!);
        Assert.Equal("el", (string)Details(j)["probedLanguage"]!);
        Assert.Matches("[α-ω]", Text(j));
    }

    [Theory]
    [InlineData("greek", Greek, "el", "είναι")]
    [InlineData("hebrew", Hebrew, "he", "אבל יש לי")]
    [InlineData("arabic", Arabic, "ar", "أنا في البيت")]
    [InlineData("czech", Czech, "cs", "Řekni mi, že")]
    public async Task Pac_auto_probes_the_written_code_page(string page, string srt, string language, string word)
    {
        var pac = await Binary(srt, "pac", new { formatOptions = new { pac = new { codePage = page } } });
        var j = await Read(pac, "show.pac");
        Assert.Equal("pac-" + page, (string)j["detected"]!["codePage"]!);
        Assert.Equal("probed", (string)Details(j)["codePageConfidence"]!);
        Assert.StartsWith(language, (string)Details(j)["probedLanguage"]!);
        Assert.Contains(word, Text(j));
        // libse's own PAC check misses some of these headers; the probe still decides without a .pac name.
        var unnamed = await Read(pac, "show.bin");
        Assert.Equal("pac-" + page, (string)unnamed["detected"]!["codePage"]!);
    }

    [Fact]
    public async Task Pac_without_recognisable_words_is_422_with_candidates_and_a_hint()
    {
        var pac = await Binary(Srt(("00:00:01,000", "00:00:02,000", "Xq 12"), ("00:00:03,000", "00:00:04,000", "Zv 34")), "pac");
        var p = await Read(pac, "show.pac", expected: HttpStatusCode.UnprocessableEntity);
        Assert.Equal("ambiguous-encoding", (string)p["code"]!);
        Assert.Equal("pac-code-page", (string)p["probe"]!);
        Assert.NotNull(p["candidates"]);
        Assert.Contains("inputCodePage", p.ToJsonString());
    }

    [Fact]
    public async Task Pac_forced_input_code_page_wins()
    {
        var pac = await Binary(Greek, "pac", new { formatOptions = new { pac = new { codePage = "greek" } } });
        var j = await Read(pac, "show.pac", profile: new { formatOptions = new { pac = new { inputCodePage = "greek" } } });
        Assert.Equal("forced", (string)Details(j)["codePageConfidence"]!);
        Assert.Contains("είναι", Text(j));
        // Forcing the wrong page is the caller's call: no 422.
        var wrong = await Read(pac, "show.pac", profile: new { formatOptions = new { pac = new { inputCodePage = "latin" } } });
        Assert.Equal("forced", (string)Details(wrong)["codePageConfidence"]!);
    }

    // 5. Text encoding probe

    [Theory]
    [InlineData(1256, Arabic, "windows-1256", "أنا في البيت")]
    [InlineData(1255, Hebrew, "windows-1255", "אבל יש לי")]
    [InlineData(874, Thai, "windows-874", "คุณเป็นอะไร")]
    public async Task Distinctive_script_legacy_srt_is_probed_uniquely(int codePage, string srt, string encoding, string word)
    {
        var bytes = Encoding.GetEncoding(codePage).GetBytes(srt);
        var j = await Read(bytes, "show.srt", "vtt");
        Assert.Equal(encoding, (string)j["detected"]!["encoding"]!);
        Assert.Equal("probed", (string)j["detected"]!["encodingConfidence"]!);
        Assert.Contains(word, Text(j));
        Assert.DoesNotContain("encoding-guessed", Codes(j));
        Assert.DoesNotContain("encoding-forced", Codes(j));
    }

    [Fact]
    public async Task Western_1252_srt_still_converts_and_forced_encoding_wins()
    {
        var srt = Srt(("00:00:01,000", "00:00:02,000", "Où êtes-vous? Je ne sais pas, c'est très bien."), ("00:00:03,000", "00:00:04,000", "What are you doing? I know."));
        var bytes = Encoding.GetEncoding(1252).GetBytes(srt);
        var j = await Read(bytes, "show.srt", "vtt");
        Assert.Equal("windows-1252", (string)j["detected"]!["encoding"]!);
        Assert.Contains("Où êtes-vous", Text(j));
        Assert.DoesNotContain("encoding-guessed", Codes(j));

        var arabic = Encoding.GetEncoding(1256).GetBytes(Arabic);
        var forced = await Read(arabic, "show.srt", "vtt", new { encoding = new { input = "windows-1252" } });
        Assert.Equal("windows-1252", (string)forced["detected"]!["encoding"]!);
        Assert.Equal("forced", (string)forced["detected"]!["encodingConfidence"]!);
        Assert.Contains("encoding-forced", Codes(forced));
        Assert.DoesNotContain("أنا", Text(forced));
    }

    [Fact]
    public async Task Encoding_language_retry_selects_arabic_with_language_applied()
    {
        var bytes = Encoding.GetEncoding(1256).GetBytes(Arabic);
        var j = await Read(bytes, "show.srt", "vtt", new { encoding = new { language = "ar" } });
        Assert.Equal("windows-1256", (string)j["detected"]!["encoding"]!);
        Assert.Equal("language", (string)j["detected"]!["encodingConfidence"]!);
        Assert.Contains("language-applied", Codes(j));
        Assert.Contains("أنا في البيت", Text(j));
    }

    [Fact]
    public async Task Unknown_encoding_language_is_422()
    {
        // Must not be valid UTF-8, or Decode never reaches the language filter.
        var bytes = Encoding.GetEncoding(1256).GetBytes(Arabic);
        var p = await Read(bytes, "show.srt", profile: new { encoding = new { language = "xx-not-a-lang" } }, expected: HttpStatusCode.UnprocessableEntity);
        Assert.Equal("invalid-profile", (string)p["code"]!);
        Assert.Contains("/encoding/language", p.ToJsonString());
    }

    [Fact]
    public async Task Genuine_encoding_clash_returns_candidates_with_samples()
    {
        // Concatenate Greek-1253 and Arabic-1256 SRTs so both distinctive scripts score with different text.
        var greek = Encoding.GetEncoding(1253).GetBytes(Greek);
        var arabic = Encoding.GetEncoding(1256).GetBytes(Arabic);
        var mixed = new byte[greek.Length + arabic.Length];
        Buffer.BlockCopy(greek, 0, mixed, 0, greek.Length);
        Buffer.BlockCopy(arabic, 0, mixed, greek.Length, arabic.Length);
        var p = await Read(mixed, "show.srt", expected: HttpStatusCode.UnprocessableEntity);
        Assert.Equal("ambiguous-encoding", (string)p["code"]!);
        Assert.Equal("text-encoding", (string)p["probe"]!);
        var candidates = p["candidates"]!.AsArray();
        Assert.True(candidates.Count >= 2, p.ToJsonString());
        foreach (var c in candidates)
        {
            Assert.False(c is System.Text.Json.Nodes.JsonValue, p.ToJsonString());
            Assert.NotNull(c!["encoding"]);
            Assert.NotNull(c["sample"]);
            Assert.True(((string)c["sample"]!).Length > 0);
        }
        Assert.Contains("/encoding/input", p.ToJsonString());
        Assert.Contains("/encoding/language", p.ToJsonString());
    }

    [Fact]
    public async Task Smoke_cp1252_and_utf16_samples_still_convert()
    {
        var j = await Read(File.ReadAllBytes(SmokeFile("sample-cp1252.srt")), "sample-cp1252.srt", "vtt");
        Assert.DoesNotContain("\uFFFD", Text(j));
        var u = await Read(File.ReadAllBytes(SmokeFile("sample-utf16.srt")), "sample-utf16.srt", "vtt");
        Assert.StartsWith("utf-16", ((string)u["detected"]!["encoding"]!).ToLowerInvariant());
    }

    // 6. target.fps semantics

    [Fact]
    public async Task Vtt_to_scc_with_target_fps_keeps_wall_clock_time()
    {
        var vtt = "WEBVTT\n\n01:00:00.000 --> 01:00:02.000\nYou know what we are doing\n";
        var j = await Json(new { content = vtt, fileName = "a.vtt", target = "scc", profile = new { target = new { fps = 29.97 } } });
        // The caption is displayed (942f) at one hour; libse loads it a few frames earlier.
        Assert.Matches(@"(?m)^01:00:00[:;]0[0-1]\t942f", Text(j));
        Assert.DoesNotMatch(@"(?m)^00:5[0-8]:", Text(j));
    }

    [Fact]
    public async Task Time_based_sources_are_not_rescaled_by_target_fps()
    {
        var srt = Srt(("00:10:00,000", "00:10:02,000", "Hello there"));
        var same = await Json(new { content = srt, target = "srt", profile = new { target = new { fps = 25 } } });
        Assert.Contains("00:10:00,000 --> 00:10:02,000", Text(same));
        var stl = await Binary("WEBVTT\n\n00:10:00.000 --> 00:10:02.000\nHello there\n", "ebustl", new { target = new { fps = 25 } }, "a.vtt");
        var back = await Read(stl, "a.stl");
        Assert.Contains("00:10:00,000 --> 00:10:02,000", Text(back));
    }

    [Fact]
    public async Task Explicit_source_fps_and_target_fps_still_rescale()
    {
        var srt = Srt(("00:10:00,000", "00:10:02,000", "Hello there"));
        var j = await Json(new { content = srt, target = "srt", profile = new { source = new { fps = 25 }, target = new { fps = 23.976 } } });
        Assert.Contains("00:10:25,", Text(j)); // 600 s × 25 / 23.976 = 625.6 s
    }


    // 7. SCC drop-frame

    private const string SccDropFrame = "Scenarist_SCC V1.0\r\n\r\n00:00:01;15\t9420 9420 94ae 94ae 9452 9452 c845 4c4c 4f80 942f 942f\r\n\r\n00:00:03;00\t942c 942c\r\n";
    private const string SccLowFrames = "Scenarist_SCC V1.0\r\n\r\n00:00:01:15\t9420 9420 94ae 94ae 9452 9452 c845 4c4c 4f80 942f 942f\r\n\r\n00:00:03:00\t942c 942c\r\n";

    [Fact]
    public async Task Scc_drop_frame_is_read_at_29_97()
    {
        var j = await Json(new { content = SccDropFrame, fileName = "a.scc", target = "srt" });
        Assert.Equal(29.97, (double)Details(j)["frameRate"]!, 2);
        Assert.Equal("inferred", (string)Details(j)["frameRateSource"]!);
        // libse 5.4 shows a pop-on caption at its end-of-caption code, 9 words (frames) after 00:00:01;15: frame 24 at
        // 29.97 ≈ 801 ms (at 23.976 it would be 1001 ms). libse 5.1 used the line's timecode (1.500 s).
        Assert.Contains("00:00:01,801", Text(j));
        Assert.DoesNotContain("frame-rate-assumed", Codes(j));
    }

    [Fact]
    public async Task Scc_without_a_telling_timecode_needs_source_fps()
    {
        var p = await Json(new { content = SccLowFrames, fileName = "a.scc", target = "srt" }, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("frame-rate-required", (string)p["code"]!);
        Assert.Equal("/source/fps", (string)p["errors"]![0]!["pointer"]!);
        var j = await Json(new { content = SccLowFrames, fileName = "a.scc", target = "srt", profile = new { source = new { fps = 29.97 } } });
        Assert.Equal("profile", (string)Details(j)["frameRateSource"]!);
        Assert.Contains("00:00:01,801", Text(j)); // displayed at the end-of-caption code (libse 5.4)
    }

    // 8. Source-cue warnings

    [Fact]
    public async Task Overlapping_and_long_cues_are_reported_with_cue_numbers()
    {
        var srt = Srt(("00:00:01,000", "00:00:04,000", "One"), ("00:00:03,000", "00:00:05,000", "Two overlaps"),
            ("00:00:06,000", "00:00:46,500", "Three is long"), ("00:00:47,000", "00:00:47,000", "Zero"));
        var j = await Json(new { content = srt, target = "vtt" });
        Assert.Equal([2], Warning(j, "overlapping-cues")["details"]!["cues"]!.AsArray().Select(n => (int)n!));
        var longCue = Warning(j, "long-cue");
        Assert.Equal(3, (int)longCue["details"]!["durations"]![0]!["cue"]!);
        Assert.Equal(40500, (int)longCue["details"]!["durations"]![0]!["durationMs"]!);
        Assert.Contains("zero-length-cue", Codes(j));
        Assert.Contains("00:00:03.000 --> 00:00:05.000", Text(j)); // times unchanged
    }

    // 9. EBU STL programme start

    [Fact]
    public async Task Stl_with_non_zero_tcp_warns_and_keeps_times()
    {
        var stl = await Binary(Srt(("00:00:01,000", "00:00:02,000", "Hello there")), "ebustl", new { formatOptions = new { ebuStl = new { frameRate = 25 } } });
        Encoding.ASCII.GetBytes("10000000").CopyTo(stl, 256);
        var j = await Read(stl, "a.stl");
        var w = Warning(j, "programme-start-not-applied");
        Assert.Equal("10:00:00:00", (string)w["details"]!["timecode"]!);
        Assert.Contains("timing.offsetMs", (string)w["hint"]!);
        Assert.Contains("00:00:01,000 --> 00:00:02,000", Text(j));
        stl = await Binary(Srt(("00:00:01,000", "00:00:02,000", "Hello there")), "ebustl");
        Assert.DoesNotContain("programme-start-not-applied", Codes(await Read(stl, "a.stl")));
    }

    // 4. format-defaults-applied

    [Fact]
    public async Task Fresh_stl_header_defaults_are_reported()
    {
        var j = await Json(new { content = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nYou know what we are doing\n", fileName = "a.vtt", target = "ebustl" });
        var fields = Warning(j, "format-defaults-applied")["details"]!["fields"]!.AsObject();
        Assert.Equal("No Title", (string)fields["originalProgrammeTitle"]!);
        Assert.Equal("USA", (string)fields["countryOfOrigin"]!);
        Assert.NotNull(fields["language"]);
        Assert.Contains("formatOptions.ebuStl", (string)Warning(j, "format-defaults-applied")["hint"]!);

        var set = await Json(new { content = Samples.Srt, target = "ebustl", profile = new { formatOptions = new { ebuStl = new { originalProgrammeTitle = "Show", countryOfOrigin = "BEL", language = "en" } } } });
        Assert.DoesNotContain("format-defaults-applied", Codes(set));
    }

    /// <summary>The GSI language code (LC, bytes 14-15 as ASCII hex).</summary>
    private static string GsiLanguage(JsonObject j) => Encoding.ASCII.GetString(Convert.FromBase64String((string)j["outputs"]![0]!["content"]!), 14, 2);


    [Theory]
    [InlineData("Greek", "70")]
    [InlineData("Hebrew", "6C")]
    [InlineData("Arabic", "7E")]
    public async Task Stl_auto_language_writes_the_appendix_3_code_for_detected_non_latin_text(string name, string hex)
    {
        var text = name switch { "Greek" => Greek, "Hebrew" => Hebrew, _ => Arabic };
        var j = await Json(new { content = text, fileName = "a.srt", target = "ebustl", profile = new { target = new { fps = 25 } } });
        Assert.Equal(hex, GsiLanguage(j));
        var w = Warning(j, "format-defaults-applied");
        Assert.Equal(hex, (string)w["details"]!["fields"]!["language"]!);
        Assert.Matches($@"language {hex} \(auto-detected: \w+\)", (string)w["message"]!);
        Assert.DoesNotContain("fallback", (string)w["message"]!);
    }

    [Fact]
    public async Task Stl_language_el_maps_to_70_without_a_warning_about_it()
    {
        var j = await Json(new { content = Greek, fileName = "a.srt", target = "ebustl", profile = new { target = new { fps = 25 }, formatOptions = new { ebuStl = new { language = "el", originalProgrammeTitle = "T", countryOfOrigin = "GRC" } } } });
        Assert.Equal("70", GsiLanguage(j));
        Assert.DoesNotContain("format-defaults-applied", Codes(j));
        Assert.DoesNotContain("ebu-language-mismatch", Codes(j));
    }

    [Fact]
    public async Task Stl_explicit_language_that_disagrees_with_the_detected_one_warns()
    {
        var j = await Json(new { content = Greek, fileName = "a.srt", target = "ebustl", profile = new { target = new { fps = 25 }, formatOptions = new { ebuStl = new { language = "14", originalProgrammeTitle = "T", countryOfOrigin = "GRC" } } } });
        Assert.Equal("14", GsiLanguage(j));
        var w = Warning(j, "ebu-language-mismatch");
        Assert.Equal("formatOptions.ebuStl.language \"14\" writes EBU language code 14, but the text looks like el (EBU code 70).", (string)w["message"]!);
        Assert.Contains("set formatOptions.ebuStl.language to \"70\" or \"auto\"", (string)w["hint"]!);
        var f = w["details"]!["fields"]!;
        Assert.Equal("14", (string)f["language"]!); Assert.Equal("el", (string)f["detectedLanguage"]!); Assert.Equal("70", (string)f["detectedCode"]!);
        // An agreeing value, "auto", and no setting give no mismatch.
        foreach (var lang in new[] { "70", "el", "auto" })
        {
            var ok = await Json(new { content = Greek, fileName = "a.srt", target = "ebustl", profile = new { target = new { fps = 25 }, formatOptions = new { ebuStl = new { language = lang, originalProgrammeTitle = "T", countryOfOrigin = "GRC" } } } });
            Assert.DoesNotContain("ebu-language-mismatch", Codes(ok));
        }
    }

    [Fact]
    public async Task Stl_language_mismatch_is_skipped_when_nothing_is_detected()
    {
        var j = await Json(new { content = "1\r\n00:00:01,000 --> 00:00:02,000\r\n...\r\n", fileName = "a.srt", target = "ebustl", profile = new { target = new { fps = 25 }, formatOptions = new { ebuStl = new { language = "14", originalProgrammeTitle = "T", countryOfOrigin = "GRC" } } } });
        Assert.DoesNotContain("ebu-language-mismatch", Codes(j));
    }

    [Fact]
    public async Task Stl_fallback_for_a_detected_language_without_a_mapping_says_so()
    {
        var j = await Json(new { content = UnmappedLanguage, fileName = "a.srt", target = "ebustl", profile = new { target = new { fps = 25 } } });
        var w = Warning(j, "format-defaults-applied");
        Assert.Matches(@"language 09 \(fallback: detected af has no built-in EBU mapping; set formatOptions\.ebuStl\.language to its two-digit upper-case hex code from EBU Tech 3264 Appendix 3\)", (string)w["message"]!);
        Assert.DoesNotContain("ISO 639-1 table", (string)w["message"]!);
    }

    [Fact]
    public async Task Stl_language_error_lists_every_iso_code_and_the_hex_route()
    {
        var j = await Json(new { content = Samples.Srt, target = "ebustl", profile = new { formatOptions = new { ebuStl = new { language = "xx" } } } }, HttpStatusCode.BadRequest);
        var hint = (string)j["errors"]![0]!["hint"]!;
        Assert.Equal("'xx' has no EBU STL language code.", (string)j["errors"]![0]!["message"]!);
        Assert.Contains($"Use one of these ISO 639-1 codes: {string.Join(", ", SmartSubtitleConverterApi.Binary.BinaryOptions.EbuLanguages.Keys)}.", hint);
        Assert.Contains("el, ", hint);
        Assert.Contains("two-digit upper-case hex code from EBU Tech 3264 Appendix 3 (for example Dari \"73\")", hint);
        Assert.Contains("el, ", hint); Assert.Contains("ru, ", hint);
        Assert.DoesNotContain("...", hint);
    }

    private Task<JsonObject> StlWith(object ebu, string? content = null, HttpStatusCode? status = null) =>
        Json(new { content = content ?? Samples.Srt, fileName = "a.srt", target = "ebustl", profile = new { target = new { fps = 25 }, formatOptions = new { ebuStl = ebu } } }, status ?? HttpStatusCode.OK);

    private static JsonNode Header(JsonObject j) => j["outputs"]![0]!["header"]!;

    [Theory]
    [InlineData("el")]
    [InlineData("EL")]
    [InlineData(" el ")]
    [InlineData("\n70\n")]
    [InlineData("70")]
    [InlineData("  Auto ")]
    public async Task Stl_language_ignores_case_and_surrounding_whitespace(string lang)
    {
        var j = await StlWith(new { language = lang, originalProgrammeTitle = "T", countryOfOrigin = "GRC" }, Greek);
        Assert.Equal("70", GsiLanguage(j));
        Assert.Equal("70 (Greek)", (string)Header(j)["language"]!);
        Assert.DoesNotContain("ebu-language-mismatch", Codes(j));
    }

    [Fact]
    public async Task Stl_hex_language_is_written_upper_case()
    {
        var j = await StlWith(new { language = " 6c ", originalProgrammeTitle = "T", countryOfOrigin = "ISR" });
        Assert.Equal("6C", GsiLanguage(j));
        Assert.Equal("6C (Hebrew)", (string)Header(j)["language"]!);
    }

    private static string GsiCountry(JsonObject j) => Encoding.ASCII.GetString(Convert.FromBase64String((string)j["outputs"]![0]!["content"]!), 274, 3);

    [Theory]
    [InlineData("BEL", "BEL")]
    [InlineData("bel", "BEL")]
    [InlineData("BE", "BEL")]
    [InlineData("be", "BEL")]
    [InlineData(" Belgium ", "BEL")]
    [InlineData("belgium", "BEL")]
    [InlineData("\nBELGIUM\t", "BEL")]
    [InlineData("Cote d'Ivoire", "CIV")]
    [InlineData("Côte d’Ivoire", "CIV")]
    [InlineData("United Kingdom", "GBR")]
    [InlineData("UK", "GBR")]
    [InlineData("Korea, Republic of", "KOR")]
    [InlineData("Türkiye", "TUR")]
    public async Task Stl_country_resolves_to_alpha_3(string input, string alpha3)
    {
        var j = await StlWith(new { originalProgrammeTitle = "T", countryOfOrigin = input, language = "en" });
        Assert.Equal(alpha3, GsiCountry(j));
        Assert.StartsWith(alpha3 + " (", (string)Header(j)["countryOfOrigin"]!);
    }

    [Fact]
    public async Task Stl_unknown_country_is_rejected_with_suggestions()
    {
        var j = await StlWith(new { originalProgrammeTitle = "T", countryOfOrigin = "Belgum" }, status: HttpStatusCode.BadRequest);
        var e = j["errors"]![0]!;
        Assert.Equal("format-option-invalid", (string)j["code"]!);
        Assert.Equal("/formatOptions/ebuStl/countryOfOrigin", (string)e["pointer"]!);
        Assert.Equal("'Belgum' is not an ISO 3166-1 country.", (string)e["message"]!);
        var hint = (string)e["hint"]!;
        Assert.Contains("alpha-3 code (BEL), an alpha-2 code (BE) or the English country name (Belgium)", hint);
        Assert.Contains("Did you mean Belgium (BEL)?", hint);
        var none = await StlWith(new { originalProgrammeTitle = "T", countryOfOrigin = "Qwxzv" }, status: HttpStatusCode.BadRequest);
        Assert.DoesNotContain("Did you mean", (string)none["errors"]![0]!["hint"]!);
        Assert.True(hint.Split("(").Length - 1 <= 5);
    }

    [Theory]
    [InlineData("Germny", "Germany (DEU)")]
    [InlineData("Untied States", "United States (USA)")]
    [InlineData("Portugl", "Portugal (PRT)")]
    [InlineData("Brasil", "Brazil (BRA)")]
    public async Task Stl_country_suggestions_name_the_likely_country(string typo, string suggestion)
    {
        var j = await StlWith(new { originalProgrammeTitle = "T", countryOfOrigin = typo }, status: HttpStatusCode.BadRequest);
        Assert.Contains(suggestion, (string)j["errors"]![0]!["hint"]!);
    }

    private async Task<JsonObject> Validate(string profile) =>
        await (await _c.PostAsync("/v1/profiles/validate", new StringContent(profile, Encoding.UTF8, "application/json"))).Json();

    [Fact]
    public async Task Validate_profile_lists_the_schema_and_the_format_option_errors_together()
    {
        var v = await Validate("""{"target":{"format":"ebustl"},"formatOptions":{"ebuStl":{"countryOfOrigin":"Belgique","programmeTitle":"Spring Promo"}}}""");
        Assert.False((bool)v["valid"]!);
        var errors = v["errors"]!.AsArray();
        Assert.Equal(2, errors.Count);
        // Schema errors first, then format-option errors, always in this order.
        Assert.Equal("/formatOptions/ebuStl/programmeTitle", (string)errors[0]!["pointer"]!);
        Assert.Equal("Unknown property 'programmeTitle'.", (string)errors[0]!["message"]!);
        Assert.Equal("/formatOptions/ebuStl/countryOfOrigin", (string)errors[1]!["pointer"]!);
        Assert.Contains("Did you mean Belgium (BEL)?", (string)errors[1]!["hint"]!);
        for (var i = 0; i < 3; i++) Assert.Equal(errors.ToJsonString(), (await Validate("""{"target":{"format":"ebustl"},"formatOptions":{"ebuStl":{"countryOfOrigin":"Belgique","programmeTitle":"Spring Promo"}}}"""))["errors"]!.ToJsonString());
    }

    [Fact]
    public async Task Validate_profile_collects_every_error_class()
    {
        var v = await Validate("""{"timing":{"ofsetMs":1},"formatOptions":{"ebuStl":{"language":"xx","countryOfOrigin":"Atlantis","frameRate":26,"maxRows":"many"}}}""");
        var pointers = v["errors"]!.AsArray().Select(e => (string)e!["pointer"]!).ToList();
        Assert.Equal(["/timing/ofsetMs", "/formatOptions/ebuStl/maxRows", "/formatOptions/ebuStl/frameRate", "/formatOptions/ebuStl/language", "/formatOptions/ebuStl/countryOfOrigin"], pointers);
    }

    [Fact]
    public async Task Validate_profile_with_a_malformed_block_reports_the_shape_only()
    {
        var v = await Validate("""{"formatOptions":{"ebuStl":"greek"},"operations":[{"id":"SplitLong"},{"id":"DeleteFirst"}]}""");
        Assert.False((bool)v["valid"]!);
        var pointers = v["errors"]!.AsArray().Select(e => (string)e!["pointer"]!).ToList();
        Assert.Contains("/formatOptions/ebuStl", pointers);
        Assert.Contains("/operations/0/id", pointers);
    }

    [Fact]
    public async Task Convert_lists_an_unknown_property_and_a_bad_country_together()
    {
        var j = await Json(new { content = Samples.Srt, target = "ebustl", profile = new { formatOptions = new { ebuStl = new { countryOfOrigin = "Belgique", programmeTitle = "T" } } } }, HttpStatusCode.BadRequest);
        Assert.Equal("invalid-profile", (string)j["code"]!);
        var pointers = j["errors"]!.AsArray().Select(e => (string)e!["pointer"]!).ToList();
        Assert.Equal(["/formatOptions/ebuStl/programmeTitle", "/formatOptions/ebuStl/countryOfOrigin"], pointers);
    }

    [Fact]
    public async Task Convert_keeps_the_422_for_an_unsupported_target_and_lists_the_option_error_too()
    {
        var j = await Json(new { content = Samples.Srt, target = "bluraysup", profile = new { formatOptions = new { ebuStl = new { countryOfOrigin = "Belgique" } } } }, (HttpStatusCode)422);
        Assert.Equal("format-not-supported", (string)j["code"]!);
        var pointers = j["errors"]!.AsArray().Select(e => (string)e!["pointer"]!).ToList();
        Assert.Equal(["/target/format", "/formatOptions/ebuStl/countryOfOrigin"], pointers);
    }

    [Fact]
    public async Task Convert_with_only_option_errors_is_still_format_option_invalid()
    {
        var j = await Json(new { content = Samples.Srt, target = "ebustl", profile = new { formatOptions = new { ebuStl = new { countryOfOrigin = "Belgique", language = "xx" } } } }, HttpStatusCode.BadRequest);
        Assert.Equal("format-option-invalid", (string)j["code"]!);
        Assert.Equal(2, j["errors"]!.AsArray().Count);
    }

    [Fact]
    public async Task Stl_result_echoes_the_header_that_was_written()
    {
        var gr = await StlWith(new { language = "el", originalProgrammeTitle = "Show", originalEpisodeTitle = "Ep 1", translatedProgrammeTitle = "Εκπομπή", translatorsName = "A. B.", publisher = "Pub", countryOfOrigin = "GRC" }, Greek);
        var h = Header(gr);
        Assert.Equal("70 (Greek)", (string)h["language"]!);
        Assert.Equal("GRC (Greece)", (string)h["countryOfOrigin"]!);
        Assert.Equal("Show", (string)h["originalProgrammeTitle"]!);
        Assert.Equal("Ep 1", (string)h["originalEpisodeTitle"]!);
        Assert.Equal("A. B.", (string)h["translatorsName"]!);
        Assert.Equal("Pub", (string)h["publisher"]!);

        var es = await StlWith(new { language = "es", originalProgrammeTitle = "Show", countryOfOrigin = "Spain" });
        Assert.Equal("ESP (Spain)", (string)Header(es)["countryOfOrigin"]!);
        Assert.Equal("0A (Spanish)", (string)Header(es)["language"]!);
        // Empty fields are left out.
        Assert.Null(Header(es)["publisher"]); Assert.Null(Header(es)["translatorsName"]);
    }

    [Fact]
    public async Task Non_ebu_outputs_have_no_header_echo()
    {
        var j = await Json(new { content = Samples.Srt, target = "webvtt" });
        Assert.Null(j["outputs"]![0]!["header"]);
    }

    [Fact]
    public async Task Stl_header_copied_from_the_source_is_not_reported()
    {
        var stl = await Binary(Samples.Srt, "ebustl", new { formatOptions = new { ebuStl = new { originalProgrammeTitle = "Show", countryOfOrigin = "BEL", language = "en" } } });
        var j = await Read(stl, "a.stl", "ebustl");
        Assert.DoesNotContain("format-defaults-applied", Codes(j));
    }

    [Fact]
    public async Task Cavena_title_and_language_defaults_are_reported()
    {
        var j = await Json(new { content = BinaryFormatTests.Srt, fileName = "episode.srt", target = "cavena890", profile = new { target = new { fps = 25 } } });
        var fields = Warning(j, "format-defaults-applied")["details"]!["fields"]!.AsObject();
        Assert.Equal("episode", (string)fields["title"]!);
        Assert.NotNull(fields["language"]);
        var set = await Json(new { content = BinaryFormatTests.Srt, fileName = "episode.srt", target = "cavena890", profile = new { target = new { fps = 25 }, formatOptions = new { cavena890 = new { title = "EPISODE", language = "en" } } } });
        Assert.DoesNotContain("format-defaults-applied", Codes(set));
    }
}
