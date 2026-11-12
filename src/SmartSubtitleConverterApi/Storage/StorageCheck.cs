using System.Runtime.InteropServices;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.S3;
using SmartSubtitleConverterApi.Infrastructure;
using static SmartSubtitleConverterApi.Storage.StorageDiagnostics;

namespace SmartSubtitleConverterApi.Storage;

/// <summary>
/// Warning-only startup diagnostics for storage. It never writes, never fails /readyz and never blocks listening.
/// Order: effective configuration (<c>storage.config</c>), resolved AWS credential source and one HeadBucket per
/// allowlisted bucket (<c>storage.s3</c>), metadata checks of filesystem roots (<c>storage.file</c>), then one
/// <c>storage.check done ok=N warnings=M</c> line. HTTPS hosts are not contacted. Secrets are only printed masked.
/// </summary>
public sealed class StorageCheck(ApiOptions opts, S3Storage s3, ILogger<StorageCheck> log, IConfiguration? config = null)
{
    /// <summary>Raw variable lookup; defaults to configuration for API settings and the process environment for AWS_*.</summary>
    public Func<string, string?>? Env { get; init; }
    /// <summary>Credential resolution; defaults to the AWS SDK's default chain (the same one the S3 client uses).</summary>
    public Func<CancellationToken, Task<AWSCredentials>>? ResolveCredentials { get; init; }

    private string? Lookup(string name) => Env != null ? Env(name)
        : name.StartsWith("AWS_", StringComparison.Ordinal) ? Environment.GetEnvironmentVariable(name) : config?[name] ?? Environment.GetEnvironmentVariable(name);

    public async Task RunAsync(CancellationToken ct)
    {
        int ok = 0, warnings = 0;
        void Emit(bool warning, string line)
        {
            if (warning) { warnings++; log.LogWarning("{Line}", line); } else log.LogInformation("{Line}", line);
        }
        try
        {
            foreach (var finding in Inspect(opts, Lookup)) Emit(finding.Warning, finding.Line);
            var schemes = new[] { opts.StorageFileRoots.Length > 0 ? "file" : null, opts.HttpAllowedHosts.Length > 0 ? "https" : null, opts.S3AllowedBuckets.Length > 0 ? "s3" : null }.OfType<string>().ToArray();
            Emit(false, $"storage.config schemes={(schemes.Length == 0 ? "none" : string.Join(',', schemes))}" + (schemes.Length == 0 ? " detail=\"S3_ALLOWED_BUCKETS, STORAGE_FILE_ROOTS and HTTP_ALLOWED_HOSTS are all unset, so only uploads and inline content are accepted\" fix=\"set the allowlist you need; with Docker Compose a .env file reaches the container only through env_file or environment entries\"" : ""));

            if (opts.S3AllowedBuckets.Length > 0)
            {
                var (source, credOk, line) = await Credentials(ct);
                Emit(!credOk, line);
                if (credOk) ok++;
                foreach (var bucket in opts.S3AllowedBuckets)
                {
                    if (ct.IsCancellationRequested) return;
                    if (BucketNameProblem(bucket) != null) { Emit(false, $"storage.s3 bucket={bucket} skipped reason=invalid-name detail=\"see the storage.config warning for S3_ALLOWED_BUCKETS\""); continue; }
                    var (bucketOk, warning, detail) = await s3.ProbeBucketAsync(bucket, ct, source);
                    if (bucketOk) { ok++; Emit(false, $"storage.s3 bucket={bucket} ok"); }
                    else Emit(true, $"storage.s3 bucket={bucket} warning={warning} {Scrub(detail, Lookup)}");
                }
            }
            if (opts.StorageFileRoots.Length > 0)
            {
                if (!OperatingSystem.IsLinux()) Emit(false, "storage.file skipped reason=non-linux detail=\"filesystem URI storage requires a Linux server\"");
                else foreach (var root in opts.StorageFileRoots)
                {
                    if (ct.IsCancellationRequested) return;
                    var (warning, detail, writable) = CheckRoot(root);
                    if (warning == null) { ok++; Emit(false, $"storage.file root={root} ok writable={writable}"); }
                    else Emit(true, $"storage.file root={root} warning={warning} var=STORAGE_FILE_ROOTS {detail}");
                }
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested) log.Log(warnings > 0 ? LogLevel.Warning : LogLevel.Information, "{Line}", $"storage.check done ok={ok} warnings={warnings}");
        }
    }

    /// <summary>Resolves the SDK default credential chain once, bounded by IO_TIMEOUT_SECONDS. Never logs a secret in full.</summary>
    public async Task<(string Source, bool Ok, string Line)> Credentials(CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(opts.IoTimeoutSeconds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        AWSCredentials? creds = null;
        try
        {
            var resolve = ResolveCredentials ?? (token => Task.Run(() => new DefaultAWSCredentialsIdentityResolver().ResolveIdentityAsync(SafeConfig(), token), token));
            creds = await resolve(deadline.Token).WaitAsync(timeout, ct);
            var source = Classify(creds);
            if (source == "anonymous")
                return (source, false, Warn("storage.s3 credentials", "anonymous", "AWS_ACCESS_KEY_ID", "<unset>", "the SDK resolved anonymous credentials; S3 requests are unsigned and private buckets answer 403", "provide credentials via AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY, a profile or a role"));
            var immutable = await creds.GetCredentialsAsync().WaitAsync(timeout, ct); // env/profile: local; roles: the same IMDS/container call the S3 client makes
            var where = source switch
            {
                "profile" => $" profile={Q((Lookup("AWS_PROFILE") ?? "default").Trim())} credentials_file={Q(Lookup("AWS_SHARED_CREDENTIALS_FILE") ?? "~/.aws/credentials")} config_file={Q(Lookup("AWS_CONFIG_FILE") ?? "~/.aws/config")}",
                "web-identity" => $" role_arn={Q(Lookup("AWS_ROLE_ARN"))}",
                "container-role" => " endpoint=container-credentials",
                "instance-role" => " endpoint=instance-metadata",
                _ => "",
            };
            return (source, true, $"storage.s3 credentials ok source={source}{where} access_key_id={Mask(immutable.AccessKey)} session_token={(string.IsNullOrEmpty(immutable.Token) ? "no" : "yes")} type={creds.GetType().Name}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            var source = Classify(creds);
            return (source, false, $"storage.s3 credentials warning=timeout source={source} problem={Q($"credential resolution did not finish within {opts.IoTimeoutSeconds}s (IO_TIMEOUT_SECONDS)")} fix={Q("check that the instance/container metadata endpoint is reachable, or provide AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY or AWS_PROFILE")}");
        }
        catch (Exception ex)
        {
            var found = Classify(creds);
            var problem = creds == null ? "no AWS credentials found in the default chain (environment, web identity, shared profile, container role, instance role); every S3 request will fail"
                : $"the {found} credentials could not be loaded";
            return (found, false, $"storage.s3 credentials warning={(creds == null ? "no-credentials" : "credentials-unavailable")} source={found} problem={Q(problem)} sdk={Q(Scrub(ex.Message, Lookup))} "
                + $"fix={Q("set AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY (and AWS_SESSION_TOKEN for temporary keys), or mount a credentials file and set AWS_PROFILE/AWS_SHARED_CREDENTIALS_FILE, or attach an IAM role to the task/instance/pod")}");
        }
    }

    private AmazonS3Config SafeConfig()
    {
        try { return S3Storage.Configuration(opts); }
        catch (Exception) { return new AmazonS3Config(); } // a malformed endpoint is reported separately; credentials do not depend on it
    }

    /// <summary>Metadata-only checks that mirror how FileStorage resolves paths (absolute, no symlinked components).</summary>
    public static (string? Warning, string Detail, bool Writable) CheckRoot(string root)
    {
        static string D(string problem, string fix) => $"problem={Q(problem)} fix={Q(fix)}";
        try
        {
            if (!Path.IsPathFullyQualified(root)) return ("not-absolute", D("STORAGE_FILE_ROOTS entries must be absolute container paths", $"use an absolute path such as /{root.TrimStart('.', '/')} that matches the volume mount target"), false);
            var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Length == 0) full = "/";
            // FileStorage walks every component with O_NOFOLLOW, so a symlink anywhere in the root path fails every request.
            var current = "/";
            foreach (var part in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var info = new FileInfo(current);
                if (info.LinkTarget != null) return ("symlink", D($"path component {current} is a symlink; symlinks are not followed, so every request under this root fails", $"use the resolved path ({info.ResolveLinkTarget(true)?.FullName ?? info.LinkTarget}) in STORAGE_FILE_ROOTS"), false);
                if (!info.Exists && !Directory.Exists(current)) return ("missing", D($"path does not exist inside the container (first missing component: {current})", root.Contains(',') ? "separate roots with ':' or use a JSON array, not ','" : "check the volume mount target (docker -v host:container / compose volumes) and use the container-side path"), false);
            }
            if (!Directory.Exists(full)) return ("not-a-directory", D("path exists but is a file, not a directory", "point STORAGE_FILE_ROOTS at the directory that contains the subtitles"), false);
            try { using var e = Directory.EnumerateFileSystemEntries(full).GetEnumerator(); e.MoveNext(); }
            catch (UnauthorizedAccessException) { return ("not-readable", D("permission denied listing the directory", "give the container user (see compose 'user:' / the image UID) read and execute permission on the mount, e.g. chown or chmod on the host; check NFS root-squash"), false); }
            return (null, "", access(full, 2) == 0); // W_OK check only; nothing is created. A read-only input root is fine.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ("io-error", D($"could not inspect the path ({ex.GetType().Name})", "check the mount and its permissions"), false);
        }
    }

    [DllImport("libc", SetLastError = true)] private static extern int access(string path, int mode);
}

/// <summary>Runs <see cref="StorageCheck"/> once in the background after start; failures are logged, never thrown.</summary>
public sealed class StorageCheckService(StorageCheck check, ILogger<StorageCheckService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never delay host start / listening
        try { await check.RunAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { log.LogWarning("{Line}", $"storage.check warning=check-failed error={ex.GetType().Name} fix=\"report this; storage requests may still work\""); }
    }
}
