using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SubtitleEditConverterApi.Infrastructure;
using Json = SubtitleEditConverterApi.Infrastructure.Json;

namespace SubtitleEditConverterApi.Jobs;

/// <summary>Bounded metadata only. Active work is bounded separately from retained history.</summary>
public sealed class JobStore : IDisposable
{
    public const int MaxMetadataBytes = 128 * 1024;
    private sealed class Entry
    {
        public required string Id;
        public required DateTimeOffset Created;
        public required string Owner;
        public string Status = "running";
        public DateTimeOffset? Finished;
        public JsonObject? Result;
        public JsonObject? Error;
        public string? Input;
        public string? Output;
    }
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Lock _gate = new();
    private readonly ApiOptions _opts;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private int _active;
    public JobStore(ApiOptions opts, TimeProvider time)
    {
        _opts = opts;
        _time = time;
        _timer = time.CreateTimer(_ => { lock (_gate) Expire(); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }
    private void Expire()
    {
        var before = _time.GetUtcNow().AddSeconds(-_opts.JobTtlSeconds);
        foreach (var id in _entries.Values.Where(e => e.Created <= before).Select(e => e.Id).ToList()) _entries.Remove(id);
    }
    public string Create(string owner = "", string? input = null, string? output = null)
    {
        lock (_gate)
        {
            Expire();
            // Evicting running records must never allow an unbounded number of background transfers.
            if (_active >= _opts.JobMaxEntries) throw new ApiException(503, ErrorCodes.QueueFull,
                "The active job limit has been reached.", "Poll existing jobs and retry after Retry-After.") { RetryAfterSeconds = 1 };
            if (_entries.Count >= _opts.JobMaxEntries) _entries.Remove(_entries.Values.MinBy(e => e.Created)!.Id);
            var id = Guid.NewGuid().ToString("N");
            _entries.Add(id, new Entry { Id = id, Created = _time.GetUtcNow(), Owner = owner, Input = Redact(input), Output = Redact(output) });
            _active++;
            return id;
        }
    }
    public void Finish(string id, JsonObject? result = null, JsonObject? error = null)
    {
        lock (_gate)
        {
            _active--;
            Expire();
            if (!_entries.TryGetValue(id, out var e)) return; // expired or evicted work cannot resurrect itself
            e.Status = error == null ? "succeeded" : "failed";
            e.Finished = _time.GetUtcNow();
            e.Result = Bound(result);
            e.Error = Bound(error);
        }
    }
    private static JsonObject? Bound(JsonObject? value)
    {
        if (value == null) return null;
        if (Encoding.UTF8.GetByteCount(value.ToJsonString()) <= MaxMetadataBytes) return (JsonObject)value.DeepClone();
        // Large per-cue diagnostics must not defeat the memory cap. Never truncate JSON text.
        var summary = new JsonObject { ["metadataTruncated"] = true, ["hint"] = "Detailed metadata exceeded the per-job limit; use the synchronous response or requested sidecar for full diagnostics." };
        foreach (var key in new[] { "stats", "code", "status", "title" }) if (value[key] is { } v) summary[key] = v.DeepClone();
        return summary;
    }
    public JsonObject? Get(string id, string owner = "")
    {
        lock (_gate)
        {
            Expire();
            if (!_entries.TryGetValue(id, out var e) || e.Owner != owner) return null;
            return JsonSerializer.SerializeToNode(new { id = e.Id, status = e.Status, createdAt = e.Created, finishedAt = e.Finished,
                input = e.Input, output = e.Output, result = e.Result, error = e.Error }, Json.Options)!.AsObject();
        }
    }
    public int Count { get { lock (_gate) { Expire(); return _entries.Count; } } }
    public static string? Redact(string? location) => Uri.TryCreate(location, UriKind.Absolute, out var u) && u.Scheme == "https"
        ? u.GetLeftPart(UriPartial.Path) : location;
    public void Dispose() => _timer.Dispose();
}
