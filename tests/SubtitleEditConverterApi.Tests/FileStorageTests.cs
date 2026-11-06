using System.Net.Http.Json;
using SubtitleEditConverterApi.Storage;
namespace SubtitleEditConverterApi.Tests;
public sealed class FileStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "se-storage-" + Guid.NewGuid());
    public FileStorageTests() { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "in.srt"), Samples.Srt); }
    public void Dispose() => Directory.Delete(root, true);
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Read_convert_write_sidecar_and_detect(bool atomic)
    {
        using var f = ApiFactory.With(new() { ["STORAGE_FILE_ROOTS"] = root, ["STORAGE_FILE_ATOMIC"] = atomic.ToString() }); var c = f.CreateClient();
        var output = Path.Combine(root, "out.vtt");
        var res = await c.PostAsJsonAsync("/v1/convert/json", new { input = new Uri(Path.Combine(root, "in.srt")).AbsoluteUri, output, target = "vtt", sidecar = true });
        Assert.Equal(200, (int)res.StatusCode);
        var j = await res.Json();
        Assert.Null(j["outputs"]![0]!["content"]);
        Assert.Contains("WEBVTT", File.ReadAllText(output));
        var metadata = File.ReadAllText(output + ".json");
        Assert.Contains((string)j["id"]!, metadata); Assert.DoesNotContain("Hello", metadata);
        Assert.DoesNotContain(Directory.GetFiles(root), p => p.EndsWith(".partial"));
        var detect = await c.PostAsJsonAsync("/v1/detect", new { input = Path.Combine(root, "in.srt") });
        Assert.Equal("subrip", (string)(await detect.Json())["id"]!);
        var exists = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, output, target = "vtt" });
        Assert.Equal(409, (int)exists.StatusCode);
    }
    [Fact]
    public async Task Sidecar_failure_keeps_subtitle_with_warning()
    {
        var output = Path.Combine(root, "out.vtt"); File.WriteAllText(output + ".json", "existing");
        using var f = ApiFactory.With(new() { ["STORAGE_FILE_ROOTS"] = root }); var c = f.CreateClient();
        var res = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, output, sidecar = true, target = "vtt" });
        Assert.Equal(200, (int)res.StatusCode); Assert.True(File.Exists(output));
        Assert.Contains("sidecar-write-failed", (await res.Json())["warnings"]!.ToJsonString());
        Assert.Equal("existing", File.ReadAllText(output + ".json"));
    }
    [Fact]
    public async Task Disabled_escape_dot_segment_missing_and_symlinks_are_rejected()
    {
        using var off = new ApiFactory();
        Assert.Equal(422, (int)(await off.CreateClient().PostAsJsonAsync("/v1/detect", new { input = Path.Combine(root, "in.srt") })).StatusCode);
        using var f = ApiFactory.With(new() { ["STORAGE_FILE_ROOTS"] = root }); var c = f.CreateClient();
        foreach (var input in new[] { "/etc/passwd", root + "/../in.srt", "file://" + root + "/../in.srt", root + "/" })
            Assert.Equal(422, (int)(await c.PostAsJsonAsync("/v1/detect", new { input })).StatusCode);
        File.CreateSymbolicLink(Path.Combine(root, "link.srt"), "/etc/passwd");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), "/tmp");
        Assert.Equal(422, (int)(await c.PostAsJsonAsync("/v1/detect", new { input = Path.Combine(root, "link.srt") })).StatusCode);
        Assert.Equal(422, (int)(await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, output = Path.Combine(root, "linked/out.vtt"), target = "vtt" })).StatusCode);
        Assert.Equal(404, (int)(await c.PostAsJsonAsync("/v1/detect", new { input = Path.Combine(root, "absent.srt") })).StatusCode);
        Assert.Equal(422, (int)(await c.PostAsJsonAsync("/v1/convert/json", new { input = Path.Combine(root, "in.srt"), content = Samples.Srt, target = "vtt" })).StatusCode);
    }
}
