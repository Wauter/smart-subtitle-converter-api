using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using System.Net;
using SubtitleEditConverterApi.Infrastructure;
using SubtitleEditConverterApi.Storage;
namespace SubtitleEditConverterApi.Tests;
public class S3StorageTests
{
    private sealed class Failing(HttpStatusCode status) : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://unused.invalid", AuthenticationRegion = "us-east-1" })
    {
        public int Calls;
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new AmazonS3Exception("test failure") { StatusCode = status }; }
    }
    [Fact]
    public async Task Allowlists_and_exact_prefixes_checked_before_network()
    {
        using var fake = new Failing(HttpStatusCode.Forbidden);
        using var s3 = new S3Storage(new() { S3AllowedBuckets = ["subs"], S3AllowedPrefixes = ["in/"] }, fake);
        foreach (var uri in new[] { "s3://other/in/a.srt", "s3://subs/inside/a.srt", "s3://subs/in/../a.srt", "s3://subs/in/a.srt?key=bad" })
            Assert.Equal(422, (await Assert.ThrowsAsync<ApiException>(() => s3.ReadAsync(StorageLocation.Parse(uri), default))).Status);
        Assert.Equal(0, fake.Calls);
        Assert.Equal(("subs", "in/a b.srt"), s3.Checked(StorageLocation.Parse("s3://subs/in/a%20b.srt")));
        using var off = new S3Storage(new(), fake);
        await Assert.ThrowsAsync<ApiException>(() => off.ReadAsync(StorageLocation.Parse("s3://subs/in/a.srt"), default));
    }
    [Theory][InlineData(403, 1)][InlineData(404, 1)][InlineData(503, 2)]
    public async Task Only_transient_s3_errors_are_retried(int status, int count)
    {
        using var fake = new Failing((HttpStatusCode)status);
        using var s3 = new S3Storage(new() { S3AllowedBuckets = ["subs"], IoRetries = 1 }, fake);
        var error = await Assert.ThrowsAsync<ApiException>(() => s3.ReadAsync(StorageLocation.Parse("s3://subs/a.srt"), default));
        Assert.Equal(count, fake.Calls);
        Assert.Equal(status == 404 ? 404 : 502, error.Status);
    }
}
