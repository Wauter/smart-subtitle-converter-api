using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Net.Http.Headers;
using SmartSubtitleConverterApi.Conversion;
using SmartSubtitleConverterApi.Jobs;
using SmartSubtitleConverterApi.Storage;
using SmartSubtitleConverterApi.Infrastructure;
using SmartSubtitleConverterApi.Profiles;

namespace SmartSubtitleConverterApi.Endpoints;

public sealed record ConvertJsonRequest(
    string? Content, string? ContentEncoding, string? FileName, string? Preset, JsonObject? Profile, string? Target,
    string? ResponseEncoding, string? Input = null, string? Output = null, bool Sidecar = false, string? IfExists = null);

/// <summary>One output. <c>verification</c>: the read-back check, binary targets only .</summary>
public sealed record OutputDto(string FileName, string Format, string FormatId, string MediaType, string? Content, string ContentEncoding, string Encoding, int Cues,
    string Kind = Formats.FormatKinds.Text, Verification? Verification = null, string? Output = null, int? Bytes = null);
public sealed record StatsDto(int ParagraphsIn, int ParagraphsOut, long ElapsedMs, long QueueWaitMs);
public sealed record ConvertJsonResponse(List<OutputDto> Outputs, DetectedInfo? Detected, StatsDto Stats, List<Warning> Warnings, JsonObject Profile, string? Id = null);

public static class ConvertEndpoints
{
    public static void Map(RouteGroupBuilder v1)
    {
        v1.MapGet("/jobs/{id}", (string id, HttpContext ctx, JobStore jobs) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var job = jobs.Get(id);
            if (job == null) throw JobNotFound();
            return Results.Json(job, Json.Options);
        }).WithName("getJob").WithTags("jobs").WithSummary("Get retained job metadata, never subtitle content.").Produces<JsonObject>().ProducesProblem(404);
        foreach (var which in new[] { "input", "output" })
        {
            var selected = which;
            v1.MapGet("/jobs/{id}/" + which, (string id, HttpContext ctx, JobStore jobs, StorageRouter storage, Formats.FormatRegistry formats) => GetJobFile(id, selected, ctx, jobs, storage, formats))
                .WithName(which == "input" ? "getJobInput" : "getJobOutput").WithTags("jobs")
                .WithSummary("Re-read the job's stored " + which + " location; never a cached file.")
                .WithDescription("Any valid API key knowing the id may fetch it. Current allowlists/size limits apply. Query-redacted HTTPS locations cannot be replayed. Files may have changed or disappeared.")
                .Produces(200, contentType: "application/octet-stream").ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(422).ProducesProblem(502);
        }
        v1.MapPost("/convert", ConvertMultipart)
            .WithName("convert").WithTags("convert")
            .WithSummary("Convert a subtitle file (multipart upload).")
            .WithDescription("Upload `file` with `preset`, `target` and/or `profile` (JSON overrides). Response headers report detected format/encoding, cue counts and warnings.")
            .WithBody(new() { ["multipart/form-data"] = OpenApiBodies.Multipart(OpenApiBodies.ConvertParts) })
            .Produces(200, contentType: "application/octet-stream").Produces(202)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(502).ProducesProblem(413).ProducesProblem(415).ProducesProblem(422).ProducesProblem(503).ProducesProblem(504)
            .DisableAntiforgery();

        v1.MapPost("/convert/json", ConvertJson)
            .WithName("convertJson").WithTags("convert")
            .WithSummary("Convert subtitle content sent as JSON (for agents).")
            .WithDescription("Send `content` as text (or base64 with contentEncoding=base64; binary formats such as EBU STL must be base64). Outputs come back as text for Unicode encodings, base64 otherwise; binary targets are always base64.")
            .Produces<ConvertJsonResponse>().Produces(202)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(502).ProducesProblem(413).ProducesProblem(422).ProducesProblem(503).ProducesProblem(504);
    }

    private static async Task<IResult> ConvertMultipart(HttpContext ctx, ProfileResolver resolver, ConversionQueue queue, ConversionEngine engine, ApiOptions opts, JobStore jobs, IHostApplicationLifetime lifetime, StorageRouter storage)
    {
        var (bytes, fileName, preset, profileJson, target, inputRaw, outputRaw, sidecar, ifExists) = await ReadMultipart(ctx, opts);
        var input = inputRaw == null ? null : StorageLocation.Parse(inputRaw);
        var output = outputRaw == null ? null : StorageLocation.Parse(outputRaw);
        fileName ??= input?.FileName;
        var overrides = ParseProfile(profileJson, opts);
        var (profile, resolved, presetName) = resolver.ResolveForConversion(preset, overrides, target);
        Track(ctx, profile, presetName, fileName);
        if (bytes != null) RejectZip(bytes, fileName);
        var (r, waitMs, id) = await RunJob(ctx, jobs, (jobId, ct) => ConvertWork(storage, bytes, fileName, input, output, sidecar, queue, engine, profile, resolved, jobId, ct, ifExists == "replace"), resolved, opts, lifetime.ApplicationStopping, inputRaw, outputRaw);
        if (r == null) return Accepted(ctx, jobs, id);
        ctx.Items["queueWaitMs"] = waitMs;
        ctx.Response.Headers.CacheControl = "no-store";
        if (presetName != null) ctx.Response.Headers["X-Profile"] = presetName;
        SetWarnings(ctx, r.Warnings);
        SetDetected(ctx, r);
        ctx.Response.Headers["X-Output-Kind"] = r.Outputs[0].Format.Kind;
        if (output != null || r.Outputs.Count != 1) return Results.Json(ToJson(r, resolved, waitMs, null, outputRaw) with { Id = id }, Json.Options);
        var o = r.Outputs[0];
        if (o.Verification is { } v) ctx.Response.Headers["X-Verification-Verdict"] = v.Verdict;
        ctx.Items["bytesOut"] = o.Bytes.Length;
        return File(ctx, o.Bytes, ContentType(o), o.FileName);
    }

    private static async Task<IResult> ConvertJson(HttpContext ctx, ConvertJsonRequest req, ProfileResolver resolver, ConversionQueue queue, ConversionEngine engine, ApiOptions opts, Formats.FormatRegistry formats, JobStore jobs, IHostApplicationLifetime lifetime, StorageRouter storage)
    {
        var ifExists = ParseIfExists(req.IfExists);
        if (req.Input != null && req.Content != null) throw new ApiException(422, ErrorCodes.InvalidRequest, "Use input or content, never both.", "Remove one source.");
        var input = req.Input == null ? null : StorageLocation.Parse(req.Input);
        var output = req.Output == null ? null : StorageLocation.Parse(req.Output);
        var fileName = req.FileName ?? input?.FileName;
        var bytes = input == null ? DecodeContent(req.Content, req.ContentEncoding) : null;
        if (bytes?.Length > opts.MaxUploadBytes) throw Problems.TooLarge(opts);
        if (req.Profile is { } p && Encoding.UTF8.GetByteCount(p.ToJsonString()) > opts.ProfileMaxBytes) throw ProfileTooLarge(opts);
        var (profile, resolved, presetName) = resolver.ResolveForConversion(req.Preset, req.Profile, req.Target);
        Track(ctx, profile, presetName, req.FileName);
        var isText = (req.ContentEncoding ?? "text").Equals("text", StringComparison.OrdinalIgnoreCase);
        if (input == null && isText && profile.Source?.Format is { } sf && formats.Find(sf) is { Kind: Formats.FormatKinds.Binary } bf)
        {
            throw new ApiException(422, ErrorCodes.InvalidBinaryInput, $"'{bf.Id}' is a binary format, but 'content' was sent as text.",
                "Send the raw file bytes base64-encoded with contentEncoding=base64 (text would be UTF-8 re-encoded and corrupted).")
            { Errors = [new("/contentEncoding", "Must be 'base64' for a binary source format.")] };
        }

        if (bytes != null) RejectZip(bytes, req.FileName);
        var (r, waitMs, id) = await RunJob(ctx, jobs, (jobId, ct) => ConvertWork(storage, bytes, fileName, input, output, req.Sidecar, queue, engine, profile, resolved, jobId, ct, ifExists == "replace"), resolved, opts, lifetime.ApplicationStopping, req.Input, req.Output);
        if (r == null) return Accepted(ctx, jobs, id);
        ctx.Items["queueWaitMs"] = waitMs;
        ctx.Response.Headers.CacheControl = "no-store";
        SetDetected(ctx, r);
        ctx.Response.Headers["X-Output-Kind"] = r.Outputs[0].Format.Kind;
        if (presetName != null) ctx.Response.Headers["X-Profile"] = presetName;
        return Results.Json(ToJson(r, resolved, waitMs, req.ResponseEncoding, req.Output) with { Id = id }, Json.Options);
    }

    private static ApiException JobNotFound() => new(404, ErrorCodes.JobNotFound, "Job is unknown or gone (expired, evicted or server restarted).", "Job history is in memory only. A missing record is not a failed conversion; inspect the original storage before resubmitting writes.");

    private static async Task<IResult> GetJobFile(string id, string which, HttpContext ctx, JobStore jobs, StorageRouter storage, Formats.FormatRegistry formats)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var job = jobs.Get(id) ?? throw JobNotFound();
        if (which == "output" && (string?)job["status"] == "running")
            throw new ApiException(409, ErrorCodes.JobNotReady, "The conversion is still running; its output is not ready.", "Poll GET /v1/jobs/{id} and retry once succeeded. Stored input may already be readable.");
        var location = (string?)job[which];
        var publicLocation = location?.StartsWith('/') == true ? new Uri(location).AbsoluteUri : location;
        if (location == null || which == "output" && (string?)job["status"] == "failed") throw new ApiException(409, which == "input" ? ErrorCodes.JobInputNotStored : ErrorCodes.JobOutputNotStored,
            which == "input"
                ? "The job has no stored input: its source was uploaded or sent inline, and those bytes are not kept."
                : (string?)job["status"] == "failed"
                    ? "The job has no stored output: the conversion failed, so nothing was written."
                    : "The job has no stored output: it was converted without an output location, so the result was returned in the convert response and not stored.",
            which == "input"
                ? "Use your original copy of the file, or convert from an input location to make the source re-readable."
                : (string?)job["status"] == "failed"
                    ? "Inspect the job's error (GET /v1/jobs/{id}) and the destination before retrying."
                    : "Use the result from the original response. To read it later, convert with an output location (file, S3 or HTTPS); response bytes are not kept.");
        if ((bool?)job[which + "Replayable"] != true)
            throw new ApiException(409, ErrorCodes.JobLocationNotReplayable, "This job location cannot be replayed because its HTTPS query was redacted.", "Use a fresh signed URL via input on a new request, or access storage directly. No signed query is retained by the job.") { Extensions = { ["location"] = publicLocation } };
        var parsed = StorageLocation.Parse(location);
        byte[] bytes;
        try { bytes = await storage.ReadAsync(parsed, ctx.RequestAborted); }
        catch (ApiException ex) when (ex.Code == "storage-not-found")
        { throw new ApiException(404, ErrorCodes.JobFileGone, "The file is no longer at the job's stored location.", "Job history is not a file archive. Inspect storage or the original result; do not blindly repeat writes.") { Extensions = { ["location"] = publicLocation } }; }
        var formatId = which == "input" ? (string?)job["result"]?["detected"]?["id"] : (string?)job["result"]?["outputs"]?.AsArray().FirstOrDefault()?["formatId"];
        var format = formats.Find(formatId ?? Path.GetExtension(parsed.FileName));
        ctx.Response.Headers["X-Job-Id"] = id;
        return File(ctx, bytes, format?.MediaType ?? "application/octet-stream", parsed.FileName);
    }

    private static string ParseIfExists(string? value) => value switch
    {
        null or "error" => "error", "replace" => "replace",
        _ => throw new ApiException(422, ErrorCodes.InvalidRequest, "Unknown ifExists policy.", "Valid ifExists values: error | replace. Archive and delete are not API operations.")
            { Errors = [new("/ifExists", "error | replace")] },
    };

    private sealed record Completed(ConversionResult? Result, long WaitMs, ApiException? Error = null);

    private static async Task<(ConversionResult? Result, long WaitMs, string Id)> RunJob(HttpContext ctx, JobStore jobs,
        Func<string, CancellationToken, Task<(ConversionResult Result, long QueueWaitMs)>> work, JsonObject resolved, ApiOptions opts,
        CancellationToken stopping, string? input = null, string? output = null)
    {
        var waitSeconds = ParseWait(ctx.Request.Headers["Prefer"].ToString(), opts.RequestWaitSeconds);
        var id = jobs.Create(input, output);
        ctx.Items["jobId"] = id;
        ctx.Response.Headers["X-Job-Id"] = id;
        ctx.Response.Headers.CacheControl = "no-store";
        if (ctx.Request.Headers.ContainsKey("Prefer")) ctx.Response.Headers["Preference-Applied"] = $"wait={waitSeconds}";
        // No HttpContext, request stream or request cancellation token is captured by the job.
        var task = CompleteJob(jobs, id, work, resolved, opts, stopping, output, RequestIdMiddleware.Get(ctx), ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ConversionJobs"));
        if (waitSeconds == 0) return (null, 0, id);
        Completed done;
        try { done = await task.WaitAsync(TimeSpan.FromSeconds(waitSeconds), ctx.RequestAborted); }
        catch (TimeoutException) { return (null, 0, id); }
        if (done.Error != null) throw done.Error;
        return (done.Result, done.WaitMs, id);
    }

    private static async Task<Completed> CompleteJob(JobStore jobs, string id,
        Func<string, CancellationToken, Task<(ConversionResult Result, long QueueWaitMs)>> work,
        JsonObject resolved, ApiOptions opts, CancellationToken stopping, string? output, string requestId, ILogger logger)
    {
        try
        {
            var (r, wait) = await work(id, stopping);
            var metadata = ToJson(r, resolved, wait, null, output, includeContent: false);
            metadata = metadata with { Outputs = metadata.Outputs.Select(o => o with { Content = null }).ToList(), Id = id };
            jobs.Finish(id, JsonSerializer.SerializeToNode(metadata, Json.Options)!.AsObject());
            return new(r, wait);
        }
        catch (Exception ex)
        {
            var problem = ex as ApiException ?? new ApiException(500, ErrorCodes.InternalError, "Conversion failed or was interrupted by server shutdown.", "See the server logs.");
            problem.Extensions["id"] = id;
            if (ex is not ApiException)
            {
                // Do not log exception messages: a transport may include a signed URL.
                logger.LogError("Job {jobId} request {requestId} failed: {exceptionType} {stackTrace}", id, requestId, ex.GetType().FullName, ex.StackTrace);
                problem.Debug["exceptionType"] = ex.GetType().FullName;
                problem.Debug["stackTrace"] = ex.StackTrace;
            }
            jobs.Finish(id, error: Problems.ToJson(problem, requestId, opts.ErrorsVerbose));
            return new(null, 0, problem); // observed even when the original HTTP request already returned 202
        }
    }

    internal static int ParseWait(string prefer, int defaultWait)
    {
        var values = prefer.Split(',').Select(p => p.Trim()).Where(p => p.StartsWith("wait", StringComparison.OrdinalIgnoreCase) && (p.Length == 4 || p[4] is '=' or ' ')).ToList();
        if (values.Count == 0) return defaultWait;
        var parts = values[0].Split('=', 2, StringSplitOptions.TrimEntries);
        if (values.Count != 1 || parts.Length != 2 || !int.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var wait) || wait is < 0 or > 60)
            throw new ApiException(400, ErrorCodes.InvalidRequest, "Prefer: wait must be one integer from 0 to 60 seconds.", "Use Prefer: wait=30, or wait=0 for an immediate job id.");
        return wait;
    }

    private static IResult Accepted(HttpContext ctx, JobStore jobs, string id)
    {
        ctx.Response.Headers.Location = $"/v1/jobs/{id}";
        ctx.Response.Headers.RetryAfter = "1";
        return Results.Json(new { id, status = "running", location = $"/v1/jobs/{id}" }, Json.Options, statusCode: 202);
    }

    internal static ConvertJsonResponse ToJson(ConversionResult r, JsonObject resolved, long waitMs, string? responseEncoding, string? output = null, bool includeContent = true)
    {
        var warnings = r.Warnings.ToList();
        var outputs = new List<OutputDto>();
        foreach (var o in r.Outputs)
        {
            var binary = o.Format.Kind == Formats.FormatKinds.Binary;
            var asText = !binary && o.Unicode && !string.Equals(responseEncoding, "base64", StringComparison.OrdinalIgnoreCase);
            if (binary && string.Equals(responseEncoding, "text", StringComparison.OrdinalIgnoreCase) && !warnings.Any(w => w.Code == "encoding-ignored"))
                warnings.Add(new("encoding-ignored", "responseEncoding=text is ignored for binary targets; their content is always base64."));
            // With a storage output, report the name the file was stored under, not the converter's generated name.
            outputs.Add(new(output != null ? StoredName(output) ?? o.FileName : o.FileName, o.Format.Name, o.Format.Id, o.Format.MediaType,
                output != null || !includeContent ? null : asText ? DecodeUnicode(o.Bytes) : Convert.ToBase64String(o.Bytes), asText ? "text" : "base64", o.EncodingName, o.Cues,
                Kind: o.Format.Kind, Verification: o.Verification, Output: JobStore.Redact(output), Bytes: o.Bytes.Length));
        }
        return new(outputs, r.Detected, new(r.ParagraphsIn, r.Outputs[0].Cues, r.ElapsedMs, waitMs), warnings, resolved);
    }

    /// <summary>Last path segment of an output location (bare path, file:, s3: or https: URI), decoded; null if it has none.</summary>
    internal static string? StoredName(string output)
    {
        string? name;
        if (output.Contains("://") && Uri.TryCreate(output, UriKind.Absolute, out var u)) name = Uri.UnescapeDataString(u.AbsolutePath.Split('/')[^1]);
        else name = Path.GetFileName(output);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    internal static void RejectZip(byte[] bytes, string? fileName)
    {
        if ((bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] is 3 or 5 or 7 && bytes[3] is 4 or 6 or 8)
            || string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(422, ErrorCodes.FormatNotSupported, "ZIP input is not supported. Send one subtitle per request.", "Extract the archive and submit a separate conversion for each subtitle.");
    }

    private static async Task<(byte[]? Bytes, string? FileName, string? Preset, string? Profile, string? Target, string? Input, string? Output, bool Sidecar, string IfExists)> ReadMultipart(HttpContext ctx, ApiOptions opts)
    {
        if (!ctx.Request.HasFormContentType || !(ctx.Request.ContentType ?? "").StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(415, ErrorCodes.UnsupportedMediaType, "Use multipart/form-data here.", "Send file or input, or use POST /v1/convert/json.");
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var file = form.Files.GetFile("file");
        var input = NullIfEmpty(form["input"]);
        if ((file == null) == (input == null)) throw new ApiException(400, ErrorCodes.InvalidRequest, "Provide exactly one of multipart file or input.", "Send one subtitle per request.");
        byte[]? bytes = null;
        if (file != null)
        {
            if (file.Length > opts.MaxUploadBytes) throw Problems.TooLarge(opts);
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ctx.RequestAborted); bytes = ms.ToArray();
        }
        var sidecar = false;
        if (form.ContainsKey("sidecar") && !bool.TryParse(form["sidecar"], out sidecar))
            throw new ApiException(400, ErrorCodes.InvalidRequest, "sidecar must be true or false.", "Use sidecar=true to write a JSON companion file.");
        return (bytes, file?.FileName, NullIfEmpty(form["preset"]), NullIfEmpty(form["profile"]), NullIfEmpty(form["target"]), input, NullIfEmpty(form["output"]), sidecar, ParseIfExists(form.ContainsKey("ifExists") ? form["ifExists"].ToString() : null));
    }

    private static async Task<(ConversionResult Result, long QueueWaitMs)> ConvertWork(StorageRouter storage, byte[]? uploaded, string? fileName,
        StorageLocation? input, StorageLocation? output, bool sidecar, ConversionQueue queue, ConversionEngine engine, Profile profile,
        JsonObject resolved, string id, CancellationToken ct, bool replace)
    {
        if (output != null && (profile.Target?.Additional?.Count ?? 0) != 0)
            throw new ApiException(422, ErrorCodes.InvalidRequest, "An explicit output names one file, but this profile has additional targets.", "Remove target.additional, or omit output to receive multiple results inline.");
        var bytes = input == null ? uploaded! : await storage.ReadAsync(input, ct);
        RejectZip(bytes, fileName);
        var (r, wait) = await queue.RunAsync(() => engine.Convert(bytes, fileName, profile), ct);
        if (output != null)
        {
            var o = r.Outputs.Single();
            await storage.WriteAsync(output, o.Bytes, o.Format.MediaType, ct, replace);
            if (!sidecar && output.Scheme != "https")
            {
                // The subtitle at this location is new, so a companion {output}.json left from an earlier job describes a file
                // that no longer exists: delete it (Wauter, 2026-11-06). Never fails the conversion.
                try
                {
                    await storage.DeleteAsync(StorageLocation.Parse(output.Value + ".json"), ct);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    r.Warnings.Add(new("sidecar-delete-failed", "The subtitle was written, but an older JSON sidecar next to it ({output}.json) could not be deleted; it describes a previous file.",
                        Hint: "Delete or ignore the stale sidecar (check permissions on that path); the subtitle was kept. Or send sidecar:true to replace it."));
                }
            }
            if (sidecar)
            {
                if (output.Scheme == "https") r.Warnings.Add(new("sidecar-skipped", "HTTPS output names one object; no second signed sidecar URL can be derived."));
                else
                {
                    try
                    {
                        var metadata = ToJson(r, resolved, wait, null, output.Value) with { Id = id };
                        await storage.WriteAsync(StorageLocation.Parse(output.Value + ".json"), JsonSerializer.SerializeToUtf8Bytes(metadata, Json.Options), "application/json", ct, replace);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        r.Warnings.Add(new("sidecar-write-failed", "The subtitle was written but its JSON sidecar could not be written.", Hint: "Check the sidecar path, permissions, limits and existing files; the subtitle was kept."));
                    }
                }
            }
        }
        return (r, wait);
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

    internal static IResult File(HttpContext ctx, byte[] bytes, string contentType, string fileName)
    {
        var cd = new ContentDispositionHeaderValue("attachment");
        cd.SetHttpFileName(fileName); // filename (ASCII fallback) + filename* (RFC 6266) for non-ASCII
        ctx.Response.Headers.ContentDisposition = cd.ToString();
        return Results.Bytes(bytes, contentType);
    }

    private static T Also<T>(this T obj, Action<T> a) { a(obj); return obj; }
}
