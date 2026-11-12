using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Storage;

/// <summary>
/// Bounds browsing work (H6): folder listings, reads by location (GET /v1/storage/file) and job file reads
/// (GET /v1/jobs/{id}/input|output). BROWSE_CONCURRENCY run at once (default 4); up to BROWSE_QUEUE_CAPACITY more wait in
/// line (default 32) for at most BROWSE_WAIT_SECONDS (default 30). Beyond that: 503 browse-busy with Retry-After. Separate
/// from the conversion queue, so a long conversion never blocks browsing and browsing never takes the conversion slot.
/// </summary>
public sealed class BrowseQueue
{
    private readonly SemaphoreSlim _slots;
    private readonly ApiOptions _opts;
    private readonly ILogger<BrowseQueue> _log;
    private int _waiting;
    private int _running;
    public long Rejected;
    /// <summary>Highest number of browse requests seen running at the same moment (never above BROWSE_CONCURRENCY).</summary>
    public int MaxRunning;

    public BrowseQueue(ApiOptions opts, ILogger<BrowseQueue> log)
    {
        _opts = opts;
        _log = log;
        _slots = new SemaphoreSlim(opts.BrowseConcurrency, opts.BrowseConcurrency);
    }

    public int Concurrency => _opts.BrowseConcurrency;
    public int Capacity => _opts.BrowseQueueCapacity;
    public int WaitSeconds => _opts.BrowseWaitSeconds;
    public int Waiting => Volatile.Read(ref _waiting);
    public int Running => Volatile.Read(ref _running);

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (!_slots.Wait(0))
        {
            if (Interlocked.Increment(ref _waiting) > _opts.BrowseQueueCapacity)
            {
                Interlocked.Decrement(ref _waiting);
                throw Busy($"{_opts.BrowseConcurrency} browse requests are running and the waiting line is full ({_opts.BrowseQueueCapacity}).");
            }
            bool got;
            try { got = await _slots.WaitAsync(TimeSpan.FromSeconds(_opts.BrowseWaitSeconds), ct); }
            finally { Interlocked.Decrement(ref _waiting); }
            if (!got) throw Busy($"This request waited {_opts.BrowseWaitSeconds} s for a free browse slot.");
        }
        var now = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref MaxRunning)) < now && Interlocked.CompareExchange(ref MaxRunning, now, seen) != seen) { }
        try { return await work(); }
        finally
        {
            Interlocked.Decrement(ref _running);
            _slots.Release();
        }
    }

    private ApiException Busy(string why)
    {
        Interlocked.Increment(ref Rejected);
        _log.LogWarning("browse.busy running={running} waiting={waiting} concurrency={concurrency} capacity={capacity}", Running, Waiting, _opts.BrowseConcurrency, _opts.BrowseQueueCapacity);
        return new ApiException(503, ErrorCodes.BrowseBusy, "The API is busy with other browse and read requests. " + why,
            "Retry after the Retry-After interval. If this happens often, the operator can raise BROWSE_CONCURRENCY or BROWSE_QUEUE_CAPACITY.")
        { RetryAfterSeconds = 2 };
    }
}
