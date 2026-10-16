using System.Text.Json.Nodes;
using SubtitleEditApi.Conversion;
using SubtitleEditApi.Formats;
using SubtitleEditApi.Infrastructure;
using SubtitleEditApi.Profiles;

namespace SubtitleEditApi.Endpoints;

public sealed record DetectJsonRequest(string Content, string? ContentEncoding, string? FileName, string? Encoding);
public sealed record PresetSummary(string Name, string Origin, string? Path, bool Valid, string? Description, string? Target, DateTimeOffset LoadedAt, string? Shadows, List<FieldError>? Errors, List<FieldError>? Warnings);
public sealed record PresetDetail(string Name, string Origin, string? Path, bool Valid, string? Description, string? Target, DateTimeOffset LoadedAt, string? Shadows, List<FieldError> Errors, List<FieldError> Warnings, JsonObject? Raw, JsonObject? Resolved);
public sealed record ProfilesResponse(List<PresetSummary> Profiles, string? ProfilesDir, string? MountError);
public sealed record ValidateResponse(bool Valid, List<FieldError> Errors, List<FieldError> Warnings, JsonObject? Resolved);

public static class InfoEndpoints
{
    public static void Map(RouteGroupBuilder v1)
    {
        v1.MapGet("/formats", (FormatRegistry reg, string? q, bool? textOnly) =>
        {
            IEnumerable<FormatInfo> list = reg.All;
            if (textOnly ?? true) list = list.Where(f => f.TextBased);
            if (!string.IsNullOrWhiteSpace(q))
                list = list.Where(f => f.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                       || f.Aliases.Any(a => a.Equals(q, StringComparison.OrdinalIgnoreCase)) || f.Extension.Equals(q.StartsWith('.') ? q : "." + q, StringComparison.OrdinalIgnoreCase));
            return Results.Json(list.ToList(), Json.Options);
        }).WithName("listFormats").WithTags("formats")
          .WithSummary("List subtitle formats.")
          .WithDescription("Use `id` or an alias as source/target format. `textOnly` defaults to true; V1 can only convert text formats.")
          .Produces<List<FormatInfo>>();

        v1.MapPost("/detect", Detect).WithName("detect").WithTags("formats")
          .WithSummary("Detect the format and encoding of a subtitle.")
          .WithDescription("Send multipart `file`, or JSON {content, contentEncoding, fileName}. 422 format-not-detected when nothing matches.")
          .WithBody(new()
          {
              ["multipart/form-data"] = OpenApiBodies.Multipart(new() { ["encoding"] = "Optional: force the input encoding." }),
              ["application/json"] = OpenApiBodies.JsonObject(new()
              {
                  ["content"] = "Subtitle content (text, or base64).", ["contentEncoding"] = "text (default) | base64",
                  ["fileName"] = "Optional file name (helps detection).", ["encoding"] = "Optional: force the input encoding.",
              }, "content"),
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
                throw new ApiException(413, ErrorCodes.PayloadTooLarge, "The profile is larger than 64 KB.", "Keep profiles small.");
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

    private static async Task<IResult> Detect(HttpContext ctx, ConversionQueue queue, ConversionEngine engine, ApiOptions opts)
    {
        byte[] bytes;
        string? fileName;
        string? encoding = null;
        if (ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.GetFile("file") ?? throw new ApiException(400, ErrorCodes.InvalidRequest, "Missing the 'file' part.", "Send the subtitle as a multipart part named 'file'.");
            if (file.Length > opts.MaxUploadBytes) throw Problems.TooLarge(opts);
            var ms = new MemoryStream();
            await file.CopyToAsync(ms, ctx.RequestAborted);
            bytes = ms.ToArray();
            fileName = file.FileName;
            encoding = form["encoding"].ToString() is { Length: > 0 } e ? e : null;
        }
        else if ((ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            var req = await ctx.Request.ReadFromJsonAsync<DetectJsonRequest>(Json.Options, ctx.RequestAborted)
                      ?? throw new ApiException(400, ErrorCodes.InvalidRequest, "Empty JSON body.", "Send {\"content\": \"...\"}.");
            bytes = ConvertEndpoints.DecodeContent(req.Content, req.ContentEncoding);
            fileName = req.FileName;
            encoding = req.Encoding;
        }
        else
        {
            throw new ApiException(415, ErrorCodes.UnsupportedMediaType, $"Content-Type '{ctx.Request.ContentType}' is not supported.", "Use multipart/form-data with a 'file' part, or application/json.");
        }
        if (bytes.Length > opts.MaxUploadBytes) throw Problems.TooLarge(opts);
        if (ZipIO.IsZip(bytes))
            throw new ApiException(422, ErrorCodes.FormatNotSupported, "Detect takes a single subtitle, not a zip.", "Unzip first, or use POST /v1/convert which reports detection per entry in manifest.json.");
        var (info, wait) = await queue.RunAsync(() => engine.Detect(bytes, fileName, encoding), ctx.RequestAborted);
        ctx.Items["queueWaitMs"] = wait;
        ctx.Items["format"] = info.Id;
        return Results.Json(info, Json.Options);
    }
}
