using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

// libse settings are process-global; tests share one process, so run them serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SmartSubtitleConverterApi.Tests;

public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?>? settings;
    private readonly Action<IServiceCollection>? configure;
    public ApiFactory() { }
    protected ApiFactory(Dictionary<string, string?> settings, Action<IServiceCollection>? configure = null) { this.settings = settings; this.configure = configure; }

    public static ApiFactory With(Dictionary<string, string?> settings, Action<IServiceCollection>? configure = null) => new Configured(settings, configure);
    private sealed class Configured(Dictionary<string, string?> s, Action<IServiceCollection>? c) : ApiFactory(s, c);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("PROFILES_DIR", settings?.GetValueOrDefault("PROFILES_DIR") ?? Path.Combine(Path.GetTempPath(), "se-api-none-" + Guid.NewGuid()));
        // Auth is on by default (H1); tests that don't configure keys run with it explicitly off.
        if (settings == null || !(settings.ContainsKey("API_KEYS") || settings.ContainsKey("API_KEYS_FILE") || settings.ContainsKey("API_AUTH_DISABLED")))
            builder.UseSetting("API_AUTH_DISABLED", "true");
        foreach (var (k, v) in settings ?? []) builder.UseSetting(k, v);
        // Never let the stuck-conversion exit (H8) end the test process.
        builder.ConfigureServices(s => { s.RemoveAll<SmartSubtitleConverterApi.Conversion.ProcessExit>(); s.AddSingleton<SmartSubtitleConverterApi.Conversion.ProcessExit>(new RecordingExit()); });
        if (configure != null) builder.ConfigureServices(configure);
    }
}

/// <summary>Records exit requests instead of ending the process.</summary>
public sealed class RecordingExit : SmartSubtitleConverterApi.Conversion.ProcessExit
{
    public readonly TaskCompletionSource<(int Code, string Reason)> Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override void Exit(int code, string reason) => Exited.TrySetResult((code, reason));
}

public static class Samples
{
    public const string Srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\nHello <i>world</i>.\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nSecond line\r\n";

    public const string Ttml = """
        <?xml version="1.0" encoding="utf-8"?>
        <tt xmlns="http://www.w3.org/ns/ttml" xml:lang="en">
          <body><div>
            <p begin="00:00:01.000" end="00:00:02.000">One</p>
            <p begin="00:00:03.000" end="00:00:04.000">Two</p>
          </div></body>
        </tt>
        """;

    /// <summary>
    /// Frame-based formats that record no rate need source.fps or target.fps (frame-rate-required). Tests that aren't
    /// about frame rates get source.fps 23.976 (libse's default, which these tests were written against) unless the
    /// profile already sets a rate or the target is EBU STL (its header sets the rate; default 25).
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject WithFps(object? profile, string? target = null)
    {
        var j = profile switch
        {
            null => new System.Text.Json.Nodes.JsonObject(),
            string s => (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(s)!,
            _ => (System.Text.Json.Nodes.JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(profile)!,
        };
        if (j["source"]?["fps"] != null || j["target"]?["fps"] != null || target is "ebustl" or "stl" or "ebu") return j;
        if (j["source"] is System.Text.Json.Nodes.JsonObject src) src["fps"] = 23.976;
        else j["source"] = new System.Text.Json.Nodes.JsonObject { ["fps"] = 23.976 };
        return j;
    }

    public static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (n, b) in entries)
            {
                using var s = z.CreateEntry(n).Open();
                s.Write(b);
            }
        }
        return ms.ToArray();
    }

    public static MultipartFormDataContent Form(byte[] file, string fileName = "movie.srt", params (string Key, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        var fc = new ByteArrayContent(file);
        fc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fc, "file", fileName);
        foreach (var (k, v) in fields) form.Add(new StringContent(v), k);
        return form;
    }

    public static MultipartFormDataContent Form(string text, string fileName = "movie.srt", params (string Key, string Value)[] fields) =>
        Form(Encoding.UTF8.GetBytes(text), fileName, fields);

    public static string Header(this HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(",", v)
        : r.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : "";

    public static async Task<System.Text.Json.Nodes.JsonObject> Json(this HttpResponseMessage r) =>
        (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
}
