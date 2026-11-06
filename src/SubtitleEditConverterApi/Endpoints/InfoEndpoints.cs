using System.Text.Json.Nodes;
using SubtitleEditConverterApi.Conversion;
using SubtitleEditConverterApi.Jobs;
using SubtitleEditConverterApi.Storage;
using SubtitleEditConverterApi.Formats;
using SubtitleEditConverterApi.Infrastructure;
using SubtitleEditConverterApi.Profiles;

namespace SubtitleEditConverterApi.Endpoints;

public sealed record DetectJsonRequest(string? Content, string? ContentEncoding, string? FileName, string? Encoding, string? Input = null);
public sealed record PresetSummary(string Name, string Origin, string? Path, bool Valid, string? Description, string? Target, DateTimeOffset LoadedAt, string? Shadows, List<FieldError>? Errors, List<FieldError>? Warnings);
public sealed record PresetDetail(string Name, string Origin, string? Path, bool Valid, string? Description, string? Target, DateTimeOffset LoadedAt, string? Shadows, List<FieldError> Errors, List<FieldError> Warnings, JsonObject? Raw, JsonObject? Resolved);
public sealed record ProfilesResponse(List<PresetSummary> Profiles, string? ProfilesDir, string? MountError);
public sealed record ValidateResponse(bool Valid, List<FieldError> Errors, List<FieldError> Warnings, JsonObject? Resolved);

public sealed record LimitsBodyBytes(long Multipart, long Json);
public sealed record LimitsJson(long MaxDecodedContentBytes, string Base64);
public sealed record LimitsQueue(int Capacity, int Depth);
public sealed record LimitsAuth(bool Required);
public sealed record LimitsBinary(long EbuStlMaxBytes);
public sealed record LimitsIo(int TimeoutSeconds, int Retries);
public sealed record LimitsJobs(int MaxEntries, int TtlSeconds, int MaxActive, int MaxMetadataBytes);
public sealed record LimitsStorage(string[] InputSchemes, string[] OutputSchemes);
public sealed record LimitsResponse(long MaxUploadBytes, LimitsBodyBytes MaxRequestBodyBytes, LimitsJson Json, int MaxProfileBytes, int MaxCues, int MaxLineChars,
    int ConversionTimeoutSeconds, LimitsQueue Queue, int RateLimitPerMinute, LimitsAuth Auth, LimitsBinary Binary, int RequestWaitSeconds, LimitsIo Io, LimitsJobs Jobs, LimitsStorage Storage)
{
    /// <summary>libse's EBU IsMine rejects files of 2,048,000 bytes or more.</summary>
    public const long EbuStlMaxBytes = 2_047_999;

    private static string[] Schemes(ApiOptions o) => new[] {
        o.StorageFileRoots.Length > 0 && OperatingSystem.IsLinux() ? "file" : null,
        o.HttpAllowedHosts.Length > 0 ? "https" : null,
        o.S3AllowedBuckets.Length > 0 ? "s3" : null,
    }.OfType<string>().ToArray();

    public static LimitsResponse From(ApiOptions o, ConversionQueue q, ApiKeyStore keys) =>
        new(o.MaxUploadBytes, new LimitsBodyBytes(o.MaxMultipartBodyBytes, o.MaxJsonBodyBytes),
            new LimitsJson(o.MaxUploadBytes, "encoded length = 4*ceil(n/3); already included in maxRequestBodyBytes.json"),
            o.ProfileMaxBytes, o.MaxCues, o.MaxLineChars,
            o.ConversionTimeoutSeconds, new LimitsQueue(q.Capacity, q.Depth), o.RateLimitPerMinute, new LimitsAuth(keys.Enabled), new LimitsBinary(EbuStlMaxBytes),
            o.RequestWaitSeconds, new(o.IoTimeoutSeconds, o.IoRetries),
            new(o.JobMaxEntries, o.JobTtlSeconds, Math.Min(o.JobMaxEntries, o.QueueCapacity + 1), JobStore.MaxMetadataBytes),
            new(Schemes(o), Schemes(o)));
}

public static class InfoEndpoints
{
    public static void Map(RouteGroupBuilder v1)
    {
        v1.MapGet("/formats", (FormatRegistry reg, string? q, bool? textOnly, string? kind, bool? writable) =>
        {
            IEnumerable<FormatInfo> list = reg.All;
            if (!string.IsNullOrWhiteSpace(kind))
            {
                var kinds = kind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(k => k.ToLowerInvariant()).ToHashSet();
                var bad = kinds.Where(k => !FormatKinds.All.Contains(k)).ToList();
                if (bad.Count > 0)
                    throw new ApiException(400, ErrorCodes.InvalidRequest, $"Unknown kind '{bad[0]}'.", $"Use a comma list of: {string.Join(", ", FormatKinds.All)}.")
                    { Errors = [new("/query/kind", $"Unknown kind '{bad[0]}'.")] };
                list = list.Where(f => kinds.Contains(f.Kind)); // kind wins over textOnly
            }
            else if (textOnly ?? true) list = list.Where(f => f.Kind == FormatKinds.Text);
            else list = list.Where(f => f.Kind is FormatKinds.Text or FormatKinds.Binary); // image/container only via kind=
            if (writable == true) list = list.Where(f => f.CanWrite);
            if (!string.IsNullOrWhiteSpace(q))
                list = list.Where(f => f.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                       || f.Aliases.Any(a => a.Equals(q, StringComparison.OrdinalIgnoreCase)) || f.Extension.Equals(q.StartsWith('.') ? q : "." + q, StringComparison.OrdinalIgnoreCase));
            return Results.Json(list.ToList(), Json.Options);
        }).WithName("listFormats").WithTags("formats")
          .WithSummary("List subtitle formats.")
          .WithDescription("Use `id` or an alias as source/target format. `textOnly` defaults to true (text formats only, as in earlier versions). `kind` is a comma list of text|binary|image|container and wins over `textOnly`; image/container formats are listed only via `kind` and can't be converted. `writable=true` keeps formats that can be a target.")
          .Produces<List<FormatInfo>>().ProducesProblem(400);

        v1.MapGet("/limits", (HttpContext ctx, ApiOptions opts, ConversionQueue queue, ApiKeyStore keys) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(LimitsResponse.From(opts, queue, keys), Json.Options);
        }).WithName("getLimits").WithTags("info")
          .WithSummary("Effective upload, queue, wait, I/O, job and storage-scheme limits.")
          .WithDescription("Lets clients pre-check inputs. `queue.depth` is a live value. `auth.required` says whether a bearer key is needed; keys are never exposed.")
          .Produces<LimitsResponse>();

        v1.MapPost("/detect", Detect).WithName("detect").WithTags("formats")
          .WithSummary("Detect the format and encoding of a subtitle.")
          .WithDescription("Send multipart `file`, or JSON {content, contentEncoding, fileName} (binary files as base64). Or send `input` (file, S3 or HTTPS URI) instead of inline content/file. Returns `kind`, `codePage` for binary input and up to 5 `candidates`. 422 format-not-detected when nothing matches.")
          .WithBody(new()
          {
              ["multipart/form-data"] = OpenApiBodies.Multipart(new() { ["input"] = "Storage URI, mutually exclusive with file.", ["encoding"] = "Optional: force the input encoding." }),
              ["application/json"] = OpenApiBodies.JsonObject(new()
              {
                  ["input"] = "Storage URI, mutually exclusive with content.", ["content"] = "Subtitle content (text, or base64).", ["contentEncoding"] = "text (default) | base64",
                  ["fileName"] = "Optional file name (helps detection).", ["encoding"] = "Optional: force the input encoding.",
              }),
          })
          .Produces<DetectedInfo>().ProducesProblem(400).ProducesProblem(415).ProducesProblem(422)
          .DisableAntiforgery();

        v1.MapGet("/profiles", (PresetStore store, ApiOptions opts) =>
        {
            var list = store.List().Select(e => new PresetSummary(e.Name, e.Origin, e.Path, e.Valid, e.Description, e.Target, e.LoadedAt, e.Shadows,
                e.Errors.Count > 0 ? e.Errors : null, e.Warnings.Count > 0 ? e.Warnings : null)).ToList();
            return Results.Json(new ProfilesResponse(list, opts.ProfilesDir, store.MountError), Json.Options);
        }).WithName("listProfiles").WithTags("profiles")
          .WithSummary("List built-in and mounted presets, including invalid ones with their errors.")
          .Produces<ProfilesResponse>();

        v1.MapGet("/profiles/{name}", (string name, PresetStore store) =>
        {
            var e = store.Get(name);
            if (e == null)
            {
                var names = store.List().Select(p => p.Name).ToList();
                var s = Text.Suggest(name, names);
                throw new ApiException(404, ErrorCodes.PresetNotFound, $"Unknown preset '{name}'.",
                    (s != null ? $"Did you mean '{s}'? " : "") + $"Known presets: {string.Join(", ", names)}. See GET /v1/profiles.");
            }
            return Results.Json(new PresetDetail(e.Name, e.Origin, e.Path, e.Valid, e.Description, e.Target, e.LoadedAt, e.Shadows, e.Errors, e.Warnings, e.Raw, e.Resolved), Json.Options);
        }).WithName("getProfile").WithTags("profiles")
          .WithSummary("Get one preset: raw JSON, resolved (extends flattened) profile and errors.")
          .Produces<PresetDetail>().ProducesProblem(404);

        v1.MapPost("/profiles/validate", async (HttpContext ctx, ProfileResolver resolver, ApiOptions opts) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            if (body.Length > opts.ProfileMaxBytes)
                throw ConvertEndpoints.ProfileTooLarge(opts);
            var obj = ProfileJson.ParseObject(body, "The request body");
            ResolveResult r;
            try
            {
                r = resolver.ValidateStandalone(obj);
            }
            catch (ApiException ex) when (ex.Code == ErrorCodes.PresetNotFound)
            {
                return Results.Json(new ValidateResponse(false, [.. ex.Errors.Select(e => e with { Hint = ex.Hint })], [], null), Json.Options);
            }
            return Results.Json(new ValidateResponse(r.Errors.Count == 0, r.Errors, r.Warnings, r.Resolved), Json.Options);
        }).WithName("validateProfile").WithTags("profiles")
          .WithSummary("Validate a profile without converting.")
          .WithDescription("Always 200 for a JSON object: `valid`, `errors` (JSON pointer + hint), `warnings` and the resolved profile. 400 only for unparseable JSON.")
          .WithBody(new() { ["application/json"] = new() { Schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.Object, Description = "A profile. JSON Schema: GET /v1/schemas/profile.json" } } })
          .Produces<ValidateResponse>().ProducesProblem(400);

        v1.MapGet("/schemas/profile.json", (HttpContext ctx) =>
            Results.Json(ProfileSpec.ToJsonSchema($"{ctx.Request.Scheme}://{ctx.Request.Host}/v1/schemas/profile.json"), Json.Options, "application/schema+json"))
          .WithName("profileSchema").WithTags("profiles")
          .WithSummary("JSON Schema (2020-12) for profiles.");
    }

    private static async Task<IResult> Detect(HttpContext ctx, ConversionQueue queue, ConversionEngine engine, ApiOptions opts, StorageRouter storage)
    {
        byte[] bytes;
        string? fileName;
        string? encoding = null;
        if (ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.GetFile("file");
            var inputRaw = form["input"].ToString();
            if ((file == null) == string.IsNullOrEmpty(inputRaw)) throw new ApiException(400, ErrorCodes.InvalidRequest, "Provide exactly one of file or input.", "Send one subtitle.");
            if (file != null)
            {
                if (file.Length > opts.MaxUploadBytes) throw Problems.TooLarge(opts);
                using var ms = new MemoryStream(); await file.CopyToAsync(ms, ctx.RequestAborted);
                bytes = ms.ToArray(); fileName = file.FileName;
            }
            else
            {
                var input = StorageLocation.Parse(inputRaw);
                bytes = await storage.ReadAsync(input, ctx.RequestAborted); fileName = input.FileName;
            }
            encoding = form["encoding"].ToString() is { Length: > 0 } e ? e : null;
        }
        else if ((ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            var req = await ctx.Request.ReadFromJsonAsync<DetectJsonRequest>(Json.Options, ctx.RequestAborted)
                      ?? throw new ApiException(400, ErrorCodes.InvalidRequest, "Empty JSON body.", "Send {\"content\": \"...\"}.");
            if (req.Input != null && req.Content != null) throw new ApiException(422, ErrorCodes.InvalidRequest, "Use input or content, never both.", "Remove one source.");
            var input = req.Input == null ? null : StorageLocation.Parse(req.Input);
            bytes = input == null ? ConvertEndpoints.DecodeContent(req.Content, req.ContentEncoding) : await storage.ReadAsync(input, ctx.RequestAborted);
            fileName = req.FileName ?? input?.FileName;
            encoding = req.Encoding;
        }
        else
        {
            throw new ApiException(415, ErrorCodes.UnsupportedMediaType, $"Content-Type '{ctx.Request.ContentType}' is not supported.", "Use multipart/form-data with a 'file' part, or application/json.");
        }
        if (bytes.Length > opts.MaxUploadBytes) throw Problems.TooLarge(opts);
        ConvertEndpoints.RejectZip(bytes, fileName);
        var (info, wait) = await queue.RunAsync(() => engine.Detect(bytes, fileName, encoding), ctx.RequestAborted);
        ctx.Items["queueWaitMs"] = wait;
        ctx.Items["format"] = info.Id;
        return Results.Json(info, Json.Options);
    }
}
