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
    /// <summary>The bucket must be allowlisted; the listed key prefix must lie inside S3_ALLOWED_PREFIXES when those are set.</summary>
    public void CheckedListing(string bucket, string keyPrefix)
    {
        if (bucket.Length is < 3 or > 63 || !bucket.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.') || !_opts.S3AllowedBuckets.Contains(bucket, StringComparer.Ordinal))
            throw StorageLocation.Rejected("S3 bucket is not in S3_ALLOWED_BUCKETS (or the bucket name is invalid).");
        if (keyPrefix.StartsWith('/') || keyPrefix.Contains('\\') || keyPrefix.Split('/').Any(s => s is "." or "..") || keyPrefix.Any(char.IsControl) || Encoding.UTF8.GetByteCount(keyPrefix) > 1024
            || (_opts.S3AllowedPrefixes.Length != 0 && !_opts.S3AllowedPrefixes.Any(p => keyPrefix.StartsWith(p, StringComparison.Ordinal))))
            throw StorageLocation.Rejected("The listed S3 key prefix must lie inside S3_ALLOWED_PREFIXES (see GET /v1/storage/locations), without dot segments or backslashes.");
    }
    /// <summary>
    /// One ListObjectsV2 page with delimiter '/': objects and common prefixes ("folders") directly under <paramref name="directory"/>
    /// whose names start with <paramref name="namePrefix"/>, in the bucket's key order. Folder marker objects (keys ending in '/') are skipped.
    /// </summary>
    public async Task<(List<StorageListing.Entry> Items, bool More, string? Token)> ListAsync(string bucket, string directory, string namePrefix, string? token, int pageSize, CancellationToken ct)
    {
        CheckedListing(bucket, directory + namePrefix);
        HiddenPaths.Check(directory);
        var response = await Attempt(token2 => _client.Value.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket, Prefix = directory + namePrefix, Delimiter = "/", MaxKeys = pageSize, ContinuationToken = token,
        }, token2), ct);
        var items = new List<StorageListing.Entry>();
        foreach (var o in response.S3Objects ?? [])
            if (o.Key.Length > directory.Length && !o.Key.EndsWith('/') && o.Key.StartsWith(directory, StringComparison.Ordinal) && o.Key[directory.Length] != '.')
                items.Add(new(o.Key[directory.Length..], false, o.Size, o.LastModified is { } m ? new DateTimeOffset(m.Kind == DateTimeKind.Local ? m.ToUniversalTime() : DateTime.SpecifyKind(m, DateTimeKind.Utc)) : null));
        foreach (var p in response.CommonPrefixes ?? [])
            if (p.Length > directory.Length && p.StartsWith(directory, StringComparison.Ordinal) && p[directory.Length] != '.') items.Add(new(p[directory.Length..], true, null, null));
        items.Sort((a, b) => StorageListing.Utf8Compare(a.Name, b.Name));
        var more = response.IsTruncated == true && !string.IsNullOrEmpty(response.NextContinuationToken);
        return (items, more, more ? response.NextContinuationToken : null);
    }
    public async Task<byte[]> ReadAsync(StorageLocation location, CancellationToken ct)
    {
        var (bucket, key) = Checked(location);
        HiddenPaths.Check(key); // before any network call
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
    /// Returns a short warning code (AWS error code, HTTP status or exception type) and a <c>problem=... fix=...</c>
    /// detail; never credentials. <paramref name="credentialSource"/> only names the source in hints.
    /// </summary>
    public async Task<(bool Ok, string? Warning, string Detail)> ProbeBucketAsync(string bucket, CancellationToken ct, string? credentialSource = null)
    {
        static string D(string problem, string fix) => $"problem={StorageDiagnostics.Q(problem)} fix={StorageDiagnostics.Q(fix)}";
        var timeout = TimeSpan.FromSeconds(_opts.IoTimeoutSeconds);
        var aws = string.IsNullOrWhiteSpace(_opts.S3Endpoint);
        var where = aws ? $"AWS S3 in S3_REGION={_opts.S3Region}" : $"S3_ENDPOINT={_opts.S3Endpoint}";
        var creds = credentialSource == null ? "the credentials" : $"the credentials (source={credentialSource})";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            // WaitAsync as a backstop in case credential resolution or the SDK ignores the token.
            var response = await _client.Value.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket }, deadline.Token).WaitAsync(timeout, ct);
            if (aws && response?.BucketRegion is { Length: > 0 } actual && actual != _opts.S3Region)
                return (false, "region-mismatch", $"bucket_region={actual} " + D($"bucket is in {actual} but S3_REGION={_opts.S3Region}", $"set S3_REGION={actual}"));
            return (true, null, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        { return (false, "Timeout", D($"no response from {where} within {_opts.IoTimeoutSeconds}s (IO_TIMEOUT_SECONDS)", "check S3_ENDPOINT/S3_REGION, DNS, proxy and firewall egress to the endpoint; raise IO_TIMEOUT_SECONDS only for slow links")); }
        catch (AmazonS3Exception ex)
        {
            var status = (int)ex.StatusCode;
            var code = string.IsNullOrWhiteSpace(ex.ErrorCode) ? (status > 0 ? "HTTP" + status : (ex.InnerException ?? ex).GetType().Name) : ex.ErrorCode;
            var region = BucketRegion(ex);
            var regionPart = region != null ? $" bucket_region={region}" : "";
            var detail = status switch
            {
                301 or 307 or 400 when region != null && region != _opts.S3Region => D($"bucket is in another region ({region}) than S3_REGION={_opts.S3Region}", $"set S3_REGION={region}"),
                301 or 307 => D($"bucket is in another region than S3_REGION={_opts.S3Region}, or the endpoint/path style is wrong", aws ? "set S3_REGION to the bucket's region (aws s3api get-bucket-location --bucket " + bucket + ")" : "check S3_ENDPOINT and S3_FORCE_PATH_STYLE=true"),
                400 => D($"bad request from {where}: bucket may be in another region than S3_REGION={_opts.S3Region}, or the endpoint/path style is wrong", aws ? "check S3_REGION against the bucket's region" : "check S3_ENDPOINT, S3_REGION and S3_FORCE_PATH_STYLE"),
                403 when code is "InvalidAccessKeyId" => D($"{creds} use an access key id that {where} does not know", "check AWS_ACCESS_KEY_ID; keys for MinIO/another provider do not work on AWS and vice versa"),
                403 when code is "SignatureDoesNotMatch" => D($"{creds} have a wrong secret key", "check AWS_SECRET_ACCESS_KEY for typos, quotes, whitespace or a trailing \\r"),
                403 when code is "InvalidToken" or "ExpiredToken" => D($"{creds} have an invalid or expired session token", "refresh AWS_SESSION_TOKEN together with the key pair, or use a role"),
                403 => D($"{creds} were rejected or bucket metadata (s3:ListBucket on arn:aws:s3:::{bucket}) is not allowed; object get/put may still work",
                    $"grant s3:ListBucket on arn:aws:s3:::{bucket} (see {StorageDiagnostics.PermissionsDoc}), check the bucket policy, and check the credentials and clock"),
                404 => D($"bucket not found at this endpoint/region ({where})", "check the bucket name in S3_ALLOWED_BUCKETS and S3_ENDPOINT; create the bucket outside this API"),
                _ => D($"S3 answered HTTP {status}", "check endpoint, region, credentials and network"),
            };
            return (false, code, $"status={status}{regionPart} {detail}");
        }
        catch (Amazon.Runtime.AmazonClientException ex) // no credentials in the chain, DNS/connect failures wrapped by the SDK
        {
            var noCredentials = ex.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase);
            return (false, noCredentials ? "NoCredentials" : ex.GetType().Name, noCredentials
                ? D("no AWS credentials found in the default chain (environment, shared config, task/instance role)", "set AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY, AWS_PROFILE with a mounted credentials file, or attach an IAM role")
                : D($"S3 client error talking to {where}", "check S3_ENDPOINT (scheme://host:port), DNS and network"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { return (false, ex.GetType().Name, D($"could not reach {where}", "check S3_ENDPOINT, DNS, TLS, proxy and firewall egress")); }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
        { return (false, "client-config", D($"the S3 client could not be created or used ({ex.GetType().Name})", "check S3_ENDPOINT is scheme://host[:port] and S3_REGION is a region code")); }
    }
    /// <summary>The bucket's real region from x-amz-bucket-region (SDK Region) or an AuthorizationHeaderMalformed message.</summary>
    /// <summary>The bucket's real region from the x-amz-bucket-region response header, or an AuthorizationHeaderMalformed message.</summary>
    public static string? BucketRegion(AmazonS3Exception ex)
    {
        for (Exception? inner = ex.InnerException; inner != null; inner = inner.InnerException)
            if (inner is Amazon.Runtime.Internal.HttpErrorResponseException { Response: { } response } && response.IsHeaderPresent("x-amz-bucket-region")
                && response.GetHeaderValue("x-amz-bucket-region") is { Length: > 0 } header)
                return header.Trim();
        var m = System.Text.RegularExpressions.Regex.Match(ex.Message ?? "", "expecting '([a-z0-9-]+)'");
        return m.Success ? m.Groups[1].Value : null;
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
