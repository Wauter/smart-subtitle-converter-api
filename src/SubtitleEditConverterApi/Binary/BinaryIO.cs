using System.Diagnostics;
using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.Interfaces;
using Nikse.SubtitleEdit.Core.SubtitleFormats;

namespace SubtitleEditConverterApi.Binary;

/// <summary>
/// Temp files for libse's binary readers (plan §1.4). libse's binary IsMine/LoadSubtitle read a real
/// path, so each job gets a private directory: one per process root created with
/// <see cref="Directory.CreateTempSubdirectory"/> (mode 0700), then a per-job subdirectory that is
/// deleted when the job ends, times out or its worker is recycled. File names are "{Guid}{ext}" and the
/// extension comes from the format registry (or ".bin"); the caller's file name is never used as a path.
/// </summary>
public static partial class BinaryIO
{
    public const string RootPrefix = "se-bin-";
    private static readonly Lock InitLock = new();
    private static DirectoryInfo? _root;

    /// <summary>The job scope of the current conversion-worker thread (set by ConversionQueue).</summary>
    [ThreadStatic] private static JobTemp? _current;

    public static JobTemp? Current { get => _current; set => _current = value; }

    /// <summary>The per-process private temp root, created on first use.</summary>
    public static DirectoryInfo Root
    {
        get
        {
            if (_root is { } r && Directory.Exists(r.FullName)) return r;
            lock (InitLock)
            {
                if (_root is { } r2 && Directory.Exists(r2.FullName)) return r2;
                _root = Directory.CreateTempSubdirectory($"{RootPrefix}{Environment.ProcessId}-");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(_root.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return _root;
            }
        }
    }

    private static int _hosts;

    /// <summary>
    /// A host started using the temp root; dispose on graceful shutdown. The last host to stop removes
    /// the root (a crash leaves it for the next startup sweep). Counted because tests run several hosts per process.
    /// </summary>
    public static IDisposable AcquireRoot()
    {
        Interlocked.Increment(ref _hosts);
        return new RootLease();
    }

    private sealed class RootLease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            lock (InitLock)
            {
                if (Interlocked.Decrement(ref _hosts) == 0 && _root is { } r)
                {
                    TryDelete(r.FullName);
                    _root = null;
                }
            }
        }
    }

    [GeneratedRegex("^se-bin-(\\d+)-")]
    private static partial Regex RootName();

    /// <summary>
    /// Startup sweep: remove temp roots left by earlier processes (crash, kill, restart with a
    /// persistent /tmp) and stale job directories in our own root. Roots of other live processes stay.
    /// </summary>
    public static int SweepAtStartup(Microsoft.Extensions.Logging.ILogger? log = null)
    {
        var removed = 0;
        var mine = Root.FullName;
        foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), RootPrefix + "*"))
        {
            if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(mine), StringComparison.Ordinal)) continue;
            var m = RootName().Match(Path.GetFileName(dir));
            if (!m.Success) continue;
            var pid = int.Parse(m.Groups[1].Value);
            if (pid != Environment.ProcessId && IsAlive(pid)) continue;
            if (TryDelete(dir)) removed++;
        }
        removed += SweepJobs(TimeSpan.Zero, except: null);
        if (removed > 0) log?.LogInformation("binary.tempSweep removed={removed}", removed);
        return removed;
    }

    /// <summary>Remove job directories older than <paramref name="minAge"/> (except the given one).</summary>
    public static int SweepJobs(TimeSpan minAge, JobTemp? except)
    {
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(Root.FullName, "job-*"))
        {
            if (except?.Path is { } keep && string.Equals(dir, keep, StringComparison.Ordinal)) continue;
            try
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) < minAge) continue;
            }
            catch (IOException) { continue; }
            if (TryDelete(dir)) removed++;
        }
        return removed;
    }

    /// <summary>Number of entries currently below the temp root (tests and diagnostics).</summary>
    public static int CountEntries() => Directory.EnumerateFileSystemEntries(Root.FullName, "*", SearchOption.AllDirectories).Count();

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---------------- reading ----------------

    /// <summary>Formats with an in-memory loader (no temp file needed for the load itself).</summary>
    public static bool HasByteLoader(SubtitleFormat f) => f is Ebu or Pac or CheetahCaptionOld; // PacUnicode derives from Pac

    /// <summary>Load <paramref name="path"/> (whose bytes are <paramref name="bytes"/>) with a fresh format instance.</summary>
    public static Subtitle Load(SubtitleFormat format, byte[] bytes, string path)
    {
        var s = new Subtitle();
        switch (format)
        {
            case Ebu e: e.LoadSubtitle(s, bytes); break;
            case Pac p: p.LoadSubtitle(s, bytes); break;
            case CheetahCaptionOld c: c.LoadSubtitle(s, bytes); break;
            default: format.LoadSubtitle(s, [], path); break;
        }
        return s;
    }

    // ---------------- writing ----------------

    public static byte[] Save(SubtitleFormat format, Subtitle subtitle, string fileName, Ebu.EbuGeneralSubtitleInformation? ebuHeader)
    {
        using var ms = new MemoryStream();
        bool ok;
        if (format is Ebu ebu) ok = ebu.Save(fileName, ms, subtitle, true, ebuHeader);
        else if (format is IBinaryPersistableSubtitle w) ok = w.Save(fileName, ms, subtitle, true);
        else throw new InvalidOperationException($"{format.Name} has no stream writer");
        if (!ok || ms.Length == 0) throw new InvalidOperationException($"libse's {format.Name} writer produced no output");
        return ms.ToArray();
    }

    /// <summary>
    /// Ebu.Save writes nothing unless this static is set, even in batch mode (plan §1). Same as
    /// seconv's HeadlessEbuUiHelper: no dialog, justification code 2 (centred).
    /// </summary>
    public sealed class HeadlessEbuUiHelper : Ebu.IEbuUiHelper
    {
        public void Initialize(Ebu.EbuGeneralSubtitleInformation header, byte displayStandardCode, string fileName, Subtitle subtitle) { }
        public bool ShowDialogOk() => true;
        public byte JustificationCode { get; set; } = 2;
    }

    public static void RegisterEbuHelper()
    {
        if (Ebu.EbuUiHelper is not HeadlessEbuUiHelper) Ebu.EbuUiHelper = new HeadlessEbuUiHelper();
    }
}

/// <summary>
/// One job's temp directory (created lazily, so text-only jobs never touch disk). Dispose deletes it;
/// the queue also disposes it from the request thread when the job times out.
/// </summary>
public sealed class JobTemp : IDisposable
{
    private readonly Lock _lock = new();
    private string? _path;
    private bool _disposed;

    public string? Path => _path;

    public string NewFile(string extension)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_path == null)
            {
                var p = System.IO.Path.Combine(BinaryIO.Root.FullName, "job-" + Guid.NewGuid().ToString("N"));
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(p);
                else Directory.CreateDirectory(p, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                _path = p;
            }
            return System.IO.Path.Combine(_path, Guid.NewGuid().ToString("N") + extension);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_path != null) BinaryIO.TryDelete(_path);
        }
    }
}

/// <summary>A temp file that is deleted on dispose; the extension can be changed by renaming.</summary>
public sealed class TempFile : IDisposable
{
    private readonly JobTemp _job;
    public string Path { get; private set; }

    public TempFile(JobTemp job, byte[] bytes, string extension)
    {
        _job = job;
        Path = job.NewFile(extension);
        var fso = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) fso.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(Path, fso))
        {
            fs.Write(bytes);
        }
    }

    /// <summary>Give the file another (registry-provided) extension; libse IsMine checks extensions.</summary>
    public void WithExtension(string extension)
    {
        if (Path.EndsWith(extension, StringComparison.Ordinal)) return;
        var next = _job.NewFile(extension);
        File.Move(Path, next);
        Path = next;
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
