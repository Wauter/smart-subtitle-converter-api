using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using SmartSubtitleConverterApi.Binary;

namespace SmartSubtitleConverterApi.Tests;

/// <summary>frame-rate-changed (1.1.0): the written EBU STL Disk Format Code implies a different rate than intended.</summary>
public class FrameRateWarningTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _c;
    public FrameRateWarningTests(ApiFactory f) => _c = f.CreateClient();

    private async Task<byte[]> Ebu(string frameRate)
    {
        var r = await BinaryFormatTests.ToBinary(_c, "ebustl", "{\"formatOptions\":{\"ebuStl\":{\"frameRate\":" + frameRate + "}}}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadAsByteArrayAsync();
    }

    private async Task<(JsonObject Json, string Dfc)> Convert(byte[] content, string fileName, object? profile)
    {
        var r = await _c.PostAsJsonAsync("/v1/convert/json", new { content = System.Convert.ToBase64String(content), contentEncoding = "base64", fileName, target = "ebustl", profile });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Json();
        var bytes = System.Convert.FromBase64String((string)j["outputs"]![0]!["content"]!);
        return (j, Encoding.ASCII.GetString(bytes, 3, 8));
    }

    private static JsonNode? FrameWarning(JsonObject j) =>
        j["warnings"]!.AsArray().FirstOrDefault(w => (string)w!["code"]! == BinaryOptions.FrameRateChangedCode);

    [Fact]
    public async Task Fires_for_a_2997_ebu_source_rewritten_with_defaults()
    {
        // An STL30.01 file whose cues are really 29.97 (the only code 29.97 can use), read as 29.97.
        var src = await Ebu("30");
        var (j, dfc) = await Convert(src, "in.stl", new { formatOptions = new { ebuStl = new { readFrameRate = 29.97 } } });
        Assert.Equal("STL30.01", dfc);
        var w = FrameWarning(j);
        Assert.NotNull(w);
        var msg = (string)w!["message"]!;
        Assert.Contains("STL30.01", msg);
        Assert.Contains("take as 30 fps", msg);
        Assert.Contains("source frame rate is 29.97 fps", msg);
        Assert.Contains("EBU Tech 3264 only defines STL25.01 and STL30.01", msg);
        Assert.Contains("formatOptions.ebuStl.frameRate", msg);
        Assert.Contains("headerFromSource:false", msg);
        Assert.Null(w["count"]);
    }

    [Fact]
    public async Task Fires_for_explicit_2997_on_an_ebu_source()
    {
        var (j, dfc) = await Convert(await Ebu("30"), "in.stl", new { formatOptions = new { ebuStl = new { frameRate = 29.97 } } });
        Assert.Equal("STL30.01", dfc);
        Assert.Contains("requested frame rate is 29.97 fps", (string)FrameWarning(j)!["message"]!);
    }

    [Fact]
    public async Task Fires_for_target_fps_2997_without_frameRate()
    {
        var (j, dfc) = await Convert(Encoding.UTF8.GetBytes(BinaryFormatTests.Srt), "in.srt", new { source = new { fps = 29.97 }, target = new { fps = 29.97 } });
        Assert.Equal("STL30.01", dfc);
        var msg = (string)FrameWarning(j)!["message"]!;
        Assert.Contains("requested frame rate is 29.97 fps", msg);
        Assert.Contains("take as 30 fps", msg);
    }

    [Fact]
    public async Task A_fresh_header_keeps_the_source_rate()
    {
        // headerFromSource:false and no rate requested: the fresh GSI takes the source header's rate (it used to default to STL25.01).
        var (j, dfc) = await Convert(await Ebu("30"), "in.stl", new { formatOptions = new { ebuStl = new { headerFromSource = false } } });
        Assert.Equal("STL30.01", dfc);
        Assert.Null(FrameWarning(j));
    }

    [Fact]
    public async Task A_fresh_header_falls_back_to_25_when_the_source_rate_is_no_ebu_rate()
    {
        var (j, dfc) = await Convert(Encoding.UTF8.GetBytes(BinaryFormatTests.Srt), "in.srt", new { source = new { fps = 50 } });
        Assert.Equal("STL25.01", dfc);
        Assert.Contains("source frame rate is 50 fps", (string)FrameWarning(j)!["message"]!);
    }

    [Fact]
    public async Task Silent_for_25_to_25()
    {
        var (j, dfc) = await Convert(await Ebu("25"), "in.stl", null);
        Assert.Equal("STL25.01", dfc);
        Assert.Null(FrameWarning(j));
        var (k, _) = await Convert(Encoding.UTF8.GetBytes(BinaryFormatTests.Srt), "in.srt", null);
        Assert.Null(FrameWarning(k));
    }

    [Theory]
    [InlineData("30", 30.0, null)]          // a 29.97 source, explicitly declared as 30
    [InlineData("25", 25.0, null)]
    [InlineData("30", null, 30.0)]          // target.fps matching the written code
    [InlineData("24", 24.0, null)]
    [InlineData("23.976", 23.976, null)]
    public async Task Silent_when_the_rate_is_explicitly_matched(string sourceRate, double? frameRate, double? targetFps)
    {
        var profile = new
        {
            target = targetFps is { } t ? new { fps = (double?)t } : null,
            formatOptions = new { ebuStl = new { readFrameRate = sourceRate == "30" ? 29.97 : (double?)null, frameRate } },
        };
        var (j, _) = await Convert(await Ebu(sourceRate), "in.stl", profile);
        Assert.Null(FrameWarning(j));
    }

    [Theory]
    [InlineData("STL25.01", 25)]
    [InlineData("STL30.01", 30)]
    [InlineData("STL23.01", 23.976)]
    [InlineData("STL24.01", 24)]
    [InlineData("STL29.01", 29)]
    [InlineData("STL50.01", 50)]
    [InlineData("XYZ", 30)]
    public void Implied_rate_follows_libse(string dfc, double fps) => Assert.Equal(fps, BinaryOptions.ImpliedFps(dfc), 3);

    [Fact]
    public void Unit_rules()
    {
        Assert.NotNull(BinaryOptions.FrameRateChanged("STL30.01", 29.97, null));
        Assert.NotNull(BinaryOptions.FrameRateChanged("STL30.01", null, 29.97));
        Assert.Null(BinaryOptions.FrameRateChanged("STL30.01", 30, 29.97));   // requested wins over source
        Assert.Null(BinaryOptions.FrameRateChanged("STL30.01", null, 30.005)); // within 0.01
        Assert.Null(BinaryOptions.FrameRateChanged("STL25.01", null, null));   // nothing intended
        Assert.Null(BinaryOptions.FrameRateChanged("STL23.01", 23, null));     // "23" is 23.976
    }
}
