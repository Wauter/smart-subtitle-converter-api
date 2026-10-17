using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

// libse settings are process-global; tests share one process, so run them serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SubtitleEditConverterApi.Tests;

public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?>? settings;
    public ApiFactory() { }
    protected ApiFactory(Dictionary<string, string?> settings) => this.settings = settings;

    public static ApiFactory With(Dictionary<string, string?> settings) => new Configured(settings);
    private sealed class Configured(Dictionary<string, string?> s) : ApiFactory(s);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("PROFILES_DIR", settings?.GetValueOrDefault("PROFILES_DIR") ?? Path.Combine(Path.GetTempPath(), "se-api-none-" + Guid.NewGuid()));
        foreach (var (k, v) in settings ?? []) builder.UseSetting(k, v);
    }
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
