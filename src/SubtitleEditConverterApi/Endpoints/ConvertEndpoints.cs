using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Net.Http.Headers;
using SubtitleEditConverterApi.Conversion;
using SubtitleEditConverterApi.Infrastructure;
using SubtitleEditConverterApi.Profiles;

namespace SubtitleEditConverterApi.Endpoints;

public sealed record ConvertJsonRequest(
    string Content, string? ContentEncoding, string? FileName, string? Preset, JsonObject? Profile, string? Target,
    string? ResponseEncoding, bool? FailFast);

/// <summary>One output. <c>verification</c>: the read-back check, binary targets only (1.2.0).</summary>
public sealed record OutputDto(string FileName, string Format, string FormatId, string MediaType, string Content, string ContentEncoding, string Encoding, int Cues, string? Source = null,
    string Kind = Formats.FormatKinds.Text, Verification? Verification = null);
public sealed record StatsDto(int ParagraphsIn, int ParagraphsOut, long ElapsedMs, long QueueWaitMs);
/// <summary>One zip entry. <c>verification</c>: read-back checks of its binary outputs, keyed by output path (1.2.0).</summary>
public sealed record ManifestEntry(string Source, string Status, string[] Outputs, DetectedInfo? Detected, List<Warning> Warnings, JsonObject? Error,
    string? Kind = null, string? CodePage = null, string? Reason = null, Dictionary<string, Verification>? Verification = null);
public sealed record ConvertJsonResponse(List<OutputDto> Outputs, DetectedInfo? Detected, StatsDto Stats, List<Warning> Warnings, JsonObject Profile, List<ManifestEntry>? Entries);

public sealed record ConvertForm(IFormFile File, string? Preset, string? Profile, string? Target, bool? FailFast);

/// <summary>A batch: one or many inputs (zip) converted with one profile, in one queue job.</summary>
internal sealed record BatchItem(string Name, ConversionResult? Result, ApiException? Error, string? SkipReason = null);
internal sealed record Batch(bool IsZip, List<BatchItem> Items, List<Warning> Warnings);

public static class ConvertEndpoints
{
    public static void Map(RouteGroupBuilder v1)
    {
        v1.MapPost("/convert", ConvertMultipart)
            .WithName("convert").WithTags("convert")
            .WithSummary("Convert a subtitle file (multipart upload).")
            .WithDescription("Upload `file` with `preset`, `target` and/or `profile` (JSON overrides). A zip upload converts every entry and returns a zip with manifest.json. Response headers report detected format/encoding, cue counts and warnings.")
            .WithBody(new() { ["multipart/form-data"] = OpenApiBodies.Multipart(OpenApiBodies.ConvertParts) })
            .Produces(200, contentType: "application/octet-stream")
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(413).ProducesProblem(415).ProducesProblem(422).ProducesProblem(503).ProducesProblem(504)
            .DisableAntiforgery();

        v1.MapPost("/convert/json", ConvertJson)
            .WithName("convertJson").WithTags("convert")
            .WithSummary("Convert subtitle content sent as JSON (for agents).")
            .WithDescription("Send `content` as text (or base64 with contentEncoding=base64; binary formats such as EBU STL must be base64). Outputs come back as text for Unicode encodings, base64 otherwise; binary targets are always base64.")
            .Produces<ConvertJsonResponse>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(413).ProducesProblem(422).ProducesProblem(503).ProducesProblem(504);
    }

    private static async Task<IResult> ConvertMultipart(HttpContext ctx, ProfileResolver resolver, ConversionQueue queue, ConversionEngine engine, ApiOptions opts)
    {
        var (bytes, fileName, preset, profileJson, target, failFast) = await ReadMultipart(ctx, opts);
        var overrides = ParseProfile(profileJson, opts);
        var (profile, resolved, presetName) = resolver.ResolveForConversion(preset, overrides, target);
        Track(ctx, profile, presetName, fileName);

        var (batch, waitMs) = await queue.RunAsync(() => RunBatch(engine, opts, bytes, fileName, profile, failFast), ctx.RequestAborted);
        ctx.Items["queueWaitMs"] = waitMs;

        var h = ctx.Response.Headers;
        h.CacheControl = "no-store";
        if (presetName != null) h["X-Profile"] = presetName;
        var allWarnings = batch.Warnings.Concat(batch.Items.SelectMany(i => i.Result?.Warnings ?? [])).ToList();
        SetWarnings(ctx, allWarnings);

        if (!batch.IsZip)
        {
            var r = batch.Items[0].Result!;
            SetDetected(ctx, r);
            h["X-Output-Kind"] = r.Outputs[0].Format.Kind == Formats.FormatKinds.Binary ? "binary" : "text";
            if (r.Outputs.Count == 1)
            {
                var o = r.Outputs[0];
                h["X-Cue-Count"] = o.Cues.ToString();
                if (o.Verification is { } v) h["X-Verification-Verdict"] = v.Verdict;
                ctx.Items["bytesOut"] = o.Bytes.Length;
                return File(ctx, o.Bytes, ContentType(o), o.FileName);
            }
            h["X-Cue-Count"] = r.Outputs[0].Cues.ToString();
            var manifest = new { outputs = r.Outputs.Select(o => new { o.FileName, format = o.Format.Id, kind = o.Format.Kind, o.EncodingName, o.Cues, o.Verification }), detected = r.Detected, warnings = r.Warnings, profile = resolved };
            BatchNames.CheckOutputs(r.Outputs.Select(o => (o.FileName, fileName ?? "subtitle")));
            var zip = ZipIO.Write(r.Outputs.Select(o => (o.FileName, o.Bytes)), manifest);
            return File(ctx, zip, "application/zip", ConversionEngine.SafeStem(fileName) + ".zip");
        }

        var entries = Manifest(batch, opts);
        h["X-Entries-Ok"] = entries.Count(e => e.Status == "ok").ToString();
        h["X-Entries-Failed"] = entries.Count(e => e.Status == "error").ToString();
        h["X-Entries-Skipped"] = entries.Count(e => e.Status == "skipped").ToString();
        var files = batch.Items.Where(i => i.Result != null)
            .SelectMany(i => i.Result!.Outputs.Select(o => (o.FileName, o.Bytes)));
        var zipOut = ZipIO.Write(files, new { entries, warnings = batch.Warnings, profile = resolved });
        return File(ctx, zipOut, "application/zip", ConversionEngine.SafeStem(fileName) + ".zip");
    }

    private static async Task<IResult> ConvertJson(HttpContext ctx, ConvertJsonRequest req, ProfileResolver resolver, ConversionQueue queue, ConversionEngine engine, ApiOptions opts, Formats.FormatRegistry formats)
    {
        var bytes = DecodeContent(req.Content, req.ContentEncoding);
        if (bytes.Length > opts.MaxUploadBytes) throw Problems.TooLarge(opts);
        if (req.Profile is { } p && Encoding.UTF8.GetByteCount(p.ToJsonString()) > opts.ProfileMaxBytes) throw ProfileTooLarge(opts);
        var (profile, resolved, presetName) = resolver.ResolveForConversion(req.Preset, req.Profile, req.Target);
        Track(ctx, profile, presetName, req.FileName);
        var isText = (req.ContentEncoding ?? "text").Equals("text", StringComparison.OrdinalIgnoreCase);
        if (isText && profile.Source?.Format is { } sf && formats.Find(sf) is { Kind: Formats.FormatKinds.Binary } bf)
        {
            throw new ApiException(422, ErrorCodes.InvalidBinaryInput, $"'{bf.Id}' is a binary format, but 'content' was sent as text.",
                "Send the raw file bytes base64-encoded with contentEncoding=base64 (text would be UTF-8 re-encoded and corrupted).")
            { Errors = [new("/contentEncoding", "Must be 'base64' for a binary source format.")] };
        }

        var (batch, waitMs) = await queue.RunAsync(() => RunBatch(engine, opts, bytes, req.FileName, profile, req.FailFast ?? false, "/content"), ctx.RequestAborted);
        ctx.Items["queueWaitMs"] = waitMs;
        ctx.Response.Headers.CacheControl = "no-store";
        var forceBase64 = string.Equals(req.ResponseEncoding, "base64", StringComparison.OrdinalIgnoreCase);

        var forceText = string.Equals(req.ResponseEncoding, "text", StringComparison.OrdinalIgnoreCase);
        var extra = new List<Warning>();
        var outputs = new List<OutputDto>();
        foreach (var (name, result, _, _) in batch.Items.Where(i => i.Result != null))
        {
            foreach (var o in result!.Outputs)
            {
                var binary = o.Format.Kind == Formats.FormatKinds.Binary;
                var asText = !binary && o.Unicode && !forceBase64;
                if (binary && forceText && extra.Count == 0)
                    extra.Add(new("encoding-ignored", "responseEncoding=text is ignored for binary targets; their content is always base64."));
                outputs.Add(new OutputDto(o.FileName, o.Format.Name, o.Format.Id, o.Format.MediaType,
                    asText ? DecodeUnicode(o.Bytes) : Convert.ToBase64String(o.Bytes), asText ? "text" : "base64", o.EncodingName, o.Cues,
                    batch.IsZip ? name : null, o.Format.Kind, o.Verification));
            }
        }
        var single = batch.IsZip ? null : batch.Items[0].Result;
        if (single != null) SetDetected(ctx, single);
        var warnings = batch.Warnings.Concat(batch.Items.SelectMany(i => i.Result?.Warnings ?? [])).Concat(extra).ToList();
        if (single != null) ctx.Response.Headers["X-Output-Kind"] = single.Outputs[0].Format.Kind == Formats.FormatKinds.Binary ? "binary" : "text";
        var stats = new StatsDto(batch.Items.Sum(i => i.Result?.ParagraphsIn ?? 0), batch.Items.Sum(i => i.Result?.Outputs.FirstOrDefault()?.Cues ?? 0),
            batch.Items.Sum(i => i.Result?.ElapsedMs ?? 0), waitMs);
        if (presetName != null) ctx.Response.Headers["X-Profile"] = presetName;
        return Results.Json(new ConvertJsonResponse(outputs, single?.Detected, stats, warnings, resolved, batch.IsZip ? Manifest(batch, opts) : null), Json.Options);
    }

    // ---------------- shared helpers ----------------

    internal static Batch RunBatch(ConversionEngine engine, ApiOptions opts, byte[] bytes, string? fileName, Profile profile, bool failFast, string pointerBase = "/file")
    {
        var warnings = new List<Warning>();
        if (!ZipIO.IsZip(bytes))
        {
            return new Batch(false, [new BatchItem(fileName ?? "subtitle", engine.Convert(bytes, fileName, profile), null)], warnings);
        }
        var entries = ZipIO.Read(bytes, opts, warnings, pointerBase);
        BatchNames.CheckEntries(entries.Where(e => e.SkipReason == null).Select(e => e.Name)); // before any conversion, whatever failFast says
        var items = new List<BatchItem>();
        var processed = 0; // <i> in pointers counts processed (non-skipped) entries
        foreach (var e in entries)
        {
            if (e.SkipReason != null)
            {
                items.Add(new BatchItem(e.Name, null, null, e.SkipReason));
                continue;
            }
            var i = processed++;
            try
            {
                items.Add(new BatchItem(e.Name, engine.Convert(e.Bytes, e.Name, profile), null));
            }
            catch (ApiException ex) when (ex.Code is not (ErrorCodes.InvalidProfile or ErrorCodes.InvalidRequest))
            {
                if (failFast)
                {
                    var stop = new ApiException(ex.Status, ex.Code, $"Zip entry {i} ('{e.Name}') failed: {ex.Message}", ex.Hint + " (failFast=true stops at the first failing entry.)", ex.InnerException)
                    { Errors = [new FieldError($"{pointerBase}/entries/{i}", ex.Message, ex.Hint)] };
                    stop.Extensions["entry"] = e.Name;
                    foreach (var (k, v) in ex.Debug) stop.Debug[k] = v;
                    throw stop;
                }
                ex.Extensions["entry"] = e.Name;
                items.Add(new BatchItem(e.Name, null, ex));
            }
        }
        return new Batch(true, FinalNames(items), warnings);
    }

    /// <summary>
    /// Final output paths for a zip batch (entry folder + output file name), decided once so the
    /// manifest, JSON outputs[] and the zip agree. Collisions fail the request; nothing is renamed.
    /// </summary>
    private static List<BatchItem> FinalNames(List<BatchItem> items)
    {
        var named = items.Select(item => item.Result == null ? item
            : item with { Result = item.Result with { Outputs = item.Result.Outputs.Select(o => o with { FileName = EntryOutputName(item.Name, o.FileName) }).ToList() } }).ToList();
        BatchNames.CheckOutputs(named.Where(i => i.Result != null).SelectMany(i => i.Result!.Outputs.Select(o => (o.FileName, i.Name))));
        return named;
    }

    private static List<ManifestEntry> Manifest(Batch batch, ApiOptions opts) =>
        batch.Items.Select(i => new ManifestEntry(i.Name, i.SkipReason != null ? "skipped" : i.Error == null ? "ok" : "error",
            i.Result?.Outputs.Select(o => o.FileName).ToArray() ?? [],
            i.Result?.Detected, i.Result?.Warnings ?? [],
            i.Error == null ? null : Problems.ToJson(i.Error, "", opts.ErrorsVerbose).Also(j => j.Remove("requestId")),
            i.Result?.Detected.Kind, i.Result?.Detected.CodePage, i.SkipReason,
            i.Result?.Outputs.Where(o => o.Verification != null).ToDictionary(o => o.FileName, o => o.Verification!) is { Count: > 0 } v ? v : null)).ToList();

    private static string EntryOutputName(string entryName, string outputName)
    {
        var dir = Path.GetDirectoryName(entryName)?.Replace('\\', '/');
        return string.IsNullOrEmpty(dir) ? outputName : $"{dir}/{outputName}";
    }

    private static async Task<(byte[] Bytes, string? FileName, string? Preset, string? Profile, string? Target, bool FailFast)> ReadMultipart(HttpContext ctx, ApiOptions opts)
    {
        if (!ctx.Request.HasFormContentType || !(ctx.Request.ContentType ?? "").StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(415, ErrorCodes.UnsupportedMediaType, $"Content-Type '{ctx.Request.ContentType}' is not supported here.",
                "Use multipart/form-data (curl -F file=@movie.srt -F target=webvtt), or POST /v1/convert/json.");
        }
        if (ctx.Request.ContentLength > opts.MaxUploadBytes + 128 * 1024) throw Problems.TooLarge(opts);
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file == null)
        {
            throw new ApiException(400, ErrorCodes.InvalidRequest, "Missing the 'file' part.", "Send the subtitle as a multipart part named 'file' (curl -F file=@movie.srt).")
            { Errors = [new FieldError("/file", "Required.")] };
        }
        if (file.Length > opts.MaxUploadBytes) throw Problems.TooLarge(opts);
        var ms = new MemoryStream((int)file.Length);
        await file.CopyToAsync(ms, ctx.RequestAborted);
        var failFastRaw = form["failFast"].ToString();
        return (ms.ToArray(), file.FileName, NullIfEmpty(form["preset"]), NullIfEmpty(form["profile"]), NullIfEmpty(form["target"]),
            failFastRaw.Equals("true", StringComparison.OrdinalIgnoreCase) || failFastRaw == "1");
    }

    internal static ApiException ProfileTooLarge(ApiOptions opts) =>
        new(413, ErrorCodes.PayloadTooLarge, $"The profile is larger than {opts.ProfileMaxBytes} bytes.", "Keep profiles small; use a mounted preset for big replace lists (PROFILE_MAX_BYTES sets the limit).");

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    internal static JsonObject? ParseProfile(string? json, ApiOptions opts)
    {
        if (json == null) return null;
        if (Encoding.UTF8.GetByteCount(json) > opts.ProfileMaxBytes) throw ProfileTooLarge(opts);
        return ProfileJson.ParseObject(json, "The 'profile' part");
    }

    internal static byte[] DecodeContent(string? content, string? encoding)
    {
        if (content == null) throw new ApiException(400, ErrorCodes.InvalidRequest, "Missing 'content'.", "Send the subtitle in 'content' (text, or base64 with contentEncoding=base64).") { Errors = [new("/content", "Required.")] };
        switch ((encoding ?? "text").ToLowerInvariant())
        {
            case "text": return new UTF8Encoding(false).GetBytes(content);
            case "base64":
                try { return Convert.FromBase64String(content); }
                catch (FormatException ex) { throw new ApiException(400, ErrorCodes.InvalidRequest, "'content' is not valid base64.", "Encode the raw file bytes with standard base64.", ex) { Errors = [new("/content", "Invalid base64.")] }; }
            default:
                throw new ApiException(400, ErrorCodes.InvalidRequest, $"Unknown contentEncoding '{encoding}'.", "Use 'text' or 'base64'.") { Errors = [new("/contentEncoding", "text | base64")] };
        }
    }

    private static string DecodeUnicode(byte[] bytes)
    {
        using var r = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    private static void Track(HttpContext ctx, Profile p, string? preset, string? fileName)
    {
        ctx.Items["preset"] = preset;
        ctx.Items["operations"] = string.Join(",", (p.Operations ?? []).Select(o => o.Id));
        ctx.Items["fileHash"] = RequestIdMiddleware.HashFileName(fileName);
    }

    private static void SetDetected(HttpContext ctx, ConversionResult r)
    {
        var h = ctx.Response.Headers;
        h["X-Detected-Format"] = r.Detected.Id;
        h["X-Detected-Encoding"] = r.Detected.Encoding;
        if (r.Detected.CodePage is { } cp) h["X-Detected-Code-Page"] = cp;
        h["X-Cue-Count-In"] = r.ParagraphsIn.ToString();
        if (!h.ContainsKey("X-Cue-Count")) h["X-Cue-Count"] = r.Outputs.FirstOrDefault()?.Cues.ToString() ?? "0";
        ctx.Items["format"] = r.Detected.Id;
        ctx.Items["encoding"] = r.Detected.Encoding;
        ctx.Items["cues"] = r.ParagraphsIn;
    }

    private static void SetWarnings(HttpContext ctx, List<Warning> warnings)
    {
        ctx.Response.Headers["X-Warnings-Count"] = warnings.Count.ToString();
        if (warnings.Count == 0) return;
        // code, message and count only: hint and details (cue numbers, code points) stay in the JSON body.
        var json = JsonSerializer.Serialize(warnings.Select(w => new { w.Code, w.Message, w.Count }), Json.Options);
        if (json.Length > 4096) json = json[..4090] + "...]";
        // Header values must be ASCII; escape anything else.
        ctx.Response.Headers["X-Warnings"] = new string(json.Select(c => c < 32 || c > 126 ? '?' : c).ToArray());
    }

    private static string ContentType(ConvertedOutput o) =>
        o.Format.MediaType == "text/plain" || o.Format.MediaType.StartsWith("text/")
            ? $"{o.Format.MediaType}; charset={CharsetName(o.EncodingName)}"
            : o.Format.MediaType;

    private static string CharsetName(string enc) => enc.StartsWith("utf-8") ? "utf-8" : enc;

    private static IResult File(HttpContext ctx, byte[] bytes, string contentType, string fileName)
    {
        var cd = new ContentDispositionHeaderValue("attachment");
        cd.SetHttpFileName(fileName); // filename (ASCII fallback) + filename* (RFC 6266) for non-ASCII
        ctx.Response.Headers.ContentDisposition = cd.ToString();
        return Results.Bytes(bytes, contentType);
    }

    private static T Also<T>(this T obj, Action<T> a) { a(obj); return obj; }
}
