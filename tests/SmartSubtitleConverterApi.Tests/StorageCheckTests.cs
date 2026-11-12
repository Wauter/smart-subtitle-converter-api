using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using System.Net;
using Microsoft.Extensions.Logging;
using SmartSubtitleConverterApi.Infrastructure;
using SmartSubtitleConverterApi.Storage;
namespace SmartSubtitleConverterApi.Tests;
public sealed class StorageCheckTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "se-check-" + Guid.NewGuid());
    public StorageCheckTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    internal sealed class Head(Func<HeadBucketRequest, Task<HeadBucketResponse>> handler) : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://unused.invalid", AuthenticationRegion = "us-east-1" })
    {
        public List<string> Buckets = [];
        public int Writes;
        public override Task<HeadBucketResponse> HeadBucketAsync(HeadBucketRequest request, CancellationToken cancellationToken = default)
        { Buckets.Add(request.BucketName); return handler(request); }
        public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        { Writes++; throw new InvalidOperationException("startup check must not write"); }
    }
    internal sealed class Capture : ILogger<StorageCheck>
    {
        public List<(LogLevel Level, string Message)> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }
    /// <summary>Runs the full check with no environment and fake static credentials; returns only the probe lines.</summary>
    private static async Task<Capture> Run(ApiOptions opts, IAmazonS3 client)
    {
        var all = await RunAll(opts, client);
        var probes = new Capture();
        probes.Lines.AddRange(all.Lines.Where(l => (l.Message.StartsWith("storage.s3 bucket=") || l.Message.StartsWith("storage.file ")) ));
        return probes;
    }
    internal static async Task<Capture> RunAll(ApiOptions opts, IAmazonS3 client, Dictionary<string, string>? env = null, Func<CancellationToken, Task<AWSCredentials>>? creds = null)
    {
        var log = new Capture();
        using var s3 = new S3Storage(opts, client);
        await new StorageCheck(opts, s3, log)
        {
            Env = k => env != null && env.TryGetValue(k, out var v) ? v : null,
            ResolveCredentials = creds ?? (_ => Task.FromResult<AWSCredentials>(new BasicAWSCredentials("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMIK7MDENGbPxRfiCYEXAMPLEKEY"))),
        }.RunAsync(default);
        return log;
    }

    [Fact]
    public async Task Reachable_bucket_logs_ok_and_never_writes()
    {
        using var fake = new Head(_ => Task.FromResult(new HeadBucketResponse { HttpStatusCode = HttpStatusCode.OK }));
        var log = await Run(new() { S3AllowedBuckets = ["subs", "tmp"] }, fake);
        Assert.Equal(["subs", "tmp"], fake.Buckets);
        Assert.Equal(0, fake.Writes);
        Assert.Equal(["storage.s3 bucket=subs ok", "storage.s3 bucket=tmp ok"], log.Lines.Select(l => l.Message));
        Assert.All(log.Lines, l => Assert.Equal(LogLevel.Information, l.Level));
    }

    [Theory]
    [InlineData(403, "AccessDenied", "object get/put may still work")]
    [InlineData(404, "NoSuchBucket", "bucket not found")]
    [InlineData(301, "PermanentRedirect", "another region")]
    [InlineData(403, null, "HTTP403")]
    public async Task Failing_bucket_logs_warning_with_code(int status, string? code, string expected)
    {
        using var fake = new Head(_ => throw new AmazonS3Exception("denied") { StatusCode = (HttpStatusCode)status, ErrorCode = code });
        var log = await Run(new() { S3AllowedBuckets = ["subs"] }, fake);
        var (level, message) = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.StartsWith($"storage.s3 bucket=subs warning={code ?? "HTTP" + status} status={status}", message);
        Assert.Contains(expected, message);
    }

    [Fact]
    public async Task Hung_bucket_times_out_with_warning()
    {
        using var fake = new Head(_ => new TaskCompletionSource<HeadBucketResponse>().Task); // ignores cancellation
        var log = await Run(new() { S3AllowedBuckets = ["slow"], IoTimeoutSeconds = 1 }, fake);
        Assert.StartsWith("storage.s3 bucket=slow warning=Timeout", Assert.Single(log.Lines).Message);
    }

    [Fact]
    public async Task Disabled_schemes_are_not_contacted()
    {
        using var fake = new Head(_ => throw new InvalidOperationException());
        var resolved = false;
        var log = await RunAll(new() { HttpAllowedHosts = ["example.com"] }, fake, creds: _ => { resolved = true; throw new InvalidOperationException(); });
        Assert.Empty(fake.Buckets);
        Assert.False(resolved);
        Assert.DoesNotContain(log.Lines, l => l.Message.StartsWith("storage.s3") || l.Message.StartsWith("storage.file"));
        Assert.Contains(log.Lines, l => l.Message == "storage.config schemes=https");
        Assert.Equal("storage.check done ok=0 warnings=0", log.Lines[^1].Message);
    }

    [Fact]
    public async Task Filesystem_roots_report_missing_file_and_ok()
    {
        if (!OperatingSystem.IsLinux()) return; // filesystem URI storage is Linux-only
        var missing = Path.Combine(root, "nope");
        var file = Path.Combine(root, "plain.txt"); File.WriteAllText(file, "x");
        using var fake = new Head(_ => throw new InvalidOperationException());
        var log = await Run(new() { StorageFileRoots = [root, missing, file] }, fake);
        Assert.Equal(3, log.Lines.Count);
        Assert.Equal((LogLevel.Information, $"storage.file root={root} ok writable=True"), log.Lines[0]);
        Assert.Equal(LogLevel.Warning, log.Lines[1].Level);
        Assert.StartsWith($"storage.file root={missing} warning=missing", log.Lines[1].Message);
        Assert.StartsWith($"storage.file root={file} warning=not-a-directory", log.Lines[2].Message);
        Assert.Equal(["plain.txt"], Directory.GetFiles(root).Select(Path.GetFileName)); // nothing written
    }

    [Fact]
    public async Task Symlinked_root_is_flagged()
    {
        if (!OperatingSystem.IsLinux()) return; // filesystem URI storage is Linux-only
        var target = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        var link = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(link, target);
        using var fake = new Head(_ => throw new InvalidOperationException());
        var log = await Run(new() { StorageFileRoots = [link] }, fake);
        Assert.StartsWith($"storage.file root={link} warning=symlink", Assert.Single(log.Lines).Message);
    }
}
