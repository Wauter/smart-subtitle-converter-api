using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Storage;

/// <summary>Linux directory-handle traversal prevents symlink swaps from escaping an allowed tree.</summary>
public sealed class FileStorage(ApiOptions opts) : IStorage
{
    private string CheckedPath(StorageLocation location)
    {
        var path = location.Value;
        if (!Path.IsPathFullyQualified(path) || path.Split('/', '\\').Any(s => s is ".." or ".") || path.Contains('\\'))
            throw StorageLocation.Rejected("Filesystem paths must be absolute, without dot segments or backslashes.");
        var full = Path.GetFullPath(path);
        if (!opts.StorageFileRoots.Any(root => full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw StorageLocation.Rejected("Filesystem path is outside STORAGE_FILE_ROOTS (or filesystem storage is disabled).");
        if (!OperatingSystem.IsLinux()) throw StorageLocation.Rejected("Filesystem URI storage currently requires a Linux server; use uploads or another enabled storage scheme on this platform.");
        return full;
    }
    private static ApiException IoError(int errno, bool writing = false) => errno switch
    {
        2 => new(404, "storage-not-found", "The storage file or parent directory does not exist.", "Create the parent directories and check the full file path."),
        17 => StorageLocation.OutputExists(),
        40 or 20 => StorageLocation.Rejected("A path component is a symlink or is not a directory. Symlinks are not followed."),
        _ => new(502, "storage-io-failed", $"Filesystem {(writing ? "write" : "read")} failed: {new Win32Exception(errno).Message}.", "Check mount availability, permissions and the container UID/GID."),
    };
    private static SafeFileHandle Open(int parent, string name, int flags, uint mode = 0)
    {
        var fd = openat(parent, name, flags | NoFollow | CloseExec, mode);
        if (fd < 0) throw IoError(Marshal.GetLastPInvokeError(), (flags & WriteOnly) != 0);
        return new SafeFileHandle((IntPtr)fd, true);
    }
    private static SafeFileHandle Parent(string full)
    {
        var dir = Open(-100, "/", DirectoryFlag);
        try
        {
            foreach (var part in Path.GetDirectoryName(full)!.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Open((int)dir.DangerousGetHandle(), part, DirectoryFlag);
                dir.Dispose(); dir = next;
            }
            return dir;
        }
        catch { dir.Dispose(); throw; }
    }
    public async Task<byte[]> ReadAsync(StorageLocation location, CancellationToken ct)
    {
        var path = CheckedPath(location);
        using var parent = Parent(path);
        using var file = Open((int)parent.DangerousGetHandle(), Path.GetFileName(path), 0 | NonBlock);
        if (statx((int)file.DangerousGetHandle(), "", 0x1000, 1, out var stat) != 0 || (stat.Mode & 0xf000) != 0x8000)
            throw StorageLocation.Rejected("Filesystem inputs must be regular files, not directories, pipes or devices.");
        using var stream = new FileStream(file, FileAccess.Read);
        if (stream.Length > opts.MaxUploadBytes) throw Problems.TooLarge(opts);
        return await StorageRouter.ReadCapped(stream, opts.MaxUploadBytes, ct);
    }
    public async Task WriteAsync(StorageLocation location, byte[] bytes, string contentType, CancellationToken ct, bool replace = false)
    {
        var path = CheckedPath(location);
        if (bytes.LongLength > opts.MaxUploadBytes) throw Problems.TooLarge(opts);
        using var parent = Parent(path);
        var fd = (int)parent.DangerousGetHandle();
        var name = Path.GetFileName(path);
        if (replace) EnsureReplaceable(fd, name);
        var staging = opts.StorageFileAtomic || replace; // replacements preserve the old file until a complete write
        var staged = staging ? "." + name + "." + Guid.NewGuid().ToString("N") + ".partial" : name;
        var created = false;
        try
        {
            using (var handle = Open(fd, staged, WriteOnly | Create | Exclusive, 0x1A4)) // 0644 (subject to the process umask) so MAM/playout users can read deliveries; no overwrite
            {
                created = true;
                using var stream = new FileStream(handle, FileAccess.Write);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            if (replace) EnsureReplaceable(fd, name);
            if (staging && renameat2(fd, staged, fd, name, replace ? 0u : 1u) != 0) throw IoError(Marshal.GetLastPInvokeError(), true);
            created = false;
        }
        catch (IOException ex) { throw new ApiException(502, "storage-io-failed", "Filesystem write failed.", "Check mount availability, space and permissions.", ex); }
        finally { if (created) _ = unlinkat(fd, staged, 0); }
    }
    private static void EnsureReplaceable(int fd, string name)
    {
        try
        {
            using var existing = Open(fd, name, 2097152); // O_PATH: inspect without reading/truncating, no-follow
            if (statx((int)existing.DangerousGetHandle(), "", 0x1000, 1, out var stat) != 0)
                throw IoError(Marshal.GetLastPInvokeError());
            if ((stat.Mode & 0xf000) != 0x8000)
                throw StorageLocation.Rejected("Replacement destinations must be regular files, never symlinks, directories or devices.");
        }
        catch (ApiException ex) when (ex.Code == "storage-not-found") { } // new file is allowed too
    }
    public Task<bool> ExistsAsync(StorageLocation location, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = CheckedPath(location);
        using var parent = Parent(path);
        try { using var handle = Open((int)parent.DangerousGetHandle(), Path.GetFileName(path), 0 | NonBlock); return Task.FromResult(true); }
        catch (ApiException ex) when (ex.Code == "storage-not-found") { return Task.FromResult(false); }
    }
    public Task<bool> DeleteAsync(StorageLocation location, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = CheckedPath(location);
        using var parent = Parent(path);
        var fd = (int)parent.DangerousGetHandle();
        var name = Path.GetFileName(path);
        try { EnsureReplaceable(fd, name); } // regular files only: never a symlink, directory or device
        catch (ApiException ex) when (ex.Code == "storage-not-found") { return Task.FromResult(false); }
        if (unlinkat(fd, name, 0) == 0) return Task.FromResult(true);
        var errno = Marshal.GetLastPInvokeError();
        if (errno == 2) return Task.FromResult(false);
        throw IoError(errno, true);
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx { [FieldOffset(28)] public ushort Mode; }
    [DllImport("libc", SetLastError = true)] private static extern int statx(int dirfd, string path, int flags, uint mask, out Statx stat);
    private const int WriteOnly = 1, Create = 64, Exclusive = 128, NonBlock = 2048, DirectoryFlag = 65536, NoFollow = 131072, CloseExec = 524288;
    [DllImport("libc", SetLastError = true)] private static extern int openat(int dirfd, string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int unlinkat(int dirfd, string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int renameat2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags);
}
