using System.IO.Compression;
using System.Net;
using System.Text;

namespace SubtitleEditConverterApi.Tests;

public class HostileInputTests : IClassFixture<HostileInputTests.SmallLimits>
{
    public sealed class SmallLimits() : ApiFactory(new() { ["MAX_UPLOAD_BYTES"] = "200000", ["ZIP_MAX_ENTRIES"] = "5", ["MAX_LINE_CHARS"] = "5000" });

    private readonly HttpClient _c;
    public HostileInputTests(SmallLimits f) => _c = f.CreateClient();

    private async Task<(int Status, string Code)> Post(byte[] body, string name, params (string, string)[] fields)
    {
        var r = await _c.PostAsync("/v1/convert", Samples.Form(body, name, fields.Length > 0 ? fields : [("target", "srt")]));
        var text = await r.Content.ReadAsStringAsync();
        var code = r.Content.Headers.ContentType?.MediaType == "application/problem+json"
            ? (string)System.Text.Json.Nodes.JsonNode.Parse(text)!["code"]! : "";
        return ((int)r.StatusCode, code);
    }

    [Fact]
    public async Task Xxe_is_rejected() =>
        Assert.Equal((422, "unsafe-xml"), await Post(Encoding.UTF8.GetBytes("""
            <?xml version="1.0"?>
            <!DOCTYPE tt [<!ENTITY x SYSTEM "file:///etc/passwd">]>
            <tt xmlns="http://www.w3.org/ns/ttml"><body><div><p begin="00:00:01.000" end="00:00:02.000">&x;</p></div></body></tt>
            """), "x.ttml"));

    [Fact]
    public async Task Billion_laughs_is_rejected() =>
        Assert.Equal((422, "unsafe-xml"), await Post(Encoding.UTF8.GetBytes("""
            <?xml version="1.0"?>
            <!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;"><!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">]>
            <tt xmlns="http://www.w3.org/ns/ttml"><body><div><p begin="00:00:01.000" end="00:00:02.000">&lol3;</p></div></body></tt>
            """), "x.ttml"));

    [Fact]
    public async Task Oversized_body_is_413()
    {
        var big = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Samples.Srt, 3000)));
        Assert.Equal((413, "payload-too-large"), await Post(big, "big.srt"));
    }

    [Fact]
    public async Task Zip_bomb_by_ratio_and_size_is_413()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var s = z.CreateEntry("bomb.srt", CompressionLevel.SmallestSize).Open();
            s.Write(new byte[20_000_000]);
        }
        Assert.Equal((413, "zip-limits-exceeded"), await Post(ms.ToArray(), "bomb.zip"));
    }

    [Theory]
    [InlineData("../evil.srt")]
    [InlineData("/etc/evil.srt")]
    [InlineData("dir\\evil.srt")]
    [InlineData("C:evil.srt")]
    public async Task Zip_path_traversal_is_rejected(string name) =>
        Assert.Equal((422, "unsafe-zip"), await Post(Samples.Zip((name, Encoding.UTF8.GetBytes(Samples.Srt))), "e.zip"));

    [Fact]
    public async Task Too_many_zip_entries_is_413() =>
        Assert.Equal((413, "zip-limits-exceeded"), await Post(Samples.Zip(Enumerable.Range(0, 6).Select(i => ($"{i}.srt", Encoding.UTF8.GetBytes(Samples.Srt))).ToArray()), "many.zip"));

    [Fact]
    public async Task Pathological_regex_times_out_cleanly()
    {
        var evil = "1\r\n00:00:01,000 --> 00:00:02,000\r\n" + new string('a', 3000) + "!\r\n";
        var profile = """{"operations":[{"id":"MultipleReplace","rules":[{"find":"(a+)+(?=b)","replace":"x","mode":"regex"}]}]}""";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (status, code) = await Post(Encoding.UTF8.GetBytes(evil), "r.srt", ("target", "srt"), ("profile", profile));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(status == 200 || code == "conversion-failed", $"{status} {code}");
    }

    [Fact]
    public async Task Very_long_line_is_rejected() =>
        Assert.Equal((422, "input-limits-exceeded"), await Post(Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\n" + new string('x', 6000)), "l.srt"));

    [Fact]
    public async Task Garbage_bytes_never_crash()
    {
        var rnd = new Random(42);
        for (var i = 0; i < 5; i++)
        {
            var bytes = new byte[rnd.Next(10, 5000)];
            rnd.NextBytes(bytes);
            var (status, code) = await Post(bytes, "g.srt");
            Assert.True(status is 200 or 422 or 400, $"{status} {code}");
        }
    }

    [Fact]
    public async Task Nothing_is_written_to_disk()
    {
        var tmp = Path.GetTempPath();
        var before = Directory.GetFiles(tmp).Length;
        await Post(Samples.Zip(("a.srt", Encoding.UTF8.GetBytes(Samples.Srt))), "a.zip");
        await Post(Encoding.UTF8.GetBytes(Samples.Srt), "a.srt");
        Assert.True(Directory.GetFiles(tmp).Length <= before);
    }
}
