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
    /// <summary>Allowlist, hidden and extension checks without touching the filesystem, in the order clients see them.</summary>
    public string Validate(StorageLocation location)
    {
        var path = CheckedPath(location);
        HiddenPaths.Check(path); // before any filesystem call: no existence oracle for hidden names
        ExtensionAllowlist.Shared.Check(location);
        return path;
    }
    public async Task<byte[]> ReadAsync(StorageLocation location, CancellationToken ct) => (await ReadVersionedAsync(location, ct)).Bytes;

    /// <summary>
    /// A cheap version tag (an HTTP entity tag) of a regular file, from one statx on an O_PATH handle: size, mtime, ctime and
    /// inode. The file itself is never opened. Used for If-None-Match on reads by location (the MCP preview cache's change check).
    /// </summary>
    public Task<string> VersionAsync(StorageLocation location, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Validate(location);
        using var parent = Parent(path);
        using var pathHandle = Open((int)parent.DangerousGetHandle(), Path.GetFileName(path), PathOnly);
        return Task.FromResult(Tag(StatRegular(pathHandle)));
    }

    private const uint StatMask = 0x3C1; // TYPE | MTIME | CTIME | INO | SIZE
    private static Statx StatRegular(SafeFileHandle pathHandle)
    {
        if (statx((int)pathHandle.DangerousGetHandle(), "", 0x1000 /* AT_EMPTY_PATH */, StatMask, out var stat) != 0 || (stat.Mode & 0xf000) != 0x8000)
            throw StorageLocation.Rejected("Filesystem inputs must be regular files, not directories, pipes or devices.");
        return stat;
    }
    private static string Tag(Statx s) => $"\"f-{s.Size:x}-{s.MtimeSeconds:x}.{s.MtimeNanos:x}-{s.CtimeSeconds:x}.{s.CtimeNanos:x}-{s.Ino:x}\"";

    public async Task<(byte[] Bytes, string Tag)> ReadVersionedAsync(StorageLocation location, CancellationToken ct)
    {
        var path = Validate(location);
        using var parent = Parent(path);
        // O_PATH first: inspect the type without opening it for reading, so a device node is never really opened (F8).
        using var pathHandle = Open((int)parent.DangerousGetHandle(), Path.GetFileName(path), PathOnly);
        var stat = StatRegular(pathHandle);
        if ((long)stat.Size > opts.MaxUploadBytes) throw Problems.TooLarge(opts);
        // Reopen the same inode through the pinned O_PATH handle; no path lookup, so nothing can be swapped in between.
        var fd = open($"/proc/self/fd/{(int)pathHandle.DangerousGetHandle()}", NonBlock | CloseExec, 0);
        if (fd < 0) throw IoError(Marshal.GetLastPInvokeError());
        using var file = new SafeFileHandle((IntPtr)fd, true);
        if (statx(fd, "", 0x1000, 1, out var again) != 0 || (again.Mode & 0xf000) != 0x8000)
            throw StorageLocation.Rejected("Filesystem inputs must be regular files, not directories, pipes or devices.");
        using var stream = new FileStream(file, FileAccess.Read);
        if (stream.Length > opts.MaxUploadBytes) throw Problems.TooLarge(opts);
        return (await StorageRouter.ReadCapped(stream, opts.MaxUploadBytes, ct), Tag(stat));
    }
    public async Task WriteAsync(StorageLocation location, byte[] bytes, string contentType, CancellationToken ct, bool replace = false)
    {
        var path = Validate(location);
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
            using var existing = Open(fd, name, PathOnly); // O_PATH: inspect without reading/truncating, no-follow
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
        var path = Validate(location);
        using var parent = Parent(path);
        try { using var handle = Open((int)parent.DangerousGetHandle(), Path.GetFileName(path), PathOnly); return Task.FromResult(true); } // O_PATH: never opens the file itself
        catch (ApiException ex) when (ex.Code == "storage-not-found") { return Task.FromResult(false); }
    }
    public Task<bool> DeleteAsync(StorageLocation location, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Validate(location);
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
    /// <summary>
    /// One page of a directory listing (STORAGE_LIST_ENABLED): entries whose name starts with <paramref name="namePrefix"/> and sorts after
    /// <paramref name="after"/> in UTF-8 byte order. The directory is opened component by component without following symlinks, and
    /// entries are inspected with lstat semantics: directories and regular files with a subtitle extension (H3) are listed; symlinks,
    /// devices, pipes, other files and hidden (dot) entries are not. Stateless: the cursor is the last name returned.
    /// Bounded (H6): more than <see cref="StorageListing.MaxEntries"/> matching names, or more than <see cref="StorageListing.MaxScan"/>
    /// names in the folder, is 422 folder-too-large. Sort keys are computed once per name (no allocation per comparison).
    /// </summary>
    public (List<StorageListing.Entry> Items, bool More) List(string directory, string namePrefix, string? after, int pageSize, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw StorageLocation.Rejected("Filesystem storage currently requires a Linux server.");
        CheckedDirectory(directory);
        HiddenPaths.Check(directory);
        if (namePrefix.StartsWith('.')) throw HiddenPaths.Rejected();
        using var dir = OpenDirectory(directory);
        var fd = (int)dir.DangerousGetHandle();
        var afterKey = after == null ? null : StorageListing.Utf8Key(after);
        var matching = 0;
        var scanned = 0;
        var candidates = new List<(byte[] Key, string Name)>();
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries($"/proc/self/fd/{fd}"))
            {
                if (++scanned > StorageListing.MaxScan) throw StorageListing.FolderTooLarge(directory, StorageListing.MaxScan, prefixed: false);
                if ((scanned & 1023) == 0) ct.ThrowIfCancellationRequested();
                var n = Path.GetFileName(path);
                if (n.Length == 0 || n[0] == '.' || n.Contains('\\') || n.Contains('\uFFFD') || !n.StartsWith(namePrefix, StringComparison.Ordinal)) continue;
                if (++matching > StorageListing.MaxEntries) throw StorageListing.FolderTooLarge(directory, StorageListing.MaxEntries, prefixed: namePrefix.Length > 0);
                var key = StorageListing.Utf8Key(n);
                if (afterKey == null || key.AsSpan().SequenceCompareTo(afterKey) > 0) candidates.Add((key, n));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new ApiException(502, "storage-io-failed", "Filesystem listing failed.", "Check mount availability, permissions and the container UID/GID.", ex); }
        candidates.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        var items = new List<StorageListing.Entry>();
        foreach (var (_, name) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (statx(fd, name, 0x100 /* AT_SYMLINK_NOFOLLOW */, 0x7ff, out var st) != 0) continue; // vanished meanwhile
            var type = st.Mode & 0xf000;
            if (type is not (0x8000 or 0x4000)) continue;
            if (type == 0x8000 && !ExtensionAllowlist.Shared.Allowed(name)) continue;
            if (items.Count == pageSize) return (items, true);
            items.Add(type == 0x4000
                ? new(name + "/", true, null, null)
                : new(name, false, (long)st.Size, DateTimeOffset.FromUnixTimeSeconds(st.MtimeSeconds).AddTicks(st.MtimeNanos / 100)));
        }
        return (items, false);
    }
    /// <summary>The directory must be an allowed root or inside one; dot segments and backslashes are rejected.</summary>
    public void CheckedDirectory(string directory)
    {
        if (!directory.StartsWith('/') || !directory.EndsWith('/') || directory.Split('/').Any(s => s is ".." or ".") || directory.Contains('\\') || directory.Any(char.IsControl))
            throw StorageLocation.Rejected("Filesystem folders must be absolute, without dot segments or backslashes.");
        if (!opts.StorageFileRoots.Any(root => directory.StartsWith(Path.GetFullPath(root).TrimEnd('/') + "/", StringComparison.Ordinal)))
            throw StorageLocation.Rejected("Filesystem folder is outside STORAGE_FILE_ROOTS (or filesystem storage is disabled).");
    }
    private static SafeFileHandle OpenDirectory(string directory)
    {
        var dir = Open(-100, "/", DirectoryFlag);
        try
        {
            foreach (var part in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Open((int)dir.DangerousGetHandle(), part, DirectoryFlag);
                dir.Dispose(); dir = next;
            }
            return dir;
        }
        catch { dir.Dispose(); throw; }
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(28)] public ushort Mode; [FieldOffset(32)] public ulong Ino; [FieldOffset(40)] public ulong Size;
        [FieldOffset(96)] public long CtimeSeconds; [FieldOffset(104)] public uint CtimeNanos;
        [FieldOffset(112)] public long MtimeSeconds; [FieldOffset(120)] public uint MtimeNanos;
    }
    [DllImport("libc", SetLastError = true)] private static extern int statx(int dirfd, string path, int flags, uint mask, out Statx stat);
    private const int PathOnly = 2097152; // O_PATH
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags, uint mode);
    private const int WriteOnly = 1, Create = 64, Exclusive = 128, NonBlock = 2048, DirectoryFlag = 65536, NoFollow = 131072, CloseExec = 524288;
    [DllImport("libc", SetLastError = true)] private static extern int openat(int dirfd, string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int unlinkat(int dirfd, string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int renameat2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags);
}
