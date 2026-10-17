using System.Diagnostics;
using System.Threading.Channels;
using SubtitleEditConverterApi.Infrastructure;

namespace SubtitleEditConverterApi.Conversion;

/// <summary>
/// Bounded FIFO queue feeding a single worker thread (design §2.4). libse settings are global,
/// so exactly one job runs at a time. Queue full -> 503 queue-full + Retry-After. A job that
/// overruns its budget -> 504; the worker thread is abandoned (libse can't be cancelled) and a
/// fresh one takes over. 3 timeouts in 5 minutes make /readyz fail.
/// </summary>
public sealed class ConversionQueue : IDisposable
{
    private sealed class Job
    {
        public required Func<object> Work { get; init; }
        public required CancellationToken Cancel { get; init; }
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<object> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long EnqueuedTicks = Stopwatch.GetTimestamp();
        public volatile bool Abandoned;
        /// <summary>The job's private temp dir (binary formats); deleted when the job ends or is abandoned.</summary>
        public readonly Binary.JobTemp Temp = new();
    }

    private readonly Channel<Job> _channel;
    private readonly ApiOptions _opts;
    private readonly ILogger<ConversionQueue> _log;
    private readonly Lock _lock = new();
    private readonly Queue<DateTimeOffset> _timeouts = new();
    private int _generation;
    private int _depth;
    private double _avgJobMs = 50;
    private long _runningSinceTicks; // 0 = idle
    private readonly CancellationTokenSource _shutdown = new();

    public long Completed;
    public long Rejected;
    public long TimedOut;
    public double TotalJobSeconds;
    public double TotalWaitSeconds;

    public ConversionQueue(ApiOptions opts, ILogger<ConversionQueue> log)
    {
        _opts = opts;
        _log = log;
        _channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(opts.QueueCapacity)
        {
            SingleReader = false, // a recycled worker may briefly overlap the abandoned one's exit
            FullMode = BoundedChannelFullMode.Wait,
        });
        StartWorker();
    }

    public int Depth => Volatile.Read(ref _depth);
    public int Capacity => _opts.QueueCapacity;
    public bool IsFull => Depth >= _opts.QueueCapacity;

    public bool Healthy
    {
        get
        {
            lock (_lock)
            {
                while (_timeouts.Count > 0 && _timeouts.Peek() < DateTimeOffset.UtcNow.AddMinutes(-5)) _timeouts.Dequeue();
                return _timeouts.Count < 3;
            }
        }
    }

    private void StartWorker()
    {
        var gen = Interlocked.Increment(ref _generation);
        var t = new Thread(() => WorkerLoop(gen)) { IsBackground = true, Name = $"conversion-worker-{gen}" };
        t.Start();
    }

    private void WorkerLoop(int gen)
    {
        var reader = _channel.Reader;
        while (!_shutdown.IsCancellationRequested && gen == Volatile.Read(ref _generation))
        {
            Job job;
            try
            {
                if (!reader.WaitToReadAsync(_shutdown.Token).AsTask().GetAwaiter().GetResult()) return;
                if (gen != Volatile.Read(ref _generation)) return;
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
            var wait = Stopwatch.GetElapsedTime(job.EnqueuedTicks).TotalSeconds;
            Volatile.Write(ref _runningSinceTicks, Stopwatch.GetTimestamp());
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
                Binary.BinaryIO.Current = null;
                job.Temp.Dispose();
                if (gen == Volatile.Read(ref _generation)) SettingsScope.Reset(); // an abandoned worker must not touch the new one's settings
                sw.Stop();
                Volatile.Write(ref _runningSinceTicks, 0);
                lock (_lock)
                {
                    _avgJobMs = _avgJobMs * 0.8 + sw.Elapsed.TotalMilliseconds * 0.2;
                    Completed++;
                    TotalJobSeconds += sw.Elapsed.TotalSeconds;
                    TotalWaitSeconds += wait;
                }
            }
        }
    }

    /// <summary>Estimate: (queued + running) x max(rolling average, current job's elapsed time).</summary>
    public int RetryAfterSeconds()
    {
        var since = Volatile.Read(ref _runningSinceTicks);
        var runningMs = since == 0 ? 0 : Stopwatch.GetElapsedTime(since).TotalMilliseconds;
        var perJob = Math.Max(_avgJobMs, runningMs);
        return Math.Clamp((int)Math.Ceiling((Depth + 1) * perJob / 1000.0), 1, 3600);
    }

    /// <summary>Run <paramref name="work"/> on the worker. Returns the result and the queue wait.</summary>
    public async Task<(T Result, long QueueWaitMs)> RunAsync<T>(Func<T> work, CancellationToken requestAborted) where T : class
    {
        var job = new Job { Work = () => work(), Cancel = requestAborted };
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
            _log.LogWarning("conversion.timeout after {seconds}s; recycling worker", _opts.ConversionTimeoutSeconds);
            job.Temp.Dispose(); // the stuck job's temp files go now, not when (if) libse returns
            StartWorker(); // abandon the stuck thread; it exits when (if) its job returns
            // Belt and braces: job dirs older than two budgets belong to no live job.
            try { Binary.BinaryIO.SweepJobs(budget * 2, except: null); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw Timeout("running", "running");
        }
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
