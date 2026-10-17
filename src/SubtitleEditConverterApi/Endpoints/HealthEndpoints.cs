using System.Globalization;
using System.Text;
using SubtitleEditConverterApi.Conversion;
using SubtitleEditConverterApi.Infrastructure;
using SubtitleEditConverterApi.Profiles;

namespace SubtitleEditConverterApi.Endpoints;

/// <summary>Startup self-test result used by /readyz (embedded SRT -> WebVTT, a Skia touch, ICU/globalization probes).</summary>
public sealed class SelfTest
{
    public bool? Passed { get; set; }
    public string? Error { get; set; }
    public bool SkiaOk { get; set; }
    public string? SkiaError { get; set; }
    public IReadOnlyList<string>? GlobalizationFailures { get; set; }

    public const string Srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\nHello <i>world</i>.\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nSecond line\r\n";
}

public sealed class SelfTestService(SelfTest state, ConversionQueue queue, ConversionEngine engine, ILogger<SelfTestService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var profile = new Profile { Target = new TargetOptions("webvtt", null, null, null) };
            var (r, _) = await queue.RunAsync(() => engine.Convert(Encoding.UTF8.GetBytes(SelfTest.Srt), "selftest.srt", profile), stoppingToken);
            var text = Encoding.UTF8.GetString(r.Outputs[0].Bytes);
            state.Passed = text.Contains("WEBVTT") && r.Outputs[0].Cues == 2;
            if (state.Passed != true) state.Error = "Self-test output did not look like WebVTT.";
        }
        catch (Exception ex)
        {
            state.Passed = false;
            state.Error = ex.Message;
        }
        try
        {
            using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 14);
            _ = font.MeasureText("Skia");
            state.SkiaOk = true;
        }
        catch (Exception ex)
        {
            state.SkiaError = ex.GetType().Name + ": " + ex.Message;
        }
        state.GlobalizationFailures = GlobalizationCheck.Run();
        log.LogInformation("selftest passed={passed} skia={skia} globalization={glob} error={error}", state.Passed, state.SkiaOk,
            state.GlobalizationFailures.Count == 0 ? "ok" : string.Join(", ", state.GlobalizationFailures), state.Error ?? state.SkiaError);
    }
}

public static class HealthEndpoints
{
    public static void Map(WebApplication app, ApiOptions opts)
    {
        app.MapGet("/healthz", () => Results.Json(new { status = "ok", version = LibseInfo.AppVersion, libse = LibseInfo.Version }, Json.Options))
            .WithTags("health").WithSummary("Liveness: the process is up.").ExcludeFromDescription();

        app.MapGet("/readyz", (SelfTest st, ConversionQueue q, PresetStore store, Formats.FormatRegistry reg) =>
        {
            var (valid, invalid) = store.Counts();
            var checks = new Dictionary<string, object?>
            {
                ["formats"] = reg.All.Count > 0 ? "ok" : "no formats loaded",
                ["selfTest"] = st.Passed switch { true => "ok", false => "failed: " + st.Error, null => "pending" },
                ["skia"] = st.SkiaOk ? "ok" : st.Passed == null ? "pending" : "failed: " + st.SkiaError,
                ["globalization"] = st.GlobalizationFailures switch
                {
                    null => "pending",
                    { Count: 0 } => "ok",
                    var f => "failed: " + string.Join(", ", f),
                },
                ["worker"] = q.Healthy ? "ok" : "unhealthy: repeated conversion timeouts",
                ["queue"] = q.IsFull ? "full" : "ok",
            };
            var ready = checks.Values.All(v => (string?)v == "ok");
            var body = new
            {
                status = ready ? "ready" : "not-ready",
                checks,
                failing = checks.Where(c => (string?)c.Value != "ok").Select(c => c.Key).ToArray(),
                queue = new { depth = q.Depth, capacity = q.Capacity },
                profiles = new { valid, invalid },
                version = LibseInfo.AppVersion,
                libse = LibseInfo.Version,
            };
            return Results.Json(body, Json.Options, statusCode: ready ? 200 : 503);
        }).WithTags("health").ExcludeFromDescription();

        if (opts.MetricsEnabled)
        {
            var ep = app.MapGet("/metrics", (ConversionQueue q, Metrics m) =>
            {
                var sb = new StringBuilder();
                void Line(string name, string type, string help, double value, string labels = "")
                {
                    sb.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
                    sb.Append("# TYPE ").Append(name).Append(' ').AppendLine(type);
                    sb.Append(name).Append(labels).Append(' ').AppendLine(value.ToString(CultureInfo.InvariantCulture));
                }
                Line("subtitleedit_queue_depth", "gauge", "Jobs waiting in the conversion queue.", q.Depth);
                Line("subtitleedit_queue_capacity", "gauge", "Queue capacity.", q.Capacity);
                Line("subtitleedit_conversions_total", "counter", "Completed queue jobs.", q.Completed);
                Line("subtitleedit_conversion_seconds_sum", "counter", "Total job run time.", q.TotalJobSeconds);
                Line("subtitleedit_queue_wait_seconds_sum", "counter", "Total queue wait time.", q.TotalWaitSeconds);
                sb.AppendLine("# HELP subtitleedit_rejected_total Rejected requests by reason.");
                sb.AppendLine("# TYPE subtitleedit_rejected_total counter");
                sb.Append("subtitleedit_rejected_total{reason=\"queue-full\"} ").AppendLine(q.Rejected.ToString());
                sb.Append("subtitleedit_rejected_total{reason=\"timeout\"} ").AppendLine(q.TimedOut.ToString());
                foreach (var (code, n) in m.Snapshot())
                    sb.Append("subtitleedit_rejected_total{reason=\"").Append(code).Append("\"} ").AppendLine(n.ToString());
                return Results.Text(sb.ToString(), "text/plain; version=0.0.4");
            }).ExcludeFromDescription();
            if (opts.MetricsPort is { } port) ep.RequireHost($"*:{port}");
        }
    }
}

/// <summary>Counts error responses by code (for /metrics).</summary>
public sealed class Metrics
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _codes = new();
    public void Count(string code) => _codes.AddOrUpdate(code, 1, (_, n) => n + 1);
    public IEnumerable<KeyValuePair<string, long>> Snapshot() => _codes.Where(c => c.Key is not ("queue-full" or "conversion-timeout"));
}
