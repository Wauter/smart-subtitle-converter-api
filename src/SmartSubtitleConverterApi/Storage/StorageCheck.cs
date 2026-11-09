using System.Runtime.InteropServices;
using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Storage;

/// <summary>
/// Warning-only startup connectivity check for enabled storage schemes. It never writes, never fails /readyz and never
/// blocks listening: S3 gets one HeadBucket per allowlisted bucket, filesystem roots get a metadata look, HTTPS hosts are
/// not contacted. Logs carry bucket names, root paths and SDK error codes only (no credentials, keys or contents).
/// </summary>
public sealed class StorageCheck(ApiOptions opts, S3Storage s3, ILogger<StorageCheck> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var bucket in opts.S3AllowedBuckets) // empty allowlist = scheme disabled, nothing logged
        {
            if (ct.IsCancellationRequested) return;
            var (ok, warning, detail) = await s3.ProbeBucketAsync(bucket, ct);
            if (ok) log.LogInformation("storage.s3 bucket={bucket} ok", bucket);
            else log.LogWarning("storage.s3 bucket={bucket} warning={warning} {detail}", bucket, warning, detail);
        }
        if (opts.StorageFileRoots.Length == 0) return;
        if (!OperatingSystem.IsLinux())
        {
            log.LogInformation("storage.file skipped reason=non-linux (filesystem URI storage requires a Linux server)");
            return;
        }
        foreach (var root in opts.StorageFileRoots)
        {
            if (ct.IsCancellationRequested) return;
            var (warning, detail, writable) = CheckRoot(root);
            if (warning == null) log.LogInformation("storage.file root={root} ok writable={writable}", root, writable);
            else log.LogWarning("storage.file root={root} warning={warning} {detail}", root, warning, detail);
        }
    }

    /// <summary>Metadata-only checks that mirror how FileStorage resolves paths (absolute, no symlinked components).</summary>
    public static (string? Warning, string Detail, bool Writable) CheckRoot(string root)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root)) return ("not-absolute", "detail=\"STORAGE_FILE_ROOTS entries must be absolute paths\"", false);
            var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Length == 0) full = "/";
            // FileStorage walks every component with O_NOFOLLOW, so a symlink anywhere in the root path fails every request.
            var current = "/";
            foreach (var part in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var info = new FileInfo(current);
                if (info.LinkTarget != null) return ("symlink", $"detail=\"path component {current} is a symlink; symlinks are not followed, use the resolved path\"", false);
                if (!info.Exists && !Directory.Exists(current)) return ("missing", "detail=\"path does not exist (check the volume mount)\"", false);
            }
            if (!Directory.Exists(full)) return ("not-a-directory", "detail=\"path exists but is not a directory\"", false);
            try { using var e = Directory.EnumerateFileSystemEntries(full).GetEnumerator(); e.MoveNext(); }
            catch (UnauthorizedAccessException) { return ("not-readable", "detail=\"permission denied listing the directory (check the container UID/GID)\"", false); }
            return (null, "", access(full, 2) == 0); // W_OK check only; nothing is created. A read-only input root is fine.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ("io-error", $"error={ex.GetType().Name}", false);
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
        catch (Exception ex) { log.LogWarning("storage.check warning=check-failed error={error}", ex.GetType().Name); }
    }
}
