using System.Net;
using System.Text;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Storage;

public sealed class S3Storage : IStorage, IDisposable
{
    private readonly ApiOptions _opts;
    private readonly Lazy<IAmazonS3> _client;
    public S3Storage(ApiOptions opts) { _opts = opts; _client = new(() => CreateClient(opts)); }
    public S3Storage(ApiOptions opts, IAmazonS3 client) { _opts = opts; _client = new(() => client); }
    public static AmazonS3Config Configuration(ApiOptions opts)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = opts.S3ForcePathStyle,
            MaxErrorRetry = 0, // one owner for retries and the entire attempt deadline (including reading streams)
            Timeout = TimeSpan.FromSeconds(opts.IoTimeoutSeconds),
        };
        if (string.IsNullOrWhiteSpace(opts.S3Endpoint)) config.RegionEndpoint = RegionEndpoint.GetBySystemName(opts.S3Region);
        else { config.ServiceURL = opts.S3Endpoint; config.AuthenticationRegion = opts.S3Region; }
        return config;
    }
    private static IAmazonS3 CreateClient(ApiOptions opts) => new AmazonS3Client(Configuration(opts)); // default SDK credential chain, never request credentials
    public (string Bucket, string Key) Checked(StorageLocation location)
    {
        var uri = location.Uri!;
        var authorityEnd = location.Value.IndexOf('/', "s3://".Length);
        var rawBucket = location.Value["s3://".Length..authorityEnd];
        if (uri.Query.Length != 0 || !uri.IsDefaultPort || rawBucket != uri.Host || uri.Host.Length is < 3 or > 63
            || !uri.Host.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.')
            || !_opts.S3AllowedBuckets.Contains(uri.Host, StringComparer.Ordinal))
            throw StorageLocation.Rejected("S3 bucket is not in S3_ALLOWED_BUCKETS (or the URI has a query, port or invalid bucket name).");
        // Preserve the object key instead of Uri's normalised AbsolutePath.
        var key = Uri.UnescapeDataString(location.Value[(authorityEnd + 1)..]);
        if (key.Length == 0 || key.EndsWith('/') || key.Contains('\\') || key.Split('/').Any(s => s is "." or "..") || key.Any(char.IsControl) || Encoding.UTF8.GetByteCount(key) > 1024
            || (_opts.S3AllowedPrefixes.Length != 0 && !_opts.S3AllowedPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal))))
            throw StorageLocation.Rejected("S3 key must name one object inside S3_ALLOWED_PREFIXES, without dot segments or backslashes (maximum 1024 UTF-8 bytes).");
        return (uri.Host, key);
    }
    public async Task<byte[]> ReadAsync(StorageLocation location, CancellationToken ct)
    {
        var (bucket, key) = Checked(location);
        return await Attempt(async token =>
        {
            using var response = await _client.Value.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, token);
            if (response.ContentLength > _opts.MaxUploadBytes) throw Problems.TooLarge(_opts);
            return await StorageRouter.ReadCapped(response.ResponseStream, _opts.MaxUploadBytes, token);
        }, ct);
    }
    public async Task WriteAsync(StorageLocation location, byte[] bytes, string contentType, CancellationToken ct, bool replace = false)
    {
        var (bucket, key) = Checked(location);
        if (bytes.LongLength > _opts.MaxUploadBytes) throw Problems.TooLarge(_opts);
        try { await Attempt(async token =>
        {
            using var stream = new MemoryStream(bytes, writable: false);
            await _client.Value.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = key, InputStream = stream, ContentType = contentType,
                UseChunkEncoding = false, AutoCloseStream = false, IfNoneMatch = replace ? null : "*",
            }, token);
            return true;
        }, ct); }
        catch (ApiException ex) when (!replace && ex.Extensions.TryGetValue("upstreamStatus", out var status) && status is int code && code is 412 or 409)
        { throw StorageLocation.OutputExists(); }
        // a single PUT; no staging, multipart upload, copy or pretend rename (the only delete is a stale {output}.json sidecar)
    }
    public async Task<bool> ExistsAsync(StorageLocation location, CancellationToken ct)
    {
        var (bucket, key) = Checked(location);
        try
        {
            return await Attempt(async token => { await _client.Value.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }, token); return true; }, ct);
        }
        catch (ApiException ex) when (ex.Code == "storage-not-found") { return false; }
    }
    public async Task<bool> DeleteAsync(StorageLocation location, CancellationToken ct)
    {
        var (bucket, key) = Checked(location);
        if (!await ExistsAsync(location, ct)) return false;
        await Attempt(async token => { await _client.Value.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, token); return true; }, ct);
        return true;
    }
    /// <summary>
    /// Startup probe: one HeadBucket (metadata only, never creates or writes objects), bounded by IO_TIMEOUT_SECONDS.
    /// Returns a short warning code (AWS error code, HTTP status or exception type) and a detail hint; never credentials.
    /// </summary>
    public async Task<(bool Ok, string? Warning, string Detail)> ProbeBucketAsync(string bucket, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(_opts.IoTimeoutSeconds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            // WaitAsync as a backstop in case credential resolution or the SDK ignores the token.
            await _client.Value.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket }, deadline.Token).WaitAsync(timeout, ct);
            return (true, null, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        { return (false, "Timeout", $"detail=\"no response within {_opts.IoTimeoutSeconds}s (IO_TIMEOUT_SECONDS); check endpoint and network\""); }
        catch (AmazonS3Exception ex)
        {
            var status = (int)ex.StatusCode;
            var code = string.IsNullOrWhiteSpace(ex.ErrorCode) ? (status > 0 ? "HTTP" + status : (ex.InnerException ?? ex).GetType().Name) : ex.ErrorCode;
            var hint = status switch
            {
                403 => "credentials were rejected or bucket metadata (s3:ListBucket) is not allowed; object get/put may still work",
                404 => "bucket not found at this endpoint/region",
                301 or 307 or 400 => "bucket may be in another region than S3_REGION, or the endpoint/path style is wrong",
                _ => "check endpoint, region, credentials and network",
            };
            return (false, code, $"status={status} detail=\"{hint}\"");
        }
        catch (Amazon.Runtime.AmazonClientException ex) // no credentials in the chain, DNS/connect failures wrapped by the SDK
        {
            var noCredentials = ex.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase);
            return (false, noCredentials ? "NoCredentials" : ex.GetType().Name, noCredentials
                ? "detail=\"no AWS credentials found in the default chain (environment, shared config, task/instance role)\""
                : "detail=\"S3 client error; check endpoint and network\"");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { return (false, ex.GetType().Name, "detail=\"could not reach the S3 endpoint; check endpoint, DNS and network\""); }
    }
    private static bool Transient(Exception ex) => ex is AmazonS3Exception s && (int)s.StatusCode is 408 or 429 or 500 or 502 or 503 or 504
        || (ex is AmazonS3Exception { StatusCode: 0, InnerException: { } inner } && Transient(inner))
        || ex is HttpRequestException { HttpRequestError: not HttpRequestError.SecureConnectionError } or IOException or OperationCanceledException;
    private async Task<T> Attempt<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(_opts.IoTimeoutSeconds));
            try { return await work(deadline.Token); }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            { throw new ApiException(404, "storage-not-found", "S3 object or bucket was not found (404).", "Check the configured endpoint, bucket and key."); }
            catch (Exception ex) when (!ct.IsCancellationRequested && Transient(ex) && attempt < _opts.IoRetries)
            { await Task.Delay(250 * (1 << attempt) + Random.Shared.Next(100), ct); }
            catch (Exception ex) when (ex is Amazon.Runtime.AmazonClientException or AmazonS3Exception or HttpRequestException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                var status = ex is AmazonS3Exception s ? (int)s.StatusCode : 0;
                var problem = new ApiException(502, "storage-io-failed", $"S3 transfer failed after {attempt + 1} attempt(s)" + (status > 0 ? $" (HTTP {status})." : "."),
                    "Check the server credential chain, endpoint, region, bucket/prefix permissions and availability. 403/404 are not retried. A timed-out PUT may already have reached storage.");
                if (status > 0) problem.Extensions["upstreamStatus"] = status;
                throw problem;
            }
        }
    }
    public void Dispose() { if (_client.IsValueCreated) _client.Value.Dispose(); }
}
