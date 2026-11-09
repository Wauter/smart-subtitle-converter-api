using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmartSubtitleConverterApi.Infrastructure;
using SmartSubtitleConverterApi.Storage;
namespace SmartSubtitleConverterApi.Tests;

public sealed class StorageDiagnosticsTests
{
    private const string KeyId = "AKIAIOSFODNN7EXAMPLE";
    private const string Secret = "wJalrXUtnFEMIK7MDENGbPxRfiCYEXAMPLEKEY";
    private const string Token = "IQoJb3JpZ2luX2VjEPT//////////wEaCXVzLWVhc3QtMSJHMEUCIQDtoken1234";

    private static ApiOptions Options(Dictionary<string, string> env)
        => ApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(env!).Build());
    private static List<StorageFinding> Inspect(Dictionary<string, string> env) => StorageDiagnostics.Inspect(Options(env), k => env.TryGetValue(k, out var v) ? v : null);
    private static StorageFinding Warning(List<StorageFinding> f, string code) => Assert.Single(f, x => x.Warning && x.Line.Contains($"warning={code} "));

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "AKI***MPLE")]
    [InlineData("wJalrXUtnFEMIK7MDENGbPxRfiCYEXAMPLEKEY", "wJa***EKEY")]
    [InlineData("ABCDEF78d6", "ABC***78d6")]
    [InlineData("short", "***")]
    [InlineData("123456789", "***")]
    [InlineData("", "***")]
    [InlineData(null, "<unset>")]
    public void Mask_keeps_first_three_and_last_four(string? value, string expected) => Assert.Equal(expected, StorageDiagnostics.Mask(value));

    [Fact]
    public void Summary_prints_values_and_masks_secrets_everywhere()
    {
        var env = new Dictionary<string, string>
        {
            ["S3_ALLOWED_BUCKETS"] = "subs,tmp", ["S3_REGION"] = "eu-west-1", ["AWS_ACCESS_KEY_ID"] = " " + KeyId,
            ["AWS_SECRET_ACCESS_KEY"] = "\"" + Secret + "\"", ["AWS_SESSION_TOKEN"] = Token,
        };
        var f = Inspect(env);
        var text = string.Join('\n', f.Select(x => x.Line));
        Assert.Contains("storage.config var=S3_ALLOWED_BUCKETS value=\"subs,tmp\" effective=\"subs,tmp\"", text);
        Assert.Contains("storage.config var=S3_REGION value=\"eu-west-1\" effective=\"eu-west-1\"", text);
        Assert.Contains("storage.config var=AWS_SECRET_ACCESS_KEY value=\"wJ***KEY\"", text.Replace("\"wJa***KEY\"", "x")); // masked with the quote as first char
        Assert.Contains("storage.config var=AWS_SESSION_TOKEN value=IQo***1234", text);
        Assert.Contains("warning=whitespace var=AWS_ACCESS_KEY_ID value= AK***MPLE", text);
        Assert.Contains("warning=quoted var=AWS_SECRET_ACCESS_KEY", text);
        Assert.Contains("fix=\"remove the quotes: AWS_SECRET_ACCESS_KEY=<value>\"", text);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain(KeyId, text);
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain(Secret[3..^4], text);
    }

    [Fact]
    public async Task Empty_allowlists_are_always_logged_as_unset()
    {
        using var fake = new StorageCheckTests.Head(_ => throw new InvalidOperationException());
        var lines = (await StorageCheckTests.RunAll(new(), fake)).Lines.Select(l => l.Message).ToList();
        Assert.Contains("storage.config var=S3_ALLOWED_BUCKETS value=<unset> effective=\"(unset — S3 off)\"", lines);
        Assert.Contains("storage.config var=STORAGE_FILE_ROOTS value=<unset> effective=\"(unset — file off)\"", lines);
        Assert.Contains("storage.config var=HTTP_ALLOWED_HOSTS value=<unset> effective=\"(unset — HTTPS off)\"", lines);
        Assert.Contains(lines, l => l.StartsWith("storage.config schemes=none") && l.Contains("env_file"));
        Assert.Equal("storage.check done ok=0 warnings=0", lines[^1]);
    }

    [Fact]
    public void Scrub_masks_configured_secrets_in_sdk_messages()
    {
        var env = new Dictionary<string, string> { ["AWS_SECRET_ACCESS_KEY"] = Secret, ["AWS_ACCESS_KEY_ID"] = KeyId };
        var scrubbed = StorageDiagnostics.Scrub($"line one\n  key {KeyId} secret {Secret}\r\nend", k => env.GetValueOrDefault(k));
        Assert.Equal("line one; key AKI***MPLE secret wJa***EKEY; end", scrubbed);
    }

    public static TheoryData<Dictionary<string, string>, string, string, string> Mistakes => new()
    {
        { new() { ["S3_ALLOWED_BUCKETS"] = "s3://subs/in" }, "bucket-has-scheme", "S3_ALLOWED_BUCKETS", "use S3_ALLOWED_BUCKETS=subs and S3_ALLOWED_PREFIXES=in/" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "subs/in/" }, "bucket-has-path", "S3_ALLOWED_BUCKETS", "S3_ALLOWED_PREFIXES=in/" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "arn:aws:s3:::subs" }, "bucket-is-arn", "S3_ALLOWED_BUCKETS", "use S3_ALLOWED_BUCKETS=subs" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "My_Subs" }, "invalid-bucket-name", "S3_ALLOWED_BUCKETS", "lowercase" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "ab" }, "invalid-bucket-name", "S3_ALLOWED_BUCKETS", "3-63 characters" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "192.168.1.1" }, "invalid-bucket-name", "S3_ALLOWED_BUCKETS", "IP address" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "\"subs\"" }, "quoted", "S3_ALLOWED_BUCKETS", "remove the quotes: S3_ALLOWED_BUCKETS=subs" },
        { new() { ["S3_ALLOWED_PREFIXES"] = "in/" }, "prefixes-without-buckets", "S3_ALLOWED_PREFIXES", "set S3_ALLOWED_BUCKETS" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_REGION"] = "eu-west-1", ["S3_ALLOWED_PREFIXES"] = "/in/" }, "prefix-leading-slash", "S3_ALLOWED_PREFIXES", "use S3_ALLOWED_PREFIXES=in/" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_REGION"] = "eu-west-1", ["S3_ALLOWED_PREFIXES"] = "in/*" }, "prefix-wildcard", "S3_ALLOWED_PREFIXES", "in/" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "subs" }, "region-missing", "S3_REGION", "set S3_REGION to the bucket's region" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["AWS_REGION"] = "eu-central-1" }, "region-missing", "S3_REGION", "this API reads S3_REGION: set S3_REGION=eu-central-1" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_REGION"] = "Europe (Ireland)" }, "region-unknown", "S3_REGION", "e.g. S3_REGION=eu-west-1" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_REGION"] = "eu-west-1 " }, "whitespace", "S3_REGION", "remove the spaces" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_ENDPOINT"] = "minio:9000", ["S3_FORCE_PATH_STYLE"] = "true" }, "endpoint-no-scheme", "S3_ENDPOINT", "S3_ENDPOINT=http://minio:9000" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_ENDPOINT"] = "http://minio:9000/subs", ["S3_FORCE_PATH_STYLE"] = "true" }, "endpoint-has-path", "S3_ENDPOINT", "S3_ENDPOINT=http://minio:9000 and keep 'subs' in S3_ALLOWED_BUCKETS" },
        { new() { ["IO_TIMEOUT_SECONDS"] = "0" }, "clamped", "IO_TIMEOUT_SECONDS", "between 1 and 300" },
        { new() { ["STORAGE_FILE_ROOTS"] = "/data/in,/data/out" }, "root-has-comma", "STORAGE_FILE_ROOTS", "STORAGE_FILE_ROOTS=/data/in:/data/out" },
        { new() { ["HTTP_ALLOWED_HOSTS"] = "https://cdn.example.com/subs" }, "host-not-bare", "HTTP_ALLOWED_HOSTS", "use HTTP_ALLOWED_HOSTS=cdn.example.com" },
        { new() { ["HTTP_ALLOWED_HOSTS"] = "cdn.example.com:443" }, "host-has-port", "HTTP_ALLOWED_HOSTS", "use HTTP_ALLOWED_HOSTS=cdn.example.com" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_REGION"] = "eu-west-1", ["AWS_ACCESS_KEY_ID"] = KeyId }, "credentials-incomplete", "AWS_SECRET_ACCESS_KEY", "set AWS_SECRET_ACCESS_KEY" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_REGION"] = "eu-west-1", ["AWS_SECRET_ACCESS_KEY"] = Secret }, "credentials-incomplete", "AWS_ACCESS_KEY_ID", "set AWS_ACCESS_KEY_ID" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_REGION"] = "eu-west-1", ["AWS_ACCESS_KEY_ID"] = "ASIAIOSFODNN7EXAMPLE", ["AWS_SECRET_ACCESS_KEY"] = Secret }, "session-token-missing", "AWS_SESSION_TOKEN", "set AWS_SESSION_TOKEN" },
        { new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_REGION"] = "eu-west-1", ["AWS_ACCESS_KEY_ID"] = KeyId, ["AWS_SECRET_ACCESS_KEY"] = Secret + "\r" }, "crlf", "AWS_SECRET_ACCESS_KEY", "LF line endings" },
        { new() { ["AWS_SHARED_CREDENTIALS_FILE"] = "/nonexistent/aws/credentials" }, "file-missing", "AWS_SHARED_CREDENTIALS_FILE", "mount the file" },
        { new() { ["AWS_CONFIG_FILE"] = "aws/config" }, "file-relative", "AWS_CONFIG_FILE", "absolute container path" },
    };

    [Theory, MemberData(nameof(Mistakes))]
    public void Each_mistake_names_var_value_problem_and_fix(Dictionary<string, string> env, string code, string var, string fix)
    {
        var w = Warning(Inspect(env), code);
        Assert.StartsWith($"storage.config warning={code} var={var} value=", w.Line);
        Assert.Contains(" problem=\"", w.Line);
        Assert.Contains(" fix=\"", w.Line);
        Assert.Contains(fix, w.Line);
        Assert.DoesNotContain(Secret, w.Line);
    }

    [Fact]
    public void Valid_aws_and_minio_configs_have_no_warnings()
    {
        Assert.DoesNotContain(Inspect(new() { ["S3_ALLOWED_BUCKETS"] = "subs,tmp", ["S3_REGION"] = "eu-west-1", ["S3_ALLOWED_PREFIXES"] = "in/,out/",
            ["AWS_ACCESS_KEY_ID"] = KeyId, ["AWS_SECRET_ACCESS_KEY"] = Secret, ["IO_TIMEOUT_SECONDS"] = "20" }), x => x.Warning);
        Assert.DoesNotContain(Inspect(new() { ["S3_ALLOWED_BUCKETS"] = "subs", ["S3_ENDPOINT"] = "http://minio:9000", ["S3_FORCE_PATH_STYLE"] = "true",
            ["AWS_ACCESS_KEY_ID"] = "minioadmin", ["AWS_SECRET_ACCESS_KEY"] = "minio-secret-value" }), x => x.Warning);
    }

    [Theory]
    [InlineData("IO_TIMEOUT_SECONDS", "10s", "IO_TIMEOUT_SECONDS=\"10s\" is not a whole number. Fix: set IO_TIMEOUT_SECONDS=10")]
    [InlineData("S3_FORCE_PATH_STYLE", "yes", "S3_FORCE_PATH_STYLE=\"yes\" is not true or false. Fix: set S3_FORCE_PATH_STYLE=true")]
    [InlineData("STORAGE_FILE_ROOTS", "[/data]", "STORAGE_FILE_ROOTS=\"[/data]\" starts with '[' but is not a JSON array")]
    public void Malformed_typed_values_still_fail_startup_with_a_clear_message(string key, string value, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Options(new() { [key] = value }));
        Assert.Contains(expected, ex.Message);
    }

    // Credential source reporting with a fake resolver (no AWS, no metadata endpoint).
    private static async Task<List<string>> RunWith(Func<CancellationToken, Task<AWSCredentials>> creds, Dictionary<string, string>? env = null, int timeout = 10)
    {
        using var fake = new StorageCheckTests.Head(_ => Task.FromResult(new HeadBucketResponse { HttpStatusCode = HttpStatusCode.OK }));
        var log = await StorageCheckTests.RunAll(new() { S3AllowedBuckets = ["subs"], S3Region = "eu-west-1", IoTimeoutSeconds = timeout }, fake, env ?? new() { ["S3_REGION"] = "eu-west-1", ["S3_ALLOWED_BUCKETS"] = "subs" }, creds);
        return log.Lines.Select(l => l.Message).ToList();
    }

    [Fact]
    public async Task Reports_profile_source_with_masked_key_and_summary()
    {
        var lines = await RunWith(_ => Task.FromResult<AWSCredentials>(new SessionAWSCredentials("ASIAIOSFODNN7EXAMPLE", Secret, Token)),
            new() { ["S3_REGION"] = "eu-west-1", ["S3_ALLOWED_BUCKETS"] = "subs", ["AWS_PROFILE"] = "subs-rw", ["AWS_SHARED_CREDENTIALS_FILE"] = "/run/secrets/aws" });
        var cred = Assert.Single(lines, l => l.StartsWith("storage.s3 credentials"));
        Assert.Equal("storage.s3 credentials ok source=profile profile=\"subs-rw\" credentials_file=\"/run/secrets/aws\" config_file=\"~/.aws/config\" access_key_id=ASI***MPLE session_token=yes type=SessionAWSCredentials", cred);
        Assert.Contains("storage.s3 bucket=subs ok", lines);
        Assert.Equal("storage.check done ok=2 warnings=1", lines[^1]); // the missing credentials file is the warning
    }

    [Fact]
    public async Task Reports_env_source_through_the_real_sdk_chain()
    {
        // Real DefaultAWSCredentialsIdentityResolver: environment credentials resolve locally, without any network.
        string?[] saved = [Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"), Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")];
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", KeyId);
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", Secret);
        try
        {
            var opts = new ApiOptions { S3AllowedBuckets = ["subs"], S3Region = "eu-west-1" };
            using var fake = new StorageCheckTests.Head(_ => Task.FromResult(new HeadBucketResponse { HttpStatusCode = HttpStatusCode.OK }));
            using var s3 = new S3Storage(opts, fake);
            var (source, ok, line) = await new StorageCheck(opts, s3, new StorageCheckTests.Capture()).Credentials(default);
            Assert.True(ok);
            Assert.Equal("env", source);
            Assert.Equal("storage.s3 credentials ok source=env access_key_id=AKI***MPLE session_token=no type=EnvironmentVariablesAWSCredentials", line);
            Assert.Equal("env", StorageDiagnostics.Classify(new EnvironmentVariablesAWSCredentials()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", saved[0]);
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", saved[1]);
        }
    }

    [Fact]
    public void Classifies_sdk_credential_types()
    {
        Assert.Equal("anonymous", StorageDiagnostics.Classify(new AnonymousAWSCredentials()));
        Assert.Equal("none", StorageDiagnostics.Classify(null));
        Assert.Equal("profile", StorageDiagnostics.Classify(new BasicAWSCredentials(KeyId, Secret)));
        Assert.Equal("profile", StorageDiagnostics.Classify(new SessionAWSCredentials(KeyId, Secret, Token)));
    }

    [Fact]
    public async Task Anonymous_credentials_warn()
    {
        var lines = await RunWith(_ => Task.FromResult<AWSCredentials>(new AnonymousAWSCredentials()));
        Assert.Contains(lines, l => l.StartsWith("storage.s3 credentials warning=anonymous var=AWS_ACCESS_KEY_ID"));
    }

    [Fact]
    public async Task Missing_credentials_are_a_loud_warning_without_secrets()
    {
        var env = new Dictionary<string, string> { ["S3_REGION"] = "eu-west-1", ["S3_ALLOWED_BUCKETS"] = "subs", ["AWS_SECRET_ACCESS_KEY"] = Secret };
        var lines = await RunWith(_ => throw new AmazonClientException($"Failed to resolve AWS credentials.\nException 1 of 2: bad secret {Secret}\nException 2 of 2: Unable to find the \"default\" profile."), env);
        var cred = Assert.Single(lines, l => l.StartsWith("storage.s3 credentials"));
        Assert.StartsWith("storage.s3 credentials warning=no-credentials source=none problem=\"no AWS credentials found", cred);
        Assert.Contains("sdk=\"Failed to resolve AWS credentials.; Exception 1 of 2: bad secret wJa***EKEY; Exception 2 of 2", cred);
        Assert.Contains("fix=\"set AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY", cred);
        Assert.DoesNotContain(lines, l => l.Contains(Secret));
        Assert.Matches(@"^storage\.check done ok=1 warnings=2$", lines[^1]); // credentials-incomplete + no-credentials; bucket ok (fake)
    }

    [Fact]
    public async Task Hung_credential_resolution_times_out()
    {
        var lines = await RunWith(_ => new TaskCompletionSource<AWSCredentials>().Task, timeout: 1);
        Assert.Contains(lines, l => l.StartsWith("storage.s3 credentials warning=timeout source=none"));
    }

    [Fact]
    public async Task Region_mismatch_reports_bucket_region()
    {
        using var fake = new StorageCheckTests.Head(_ => throw new AmazonS3Exception("The authorization header is malformed; the region 'eu-west-1' is wrong; expecting 'us-east-2'")
            { StatusCode = HttpStatusCode.BadRequest, ErrorCode = "AuthorizationHeaderMalformed" });
        var log = await StorageCheckTests.RunAll(new() { S3AllowedBuckets = ["subs"], S3Region = "eu-west-1" }, fake, new() { ["S3_REGION"] = "eu-west-1" });
        var line = Assert.Single(log.Lines, l => l.Message.StartsWith("storage.s3 bucket=")).Message;
        Assert.Equal("storage.s3 bucket=subs warning=AuthorizationHeaderMalformed status=400 bucket_region=us-east-2 problem=\"bucket is in another region (us-east-2) than S3_REGION=eu-west-1\" fix=\"set S3_REGION=us-east-2\"", line);
    }

    private sealed class Headers(Dictionary<string, string> h) : Amazon.Runtime.Internal.Transform.IWebResponseData
    {
        public long ContentLength => 0;
        public string ContentType => "application/xml";
        public HttpStatusCode StatusCode => HttpStatusCode.MovedPermanently;
        public bool IsSuccessStatusCode => false;
        public string[] GetHeaderNames() => [.. h.Keys];
        public bool IsHeaderPresent(string headerName) => h.ContainsKey(headerName);
        public string GetHeaderValue(string headerName) => h.GetValueOrDefault(headerName) ?? "";
        public Amazon.Runtime.Internal.Transform.IHttpResponseBody ResponseBody => null!;
        public Amazon.Runtime.EventStreams.IEventStreamHeader GetEventStreamHeader(string headerName) => null!;
        public bool IsEventHeaderPresent(string headerName) => false;
    }

    [Fact]
    public async Task Moved_permanently_uses_x_amz_bucket_region_header()
    {
        var inner = new Amazon.Runtime.Internal.HttpErrorResponseException(new Headers(new() { ["x-amz-bucket-region"] = "eu-north-1" }));
        using var fake = new StorageCheckTests.Head(_ => throw new AmazonS3Exception("Error making request", inner) { StatusCode = HttpStatusCode.MovedPermanently, ErrorCode = "MovedPermanently" });
        var log = await StorageCheckTests.RunAll(new() { S3AllowedBuckets = ["subs"], S3Region = "eu-west-1" }, fake, new() { ["S3_REGION"] = "eu-west-1" });
        Assert.Contains("storage.s3 bucket=subs warning=MovedPermanently status=301 bucket_region=eu-north-1 problem=\"bucket is in another region (eu-north-1) than S3_REGION=eu-west-1\" fix=\"set S3_REGION=eu-north-1\"", log.Lines.Select(l => l.Message));
    }

    [Fact]
    public async Task Access_denied_points_at_list_bucket_and_docs()
    {
        using var fake = new StorageCheckTests.Head(_ => throw new AmazonS3Exception("Forbidden") { StatusCode = HttpStatusCode.Forbidden, ErrorCode = "Forbidden" });
        var log = await StorageCheckTests.RunAll(new() { S3AllowedBuckets = ["subs"], S3Region = "eu-west-1" }, fake, new() { ["S3_REGION"] = "eu-west-1" });
        var line = Assert.Single(log.Lines, l => l.Message.StartsWith("storage.s3 bucket=")).Message;
        Assert.Contains("credentials (source=profile)", line);
        Assert.Contains("grant s3:ListBucket on arn:aws:s3:::subs (see docs/deployment.md#s3-permissions)", line);
        Assert.Equal("storage.check done ok=1 warnings=1", log.Lines[^1].Message);
        Assert.Equal(LogLevel.Warning, log.Lines[^1].Level);
    }
}
