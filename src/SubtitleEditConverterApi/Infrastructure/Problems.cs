using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace SubtitleEditConverterApi.Infrastructure;

/// <summary>Renders <see cref="ApiException"/> (and anything unexpected) as RFC 7807 problem JSON (design §7.1).</summary>
public static partial class Problems
{
    [System.Text.RegularExpressions.GeneratedRegex("Failed to bind parameter \"[^\"]*? (?<name>\\w+)\" from \"(?<value>[^\"]*)\"")]
    private static partial System.Text.RegularExpressions.Regex BindFailure();

    private static string FirstSentence(string m) => m.Split(". ")[0].TrimEnd('.') + ".";

    /// <summary>System.Text.Json path ("$.profile.target") to a JSON pointer ("/profile/target").</summary>
    internal static string JsonPathToPointer(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "$") return "";
        var p = path.StartsWith("$") ? path[1..] : path;
        p = System.Text.RegularExpressions.Regex.Replace(p, @"\[(\d+)\]", "/$1");
        p = System.Text.RegularExpressions.Regex.Replace(p, @"\['([^']*)'\]", m => "/" + m.Groups[1].Value);
        return p.Replace('.', '/');
    }

    public const string TypeBase = "https://github.com/Wauter/subtitleedit-converter-api/blob/main/docs/errors.md#";

    public static JsonObject ToJson(ApiException ex, string requestId, bool verbose)
    {
        var body = new JsonObject
        {
            ["type"] = TypeBase + ex.Code,
            ["title"] = ex.Title,
            ["status"] = ex.Status,
            ["code"] = ex.Code,
            ["detail"] = ex.Message,
            ["hint"] = ex.Hint,
            ["requestId"] = requestId,
        };
        if (ex.Errors.Count > 0)
        {
            body["errors"] = JsonSerializer.SerializeToNode(ex.Errors, Json.Options);
        }
        foreach (var (k, v) in ex.Extensions)
        {
            body[k] = JsonSerializer.SerializeToNode(v, Json.Options);
        }
        if (verbose)
        {
            var debug = new JsonObject();
            foreach (var (k, v) in ex.Debug)
            {
                debug[k] = JsonSerializer.SerializeToNode(v, Json.Options);
            }
            var inner = ex.InnerException;
            if (inner != null)
            {
                debug["exception"] = new JsonObject
                {
                    ["type"] = inner.GetType().FullName,
                    ["message"] = inner.Message,
                    ["stackTrace"] = inner.StackTrace,
                };
            }
            if (debug.Count > 0)
            {
                body["debug"] = debug;
            }
        }
        return body;
    }

    public static ApiException TooLarge(ApiOptions opts, Exception? ex = null) =>
        new(413, ErrorCodes.PayloadTooLarge, "The request body is larger than the configured limit.",
            $"Max upload is {opts.MaxUploadBytes / 1024.0 / 1024.0:0.##} MB (MAX_UPLOAD_BYTES).", ex);

    public static async Task WriteAsync(HttpContext ctx, ApiException ex)
    {
        var opts = ctx.RequestServices.GetRequiredService<ApiOptions>();
        if (ctx.Response.HasStarted)
        {
            return;
        }
        ctx.Response.Clear();
        RequestIdMiddleware.ApplyHeaders(ctx);
        ctx.Response.StatusCode = ex.Status;
        if (ex.RetryAfterSeconds is { } ra)
        {
            ctx.Response.Headers.RetryAfter = ra.ToString();
        }
        if (ex.Code == ErrorCodes.Unauthorized) ctx.Response.Headers.WWWAuthenticate = "Bearer";
        ctx.Response.ContentType = "application/problem+json";
        ctx.Items["errorCode"] = ex.Code;
        var json = ToJson(ex, RequestIdMiddleware.Get(ctx), opts.ErrorsVerbose);
        await ctx.Response.WriteAsync(json.ToJsonString(Json.Options));
    }

    /// <summary>Maps framework exceptions (body too large, bad JSON, ...) to our stable codes.</summary>
    public static ApiException Translate(Exception ex, ApiOptions opts) => ex switch
    {
        ApiException api => api,
        BadHttpRequestException { StatusCode: 413 } or InvalidDataException => TooLarge(opts, ex),
        BadHttpRequestException { StatusCode: 415 } => new ApiException(415, ErrorCodes.UnsupportedMediaType, "This endpoint takes application/json.",
            "Send Content-Type: application/json.", ex),
        BadHttpRequestException { InnerException: JsonException je } => new ApiException(400, ErrorCodes.InvalidRequest,
            $"The JSON body is invalid{(je.Path is { } jp ? $" at '{jp}'" : "")}: {FirstSentence(je.Message)}",
            "Fix the field type or JSON syntax; see the endpoint schema in /openapi/v1.json.", ex)
        { Errors = [new FieldError(JsonPathToPointer(je.Path), FirstSentence(je.Message))] },
        BadHttpRequestException b when BindFailure().Match(b.Message) is { Success: true } m => new ApiException(400, ErrorCodes.InvalidRequest,
            $"Invalid value '{m.Groups["value"].Value}' for '{m.Groups["name"].Value}'.", "Check the parameter type in /openapi/v1.json (e.g. true/false for booleans).", ex)
        { Errors = [new FieldError("/query/" + m.Groups["name"].Value, $"Invalid value '{m.Groups["value"].Value}'.")] },
        BadHttpRequestException b when b.Message.Contains("Required parameter") => new ApiException(400, ErrorCodes.InvalidRequest,
            "The request body is missing or empty.", "Send a JSON object, e.g. {\"content\": \"...\"}.", ex),
        BadHttpRequestException b => new ApiException(400, ErrorCodes.InvalidRequest, "The request could not be read.",
            "Check the Content-Type and body framing.", ex),
        JsonException je => new ApiException(400, ErrorCodes.InvalidRequest, "The request body is not valid JSON.",
            $"Fix the JSON syntax{(je.Path is { } p ? $" near '{p}'" : "")} (line {je.LineNumber + 1}).", ex),
        OperationCanceledException => new ApiException(499, ErrorCodes.InvalidRequest, "The client closed the request.", "Retry the request.", ex),
        _ => new ApiException(500, ErrorCodes.InternalError, "An unexpected error occurred.",
            "Report this with the requestId; the server log has the same id.", ex),
    };
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>Catches every exception and renders problem JSON; also logs security-relevant failures.</summary>
public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> log)
{
    private static readonly HashSet<string> SecurityCodes =
    [
        ErrorCodes.Unauthorized, ErrorCodes.PayloadTooLarge, ErrorCodes.ZipLimitsExceeded, ErrorCodes.UnsafeZip,
        ErrorCodes.UnsafeXml, ErrorCodes.RateLimited, ErrorCodes.ConversionTimeout, ErrorCodes.InputLimitsExceeded,
    ];

    public async Task InvokeAsync(HttpContext ctx, ApiOptions opts)
    {
        try
        {
            await next(ctx);
        }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            if (ex is OperationCanceledException && ctx.RequestAborted.IsCancellationRequested)
            {
                ctx.Items["errorCode"] = "client-closed";
                return;
            }
            var api = Problems.Translate(ex, opts);
            if (api.Status >= 500 && api.Code == ErrorCodes.InternalError)
            {
                log.LogError(ex, "Unhandled error {code}", api.Code);
            }
            else if (SecurityCodes.Contains(api.Code))
            {
                log.LogWarning("Security event {code}: {detail}", api.Code, api.Message);
            }
            await Problems.WriteAsync(ctx, api);
        }
    }
}
