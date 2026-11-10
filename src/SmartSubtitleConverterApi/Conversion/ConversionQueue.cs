using System.Diagnostics;
using System.Threading.Channels;
using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Conversion;

/// <summary>What the logs say about a conversion (never content). Built by the caller; every field optional.</summary>
public sealed record ConversionJobInfo(string? JobId = null, string? RequestId = null, string? FileName = null, long? Bytes = null, string? SourceFormat = null, string? Profile = null)
{
    public override string ToString() =>
        $"job={JobId ?? "-"} request={RequestId ?? "-"} file={StorageSafe(FileName)} bytes={Bytes?.ToString() ?? "-"} sourceFormat={SourceFormat ?? "auto"} profile={StorageSafe(Profile)}";
    private static string StorageSafe(string? s) => s == null ? "-" : Storage.StorageDiagnostics.Q(s.Length > 200 ? s[..200] + "..." : s);
}

/// <summary>Ends the process on purpose (H8). Replaceable in tests.</summary>
public class ProcessExit
{
    /// <summary>
    /// Exit with <paramref name="code"/>. With DOTNET_DbgEnableMiniDump=1 the runtime's crash dump is requested instead
    /// (Environment.FailFast), which captures every thread's stack, including the stuck conversion's.
    /// </summary>
    public virtual void Exit(int code, string reason)
    {
        if (Environment.GetEnvironmentVariable("DOTNET_DbgEnableMiniDump") == "1") Environment.FailFast(reason);
        Environment.Exit(code);
    }
}

/// <summary>
/// Bounded FIFO queue feeding a single worker thread (design §2.4). libse settings are process-global, so exactly one
/// conversion runs at a time, ever. Queue full -> 503 queue-full + Retry-After. A conversion that overruns its budget -> 504
/// for its caller; libse can't be cancelled, so the thread keeps running. While it does (H8), no other conversion starts:
/// new and queued requests get 503 conversion-engine-stuck and /readyz reports not ready. If it returns, work resumes on the
/// same worker. If it is still running after CONVERSION_STUCK_GRACE_SECONDS (default 2 x CONVERSION_TIMEOUT_SECONDS), the
/// process logs everything it knows and exits with code 70 so the container restart policy brings up a clean process.
/// </summary>
public sealed class ConversionQueue : IDisposable
{
    public const int StuckExitCode = 70; // EX_SOFTWARE

    private sealed class Job
    {
        public required Func<object> Work { get; init; }
        public required CancellationToken Cancel { get; init; }
        public ConversionJobInfo? Info { get; init; }
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<object> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long EnqueuedTicks = Stopwatch.GetTimestamp();
        public long StartedTicks;
        public volatile bool Abandoned;
        /// <summary>The job's private temp dir (binary formats); deleted when the job ends or is abandoned.</summary>
        public readonly Binary.JobTemp Temp = new();
    }

    private sealed record StuckState(Job Job, DateTimeOffset TimedOutAt, DateTimeOffset ExitAt);

    private readonly Channel<Job> _channel;
    private readonly ApiOptions _opts;
    private readonly ILogger<ConversionQueue> _log;
    private readonly ProcessExit _exit;
    private readonly Lock _lock = new();
    private readonly Queue<DateTimeOffset> _timeouts = new();
    private readonly Thread _worker;
    private int _depth;
    private double _avgJobMs = 50;
    private long _runningSinceTicks; // 0 = idle
    private Job? _current;
    private volatile StuckState? _stuck;
    private readonly CancellationTokenSource _shutdown = new();

    public long Completed;
    public long Rejected;
    public long TimedOut;
    public long StuckRejected;
    public double TotalJobSeconds;
    public double TotalWaitSeconds;
    /// <summary>Highest number of conversions seen running at the same moment (always 1; tests assert it).</summary>
    public int MaxConcurrent;
    private int _concurrent;

    public ConversionQueue(ApiOptions opts, ILogger<ConversionQueue> log, ProcessExit? exit = null)
    {
        _opts = opts;
        _log = log;
        _exit = exit ?? new ProcessExit();
        _channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(opts.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "conversion-worker" };
        _worker.Start();
    }

    public int Depth => Volatile.Read(ref _depth);
    public int Capacity => _opts.QueueCapacity;
    public bool IsFull => Depth >= _opts.QueueCapacity;
    /// <summary>True while a timed-out conversion is still running (no new conversion starts).</summary>
    public bool IsStuck => _stuck != null;

    public bool Healthy
    {
        get
        {
            if (IsStuck) return false;
            lock (_lock)
            {
                while (_timeouts.Count > 0 && _timeouts.Peek() < DateTimeOffset.UtcNow.AddMinutes(-5)) _timeouts.Dequeue();
                return _timeouts.Count < 3;
            }
        }
    }

    /// <summary>Why /readyz reports the worker unhealthy, or "ok".</summary>
    public string WorkerStatus => IsStuck ? "unhealthy: conversion engine stuck (a timed-out conversion is still running; no new conversions until it returns or the process restarts)"
        : Healthy ? "ok" : "unhealthy: repeated conversion timeouts";

    private void WorkerLoop()
    {
        var reader = _channel.Reader;
        while (!_shutdown.IsCancellationRequested)
        {
            Job job;
            try
            {
                if (!reader.WaitToReadAsync(_shutdown.Token).AsTask().GetAwaiter().GetResult()) return;
                if (!reader.TryRead(out job!)) continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Interlocked.Decrement(ref _depth);

            if (job.Cancel.IsCancellationRequested || job.Abandoned)
            {
                job.Temp.Dispose();
                job.Done.TrySetCanceled();
                continue; // client went away / deadline passed while queued: never run it
            }
            if (_stuck != null)
            {
                // Only reachable if a job slipped in while the stuck one was being recorded: never run beside it.
                job.Temp.Dispose();
                job.Started.TrySetException(EngineStuck());
                continue;
            }
            var wait = Stopwatch.GetElapsedTime(job.EnqueuedTicks).TotalSeconds;
            job.StartedTicks = Stopwatch.GetTimestamp();
            Volatile.Write(ref _runningSinceTicks, job.StartedTicks);
            Volatile.Write(ref _current, job);
            var now = Interlocked.Increment(ref _concurrent);
            InterlockedMax(ref MaxConcurrent, now);
            job.Started.TrySetResult();
            var sw = Stopwatch.StartNew();
            try
            {
                Binary.BinaryIO.Current = job.Temp;
                var result = job.Work();
                job.Done.TrySetResult(result);
            }
            catch (Exception ex)
            {
                job.Done.TrySetException(ex);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
                Binary.BinaryIO.Current = null;
                job.Temp.Dispose();
                SettingsScope.Reset();
                sw.Stop();
                Volatile.Write(ref _runningSinceTicks, 0);
                Volatile.Write(ref _current, null);
                lock (_lock)
                {
                    _avgJobMs = _avgJobMs * 0.8 + sw.Elapsed.TotalMilliseconds * 0.2;
                    Completed++;
                    TotalJobSeconds += sw.Elapsed.TotalSeconds;
                    TotalWaitSeconds += wait;
                }
                if (_stuck is { } s && s.Job == job)
                {
                    _stuck = null;
                    _log.LogWarning("{Line}", $"conversion.recovered the timed-out conversion returned after {sw.Elapsed.TotalSeconds:0.0}s; accepting conversions again. {job.Info}");
                }
            }
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    /// <summary>Estimate: (queued + running) x max(rolling average, current job's elapsed time).</summary>
    public int RetryAfterSeconds()
    {
        if (_stuck is { } s) return Math.Clamp((int)Math.Ceiling((s.ExitAt - DateTimeOffset.UtcNow).TotalSeconds) + 5, 1, 3600);
        var since = Volatile.Read(ref _runningSinceTicks);
        var runningMs = since == 0 ? 0 : Stopwatch.GetElapsedTime(since).TotalMilliseconds;
        var perJob = Math.Max(_avgJobMs, runningMs);
        return Math.Clamp((int)Math.Ceiling((Depth + 1) * perJob / 1000.0), 1, 3600);
    }

    private ApiException EngineStuck()
    {
        Interlocked.Increment(ref StuckRejected);
        return new ApiException(503, ErrorCodes.ConversionEngineStuck,
            "A previous conversion did not finish within its time limit and is still running, so no new conversion can start safely right now.",
            "Retry after the Retry-After interval. The server recovers by itself: the stuck conversion either finishes, or the API restarts automatically "
            + "(the container needs a restart policy such as restart: unless-stopped). If it keeps happening, send the server logs (conversion.stuck lines) to the operator.")
        { RetryAfterSeconds = RetryAfterSeconds() };
    }

    /// <summary>Run <paramref name="work"/> on the worker. Returns the result and the queue wait.</summary>
    public async Task<(T Result, long QueueWaitMs)> RunAsync<T>(Func<T> work, CancellationToken requestAborted, ConversionJobInfo? info = null) where T : class
    {
        if (_stuck != null) throw EngineStuck();
        var job = new Job { Work = () => work(), Cancel = requestAborted, Info = info };
        Interlocked.Increment(ref _depth);
        if (!_channel.Writer.TryWrite(job))
        {
            Interlocked.Decrement(ref _depth);
            Interlocked.Increment(ref Rejected);
            _log.LogWarning("queue.full depth={depth} capacity={capacity}", Depth, Capacity);
            throw new ApiException(503, ErrorCodes.QueueFull, $"The conversion queue is full ({Capacity} waiting).",
                "Retry after the Retry-After interval, or scale out with more replicas.")
            { RetryAfterSeconds = RetryAfterSeconds() };
        }

        var budget = TimeSpan.FromSeconds(_opts.ConversionTimeoutSeconds);
        var queued = Stopwatch.StartNew();
        try
        {
            await job.Started.Task.WaitAsync(budget, requestAborted);
        }
        catch (TimeoutException)
        {
            job.Abandoned = true;
            job.Temp.Dispose();
            if (_stuck != null) throw EngineStuck();
            Interlocked.Increment(ref TimedOut);
            throw Timeout("waiting in the queue", "queue");
        }
        catch (OperationCanceledException)
        {
            job.Abandoned = true;
            throw;
        }
        var waitMs = queued.ElapsedMilliseconds;

        try
        {
            var result = await job.Done.Task.WaitAsync(budget);
            return ((T)result, waitMs);
        }
        catch (TimeoutException)
        {
            job.Abandoned = true;
            Interlocked.Increment(ref TimedOut);
            lock (_lock) _timeouts.Enqueue(DateTimeOffset.UtcNow);
            job.Temp.Dispose(); // the stuck job's temp files go now, not when (if) libse returns
            // Belt and braces: job dirs older than two budgets belong to no live job.
            try { Binary.BinaryIO.SweepJobs(budget * 2, except: null); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (!job.Done.Task.IsCompleted) EnterStuck(job);
            throw Timeout("running", "running");
        }
    }

    private void EnterStuck(Job job)
    {
        var grace = TimeSpan.FromSeconds(_opts.EffectiveStuckGraceSeconds);
        var now = DateTimeOffset.UtcNow;
        var state = new StuckState(job, now, now + grace);
        lock (_lock)
        {
            if (_stuck != null || job.Done.Task.IsCompleted) return;
            _stuck = state;
        }
        var drained = 0;
        while (_channel.Reader.TryRead(out var queued))
        {
            Interlocked.Decrement(ref _depth);
            queued.Abandoned = true;
            queued.Temp.Dispose();
            queued.Started.TrySetException(EngineStuck());
            drained++;
        }
        var elapsed = Stopwatch.GetElapsedTime(job.StartedTicks).TotalSeconds;
        _log.LogError("{Line}", new string('#', 100));
        _log.LogError("{Line}", $"conversion.stuck A conversion exceeded CONVERSION_TIMEOUT_SECONDS={_opts.ConversionTimeoutSeconds} and is still running (elapsed={elapsed:0.0}s). {job.Info}");
        _log.LogError("{Line}", $"conversion.stuck libse settings are process-global, so no other conversion may run beside it: new conversions get 503 conversion-engine-stuck and /readyz reports not ready. "
            + $"Rejected {drained} queued conversion(s) now.");
        _log.LogError("{Line}", $"conversion.stuck If it has not returned by {state.ExitAt:yyyy-MM-ddTHH:mm:ssZ} (grace {grace.TotalSeconds:0}s, CONVERSION_STUCK_GRACE_SECONDS), the API exits with code {StuckExitCode} "
            + "and the container restart policy (restart: unless-stopped) starts a clean process.");
        _log.LogError("{Line}", $"conversion.stuck worker thread={_worker.Name} managedThreadId={_worker.ManagedThreadId} queueDepth={Depth} queueCapacity={Capacity} completed={Completed} timedOut={TimedOut}. "
            + "A .NET process can't read another thread's stack; to capture it, set DOTNET_DbgEnableMiniDump=1 (and DOTNET_DbgMiniDumpName=/tmp/dumps/core.%p on a writable volume): "
            + "the exit then writes a crash dump with every thread's stack (inspect with dotnet-dump analyze, clrstack -all).");
        _log.LogError("{Line}", "conversion.stuck To investigate, keep a copy of the input named above and report it with these log lines; no file content is logged.");
        _log.LogError("{Line}", new string('#', 100));
        _ = WatchAsync(state);
    }

    private async Task WatchAsync(StuckState state)
    {
        try { await Task.Delay(state.ExitAt - DateTimeOffset.UtcNow, _shutdown.Token); }
        catch (OperationCanceledException) { return; }
        if (_stuck != state) return; // recovered meanwhile
        var elapsed = Stopwatch.GetElapsedTime(state.Job.StartedTicks).TotalSeconds;
        var reason = $"conversion.stuck-exit The conversion engine has been stuck for {elapsed:0.0}s (timeout {_opts.ConversionTimeoutSeconds}s + grace {_opts.EffectiveStuckGraceSeconds}s). "
            + $"Exiting with code {StuckExitCode} so the restart policy starts a clean process. {state.Job.Info} queueDepth={Depth} rejectedWhileStuck={StuckRejected}";
        _log.LogCritical("{Line}", reason);
        _log.LogCritical("{Line}", "conversion.stuck-exit Jobs are in memory only: running and finished job records are lost with this restart. Make sure the container has restart: unless-stopped (or always).");
        await Task.Delay(500); // let the console logger flush
        _exit.Exit(StuckExitCode, reason);
    }

    /// <summary>504 with a machine-readable phase: "queue" is retryable like 503, "running" means split or simplify.</summary>
    private ApiException Timeout(string what, string phase)
    {
        var ex = new ApiException(504, ErrorCodes.ConversionTimeout, $"Conversion exceeded {_opts.ConversionTimeoutSeconds} s while {what}.",
            phase == "queue"
                ? "The server is busy: retry later (like 503 queue-full), or scale out with more replicas."
                : "Split the file or simplify operations; CONVERSION_TIMEOUT_SECONDS sets the budget.");
        ex.Extensions["phase"] = phase;
        return ex;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _channel.Writer.TryComplete();
    }
}
