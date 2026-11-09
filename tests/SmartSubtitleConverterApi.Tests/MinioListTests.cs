using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartSubtitleConverterApi.Infrastructure;
using SmartSubtitleConverterApi.Storage;
namespace SmartSubtitleConverterApi.Tests;

/// Browse and read by location against a real, isolated MinIO (MINIO_BIN; required in CI): ListObjectsV2 continuation, prefixes, folders.
public class MinioListTests
{
    [MinioFact]
    public async Task Real_minio_listing_walks_every_key_once_and_reads_by_location()
    {
        var root = Path.Combine(Path.GetTempPath(), "minio-list-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var endpoint = $"http://127.0.0.1:{port}";
        var user = "test-" + Guid.NewGuid().ToString("N"); var password = Guid.NewGuid().ToString("N"); // throwaway, loopback only
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("MINIO_BIN")!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "server", Path.Combine(root, "objects"), "--address", $"127.0.0.1:{port}", "--console-address", "127.0.0.1:0" }) start.ArgumentList.Add(arg);
        start.Environment["MINIO_ROOT_USER"] = user; start.Environment["MINIO_ROOT_PASSWORD"] = password;
        using var process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { }; process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            using var health = new HttpClient(); var ready = false;
            for (var i = 0; i < 100 && !ready; i++)
            {
                try { ready = (await health.GetAsync(endpoint + "/minio/health/ready")).IsSuccessStatusCode; } catch (HttpRequestException) { }
                if (!ready) await Task.Delay(100);
            }
            Assert.True(ready, "MinIO did not become ready.");
            var opts = new ApiOptions { S3Endpoint = endpoint, S3Region = "us-east-1", S3ForcePathStyle = true, S3AllowedBuckets = ["subs"], S3AllowedPrefixes = ["in/"], StorageListEnabled = true };
            var credentials = new BasicAWSCredentials(user, password);
            using var client = new AmazonS3Client(credentials, S3Storage.Configuration(opts));
            await client.PutBucketAsync(new PutBucketRequest { BucketName = "subs" });
            var expected = new List<string>();
            foreach (var i in Enumerable.Range(0, 420)) { var k = $"in/show_{i:000}.srt"; await client.PutObjectAsync(new PutObjectRequest { BucketName = "subs", Key = k, ContentBody = Samples.Srt, UseChunkEncoding = false }); expected.Add($"show_{i:000}.srt"); }
            await client.PutObjectAsync(new PutObjectRequest { BucketName = "subs", Key = "in/season1/ep1.srt", ContentBody = Samples.Srt, UseChunkEncoding = false }); expected.Add("season1/");
            await client.PutObjectAsync(new PutObjectRequest { BucketName = "subs", Key = "private/secret.srt", ContentBody = "secret", UseChunkEncoding = false });
            expected.Sort(StorageListing.Utf8Order);
            using var factory = ApiFactory.With(new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_ALLOWED_PREFIXES"] = "in/", ["S3_ENDPOINT"] = endpoint, ["S3_FORCE_PATH_STYLE"] = "true", ["STORAGE_LIST_ENABLED"] = "true" },
                services => { services.RemoveAll<S3Storage>(); services.AddSingleton(new S3Storage(opts, new AmazonS3Client(credentials, S3Storage.Configuration(opts)))); });
            var c = factory.CreateClient();
            var names = new List<string>(); string? cursor = null; var pages = 0;
            do
            {
                var j = await (await c.GetAsync("/v1/storage/list?location=" + Uri.EscapeDataString("s3://subs/in/") + (cursor != null ? "&cursor=" + cursor : ""))).Json();
                names.AddRange(j["items"]!.AsArray().Select(i => (string)i!["name"]!)); pages++;
                cursor = (bool)j["truncated"]! ? (string)j["next"]! : null;
                if (pages == 1) await client.DeleteObjectAsync("subs", "in/show_399.srt"); // removed before it was returned
            } while (cursor != null);
            Assert.Equal(expected.Where(n => n != "show_399.srt"), names);
            Assert.Equal(3, pages);
            var file = await c.GetAsync("/v1/storage/file?location=" + Uri.EscapeDataString("s3://subs/in/season1/ep1.srt"));
            Assert.Equal(Samples.Srt, await file.Content.ReadAsStringAsync());
            var denied = await c.GetAsync("/v1/storage/file?location=" + Uri.EscapeDataString("s3://subs/private/secret.srt"));
            Assert.Equal(422, (int)denied.StatusCode);
            var root2 = await c.GetAsync("/v1/storage/list?location=" + Uri.EscapeDataString("s3://subs/"));
            Assert.Equal(422, (int)root2.StatusCode);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            Directory.Delete(root, true);
        }
    }
}
