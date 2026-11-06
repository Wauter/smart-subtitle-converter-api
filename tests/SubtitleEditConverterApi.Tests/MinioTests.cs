using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubtitleEditConverterApi.Infrastructure;
using SubtitleEditConverterApi.Storage;

namespace SubtitleEditConverterApi.Tests;

public sealed class MinioFactAttribute : FactAttribute
{
    public MinioFactAttribute() { if (!File.Exists(Environment.GetEnvironmentVariable("MINIO_BIN"))) Skip = "Set MINIO_BIN to a built MinIO server for the isolated integration test (required in CI)."; }
}
public class MinioTests
{
    private sealed class HttpsFake : HttpMessageHandler
    {
        public byte[]? Written;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.Method == HttpMethod.Put) Written = await req.Content!.ReadAsByteArrayAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(Samples.Srt) };
        }
    }
    [MinioFact]
    public async Task Real_minio_s3_file_https_hybrids_sidecars_and_overwrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "minio-integration-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var endpoint = $"http://127.0.0.1:{port}";
        // Generated throwaway credentials, valid only for this isolated loopback MinIO process.
        var user = "test-" + Guid.NewGuid().ToString("N"); var password = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("MINIO_BIN")!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "server", Path.Combine(root, "objects"), "--address", $"127.0.0.1:{port}", "--console-address", "127.0.0.1:0" }) start.ArgumentList.Add(arg);
        start.Environment["MINIO_ROOT_USER"] = user; start.Environment["MINIO_ROOT_PASSWORD"] = password;
        using var process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { }; process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            using var health = new HttpClient(); var ready = false;
            for (var i = 0; i < 100; i++)
            {
                if (process.HasExited) throw new Exception("The isolated MinIO test process exited before readiness.");
                try { if ((await health.GetAsync(endpoint + "/minio/health/ready")).IsSuccessStatusCode) { ready = true; break; } } catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            Assert.True(ready, "MinIO did not become ready.");
            var opts = new ApiOptions { S3Endpoint = endpoint, S3Region = "us-east-1", S3ForcePathStyle = true, S3AllowedBuckets = ["subs"], S3AllowedPrefixes = ["in/", "out/"] };
            var credentials = new BasicAWSCredentials(user, password);
            using var client = new AmazonS3Client(credentials, S3Storage.Configuration(opts));
            await client.PutBucketAsync(new PutBucketRequest { BucketName = "subs" });
            await client.PutObjectAsync(new PutObjectRequest { BucketName = "subs", Key = "in/show.srt", ContentBody = Samples.Srt, UseChunkEncoding = false });
            var https = new HttpsFake();
            using var factory = ApiFactory.With(new() { ["STORAGE_FILE_ROOTS"] = root, ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_ALLOWED_PREFIXES"] = "in/,out/", ["S3_ENDPOINT"] = endpoint, ["S3_FORCE_PATH_STYLE"] = "true", ["HTTP_ALLOWED_HOSTS"] = "mam.example.com" }, services =>
            {
                services.RemoveAll<S3Storage>(); services.AddSingleton(new S3Storage(opts, new AmazonS3Client(credentials, S3Storage.Configuration(opts))));
                services.RemoveAll<HttpStorage>(); services.AddSingleton(new HttpStorage(new() { HttpAllowedHosts = ["mam.example.com"] }, new HttpClient(https)));
            });
            var c = factory.CreateClient();
            var output = Path.Combine(root, "show.vtt");
            var a = await c.PostAsJsonAsync("/v1/convert/json", new { input = "s3://subs/in/show.srt", output, target = "vtt", sidecar = true });
            Assert.Equal(200, (int)a.StatusCode); Assert.Contains("WEBVTT", File.ReadAllText(output)); Assert.True(File.Exists(output + ".json"));
            var b = await c.PostAsJsonAsync("/v1/convert/json", new { input = output, output = "s3://subs/out/show.srt", target = "srt", sidecar = true });
            Assert.Equal(200, (int)b.StatusCode);
            using (var result = await client.GetObjectAsync("subs", "out/show.srt")) { using var r = new StreamReader(result.ResponseStream); Assert.Contains("Hello", await r.ReadToEndAsync()); }
            using (var sidecar = await client.GetObjectAsync("subs", "out/show.srt.json")) { using var r = new StreamReader(sidecar.ResponseStream); var text = await r.ReadToEndAsync(); Assert.Contains("stats", text); Assert.DoesNotContain("Hello", text); }
            var binary = await c.PostAsJsonAsync("/v1/convert/json", new { input = output, output = "s3://subs/out/show.pac", target = "pac", sidecar = true });
            Assert.Equal(200, (int)binary.StatusCode);
            using (var sidecar = await client.GetObjectAsync("subs", "out/show.pac.json")) { using var r = new StreamReader(sidecar.ResponseStream); Assert.Contains("verification", await r.ReadToEndAsync()); }
            var h = await c.PostAsJsonAsync("/v1/convert/json", new { input = "https://mam.example.com/show.srt", output = "s3://subs/out/from-https.vtt", target = "vtt" });
            Assert.Equal(200, (int)h.StatusCode);
            var put = await c.PostAsJsonAsync("/v1/convert/json", new { input = "s3://subs/in/show.srt", output = "https://mam.example.com/show.vtt?signature=temporary", target = "vtt", sidecar = true });
            Assert.Equal(200, (int)put.StatusCode); Assert.Contains("sidecar-skipped", (await put.Json())["warnings"]!.ToJsonString()); Assert.NotNull(https.Written);
            var job = await (await c.GetAsync("/v1/jobs/" + put.Header("X-Job-Id"))).Json(); Assert.DoesNotContain("signature", job.ToJsonString());
            var again = await c.PostAsJsonAsync("/v1/convert/json", new { input = output, output = "s3://subs/out/show.srt", target = "srt" });
            Assert.Equal(200, (int)again.StatusCode); // S3 single PUT replaces the object as a whole.
            var absent = await c.PostAsJsonAsync("/v1/detect", new { input = "s3://subs/in/missing.srt" }); Assert.Equal(404, (int)absent.StatusCode);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            Directory.Delete(root, true);
        }
    }
}
