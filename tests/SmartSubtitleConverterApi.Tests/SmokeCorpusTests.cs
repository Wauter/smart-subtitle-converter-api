using System.Net;

namespace SmartSubtitleConverterApi.Tests;

/// <summary>
/// Sanity, not byte-for-byte: every file in tests/smoke converts through a few presets, the
/// output is re-detectable and keeps the cue count (design §9). Catches libse bumps that break
/// formats we rely on. Placeholder samples for now; real-world files go in the same folder.
/// </summary>
public class SmokeCorpusTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    public static TheoryData<string> Files()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "smoke"))) dir = dir.Parent;
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(dir!.FullName, "tests", "smoke")).Order()) data.Add(file);
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Converts_through_presets_and_round_trips(string path)
    {
        var c = f.CreateClient();
        var bytes = await File.ReadAllBytesAsync(path);
        var detect = await c.PostAsync("/v1/detect", Samples.Form(bytes, Path.GetFileName(path), ("encoding", "")));
        Assert.Equal(HttpStatusCode.OK, detect.StatusCode);
        var cues = (int)(await detect.Json())["paragraphs"]!;
        Assert.True(cues > 0);

        foreach (var preset in new[] { "srt-to-vtt", "srt-to-ass", "to-ttml" })
        {
            var profile = path.EndsWith(".sub") ? """{"source":{"fps":25}}""" : "{}";
            var r = await c.PostAsync("/v1/convert", Samples.Form(bytes, Path.GetFileName(path), ("preset", preset), ("profile", profile)));
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{preset}: {await r.Content.ReadAsStringAsync()}");
            Assert.Equal(cues.ToString(), r.Header("X-Cue-Count"));
            var output = await r.Content.ReadAsByteArrayAsync();
            Assert.NotEmpty(output);
            var again = await c.PostAsync("/v1/detect", Samples.Form(output, r.Content.Headers.ContentDisposition!.FileName!.Trim('"')));
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(cues, (int)(await again.Json())["paragraphs"]!);
        }
    }
}
