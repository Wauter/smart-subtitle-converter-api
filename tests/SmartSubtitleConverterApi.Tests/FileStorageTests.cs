using System.Net.Http.Json;
using System.Runtime.Versioning;
using SmartSubtitleConverterApi.Storage;
namespace SmartSubtitleConverterApi.Tests;
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
    [Fact]
    public async Task Writing_without_sidecar_deletes_a_stale_sidecar_and_sidecar_replace_refreshes_it()
    {
        using var f = ApiFactory.With(new() { ["STORAGE_FILE_ROOTS"] = root }); var c = f.CreateClient();
        var output = Path.Combine(root, "out.vtt");
        var first = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, output, target = "vtt", sidecar = true });
        Assert.Equal(200, (int)first.StatusCode); Assert.True(File.Exists(output + ".json"));
        var again = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, output, target = "vtt", sidecar = true, ifExists = "replace" });
        Assert.Equal(200, (int)again.StatusCode);
        Assert.Contains((string)(await again.Json())["id"]!, File.ReadAllText(output + ".json"));
        var plain = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, output, target = "vtt", ifExists = "replace" });
        Assert.Equal(200, (int)plain.StatusCode);
        Assert.True(File.Exists(output)); Assert.False(File.Exists(output + ".json"));
        Assert.DoesNotContain("sidecar", (await plain.Json())["warnings"]!.ToJsonString());
    }
    [Fact]
    public async Task Sidecar_that_cannot_be_deleted_keeps_subtitle_with_warning()
    {
        var output = Path.Combine(root, "dir.vtt"); Directory.CreateDirectory(output + ".json");
        using var f = ApiFactory.With(new() { ["STORAGE_FILE_ROOTS"] = root }); var c = f.CreateClient();
        var res = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, output, target = "vtt" });
        Assert.Equal(200, (int)res.StatusCode); Assert.True(File.Exists(output));
        Assert.Contains("sidecar-delete-failed", (await res.Json())["warnings"]!.ToJsonString());
        Assert.True(Directory.Exists(output + ".json"));
    }
    [SupportedOSPlatform("linux")]
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Outputs_are_created_0644_subject_to_umask(bool atomic)
    {
        using var f = ApiFactory.With(new() { ["STORAGE_FILE_ROOTS"] = root, ["STORAGE_FILE_ATOMIC"] = atomic.ToString() }); var c = f.CreateClient();
        var output = Path.Combine(root, "mode.vtt");
        Assert.Equal(200, (int)(await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, output, target = "vtt", sidecar = true })).StatusCode);
        // A file created with 0644 by this process shows the same umask.
        var reference = Path.Combine(root, "reference");
        using (File.Create(reference)) { }
        File.Delete(reference);
        using (new FileStream(reference, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = (UnixFileMode)0x1A4 })) { }
        Assert.Equal(File.GetUnixFileMode(reference), File.GetUnixFileMode(output));
        Assert.Equal(File.GetUnixFileMode(reference), File.GetUnixFileMode(output + ".json"));
        Assert.True(File.GetUnixFileMode(output).HasFlag(UnixFileMode.UserRead | UnixFileMode.UserWrite));
    }
    [SupportedOSPlatform("linux")]
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Missing_output_folders_below_the_root_are_created_0755(bool atomic)
    {
        using var f = ApiFactory.With(new() { ["STORAGE_FILE_ROOTS"] = root, ["STORAGE_FILE_ATOMIC"] = atomic.ToString() }); var c = f.CreateClient();
        var output = Path.Combine(root, "out/2026/show.vtt");
        Assert.Equal(200, (int)(await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, output, target = "vtt", sidecar = true })).StatusCode);
        Assert.Contains("WEBVTT", File.ReadAllText(output)); Assert.True(File.Exists(output + ".json"));
        var reference = Path.Combine(root, "reference-dir");
        Directory.CreateDirectory(reference, (UnixFileMode)0x1ED);
        Assert.Equal(File.GetUnixFileMode(reference), File.GetUnixFileMode(Path.Combine(root, "out")));
        Assert.Equal(File.GetUnixFileMode(reference), File.GetUnixFileMode(Path.Combine(root, "out/2026")));
        // The file can be read back by location and listed.
        var detect = await c.PostAsJsonAsync("/v1/detect", new { input = output });
        Assert.Equal(200, (int)detect.StatusCode);
    }
    [SupportedOSPlatform("linux")]
    [Fact]
    public async Task Folder_creation_never_follows_symlinks_creates_hidden_folders_or_leaves_the_root()
    {
        var absentRoot = Path.Combine(root, "absent-root");
        var outside = Path.Combine(Path.GetTempPath(), "se-outside-" + Guid.NewGuid());
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
            Directory.CreateSymbolicLink(Path.Combine(root, "dangling"), Path.Combine(outside, "missing"));
            using var f = ApiFactory.With(new() { ["STORAGE_FILE_ROOTS"] = string.Join(Path.PathSeparator, root, absentRoot) }); var c = f.CreateClient();
            async Task<(int, string)> Write(string output)
            {
                var r = await c.PostAsJsonAsync("/v1/convert/json", new { content = Samples.Srt, output, target = "vtt" });
                return ((int)r.StatusCode, (string)(await r.Json())["code"]!);
            }
            Assert.Equal((422, "storage-location-rejected"), await Write(Path.Combine(root, "linked/new/out.vtt")));
            Assert.Equal((422, "storage-location-rejected"), await Write(Path.Combine(root, "dangling/new/out.vtt")));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
            Assert.Equal((422, "hidden-path"), await Write(Path.Combine(root, ".hidden/out.vtt")));
            Assert.False(Directory.Exists(Path.Combine(root, ".hidden")));
            Assert.Equal((404, "storage-not-found"), await Write(Path.Combine(absentRoot, "sub/out.vtt"))); // a root itself is never created
            Assert.False(Directory.Exists(absentRoot));
            File.WriteAllText(Path.Combine(root, "file"), "x");
            Assert.Equal((422, "storage-location-rejected"), await Write(Path.Combine(root, "file/sub/out.vtt"))); // ENOTDIR
            // Reads never create anything: a missing folder is 404.
            var read = await c.PostAsJsonAsync("/v1/detect", new { input = Path.Combine(root, "nope/in.srt") });
            Assert.Equal(404, (int)read.StatusCode); Assert.Equal("storage-not-found", (string)(await read.Json())["code"]!);
            Assert.False(Directory.Exists(Path.Combine(root, "nope")));
        }
        finally { Directory.Delete(outside, true); }
    }
    [Fact]
    public void Open_flags_are_known_for_x64_and_arm64_only()
    {
        Assert.Equal((0x10000, 0x20000), LinuxOpenFlags.For(System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal((0x4000, 0x8000), LinuxOpenFlags.For(System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Null(LinuxOpenFlags.For(System.Runtime.InteropServices.Architecture.RiscV64));
        Assert.Contains("STORAGE_FILE_ROOTS", LinuxOpenFlags.Unsupported(System.Runtime.InteropServices.Architecture.RiscV64));
    }
}
