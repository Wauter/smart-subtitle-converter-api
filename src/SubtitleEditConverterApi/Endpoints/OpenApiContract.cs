using System.Reflection;
using Microsoft.OpenApi;
using SubtitleEditConverterApi.Infrastructure;

namespace SubtitleEditConverterApi.Endpoints;

/// <summary>
/// Document-wide OpenAPI contract (MCP spec answers, fixes A1/A2/A4): one ApiProblem schema for every
/// error response, optional bearer auth on /v1, the responses every /v1 operation can return
/// (401, 429, 503/504 for queued work, 400 for GETs with parameters), documented response headers
/// and the X-Request-Id request header.
/// </summary>
public static class OpenApiContract
{
    /// <summary>Every stable error code (ErrorCodes constants), for the ApiProblem.code enum and the errors.md contract test.</summary>
    public static readonly string[] AllCodes = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToArray();

    private static readonly HashSet<string> QueuedOperations = ["convert", "convertJson", "detect"];
    private static readonly HashSet<string> ConvertOperations = ["convert", "convertJson"];

    private static OpenApiSchema Str(string? description = null) => new() { Type = JsonSchemaType.String, Description = description };

    public static Task Apply(OpenApiDocument doc)
    {
        doc.Components ??= new OpenApiComponents();
        doc.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        var schemas = doc.Components.Schemas;

        schemas["FieldError"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "A problem located by a JSON pointer (profile field, query parameter, ...).",
            Required = new HashSet<string> { "pointer", "message" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["pointer"] = Str("JSON pointer, e.g. /target/format."), ["message"] = Str(), ["hint"] = Str(),
            },
        };
        schemas["ApiProblem"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "RFC 7807 problem details with a stable `code` (docs/errors.md). Extra members may appear: `phase` (conversion-timeout: queue|running), `candidates` (ambiguous-format, ambiguous-encoding), `probe` (ambiguous-encoding: pac-code-page|text-encoding).",
            Required = new HashSet<string> { "type", "title", "status", "code", "detail", "hint" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["type"] = Str("URL of the code's section in docs/errors.md."),
                ["title"] = Str(),
                ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["code"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = AllCodes.Select(c => (System.Text.Json.Nodes.JsonNode)c!).ToList() },
                ["detail"] = Str(),
                ["hint"] = Str("What to do about it."),
                ["requestId"] = Str("Same as the X-Request-Id header."),
                ["errors"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchemaReference("FieldError", doc) },
                ["debug"] = new OpenApiSchema { Type = JsonSchemaType.Object, Description = "Diagnostics, only when ERRORS_VERBOSE=true. Never contains file content." },
                ["phase"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = ["queue", "running"], Description = "conversion-timeout only: queue is retryable like 503." },
                ["candidates"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array, Description = "ambiguous-format: the format ids the bytes match; set profile.source.format to one of them. ambiguous-encoding: the PAC code pages or text encodings that remain possible (may be empty); set formatOptions.pac.inputCodePage or encoding.input.",
                    Items = new OpenApiSchema { Type = JsonSchemaType.String },
                },
                ["probe"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = ["pac-code-page", "text-encoding"], Description = "ambiguous-encoding only: which probe could not decide." },
            },
            AdditionalPropertiesAllowed = true,
        };
        schemas.Remove("ProblemDetails");

        foreach (var name in new[] { "ConvertJsonRequest", "DetectJsonRequest" })
        {
            if (schemas.TryGetValue(name, out var rs) && rs is OpenApiSchema r) r.Required = new HashSet<string>();
        }

        if (schemas.TryGetValue("ConvertJsonRequest", out var convertSchema) && convertSchema is OpenApiSchema convert && convert.Properties != null)
            convert.Properties["ifExists"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = ["error", "replace"], Default = System.Text.Json.Nodes.JsonValue.Create("error"), Description = "Default error: exclusive filesystem create or conditional S3 PUT. replace explicitly permits replacement. HTTPS follows signer/receiver policy." };

        doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        doc.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer",
            Description = "API key as a bearer token. Required only when the server has API_KEYS/API_KEYS_FILE set (GET /v1/limits → auth.required).",
        };

        foreach (var (path, item) in doc.Paths)
        {
            if (item.Operations == null) continue;
            foreach (var (method, op) in item.Operations)
            {
                op.Responses ??= new OpenApiResponses();
                // ApiProblem everywhere an error can come back.
                foreach (var (status, resp) in op.Responses.ToList())
                {
                    if (int.TryParse(status, out var s) && s >= 400) op.Responses[status] = Problem(doc, resp.Description ?? Reason(s), s);
                }
                op.Parameters ??= [];
                op.Parameters.Add(new OpenApiParameter
                {
                    Name = "X-Request-Id", In = ParameterLocation.Header, Required = false,
                    Description = "Optional client request id, echoed back; replaced by a fresh ULID when it doesn't match the pattern.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[A-Za-z0-9._-]{1,64}$" },
                });
                foreach (var resp in op.Responses.Values.OfType<OpenApiResponse>())
                {
                    resp.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    resp.Headers["X-Request-Id"] = Header("Request id (also in problem.requestId and the logs).");
                }
                if (!path.StartsWith("/v1", StringComparison.Ordinal)) continue;

                op.Security = [new OpenApiSecurityRequirement(), new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", doc)] = [] }];
                op.Responses.TryAdd("401", Problem(doc, "Unauthorized: API keys are configured and the bearer key is missing or wrong.", 401));
                op.Responses.TryAdd("429", Problem(doc, "Rate limited (only when RATE_LIMIT_PER_MINUTE > 0).", 429, retryAfter: true));
                var id = op.OperationId ?? "";
                if (QueuedOperations.Contains(id))
                {
                    op.Responses["503"] = Problem(doc, "queue-full: retry after Retry-After seconds.", 503, retryAfter: true);
                    op.Responses["504"] = Problem(doc, "conversion-timeout; `phase` says whether it timed out in the queue (retryable) or while running.", 504);
                }
                if (method == HttpMethod.Get && op.Parameters.Any(p => p.In is ParameterLocation.Query or ParameterLocation.Path))
                    op.Responses.TryAdd("400", Problem(doc, "invalid-request: a parameter has an invalid value.", 400));
                if (ConvertOperations.Contains(id))
                {
                    op.Parameters.Add(new OpenApiParameter { Name = "Prefer", In = ParameterLocation.Header, Description = "wait=N, from 0 to 60 seconds; default configured wait is 30. wait=0 returns 202 immediately.", Schema = Str() });
                    op.Responses["202"] = new OpenApiResponse { Description = "Still running; poll Location. Jobs retain metadata, not subtitle bytes.",
                        Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.Object } } },
                        Headers = new Dictionary<string, IOpenApiHeader> { ["X-Job-Id"] = Header("Job id."), ["Location"] = Header("Relative job URL."), ["Retry-After"] = Header("Seconds before polling.") } };
                }
                if (ConvertOperations.Contains(id) && op.Responses.TryGetValue("200", out var ok) && ok is OpenApiResponse okr)
                {
                    okr.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    foreach (var (h, d) in ConvertHeaders) okr.Headers[h] = Header(d);
                }
            }
        }
        return Task.CompletedTask;
    }

    private static readonly (string, string)[] ConvertHeaders =
    [
        ("X-Job-Id", "Job id; GET /v1/jobs/{id} retrieves metadata while retained."),
        ("X-Detected-Format", "Detected (or forced) source format id; single input only."),
        ("X-Detected-Encoding", "Detected text encoding, or `binary` for binary input."),
        ("X-Detected-Code-Page", "Binary input only: EBU GSI code page (e.g. 850) or pac-<name>."),
        ("X-Output-Kind", "text | binary (first output)."),
        ("X-Cue-Count", "Cues in the (first) output."),
        ("X-Cue-Count-In", "Cues read from the input."),
        ("X-Warnings-Count", "Number of warnings."),
        ("X-Warnings", "Warnings as compact JSON with code, message and count only (ASCII, truncated to 4 KB); hint and details are in the JSON body."),
        ("X-Verification-Verdict", "Single binary output only: read-back verdict lossless | quantised | lossy | unverified (details in the JSON response's outputs[].verification)."),
        ("X-Profile", "Preset name, when one was used."),
    ];

    private static OpenApiHeader Header(string description) => new() { Description = description, Schema = new OpenApiSchema { Type = JsonSchemaType.String } };

    private static OpenApiResponse Problem(OpenApiDocument doc, string description, int status, bool retryAfter = false)
    {
        var r = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference("ApiProblem", doc) } },
            Headers = new Dictionary<string, IOpenApiHeader> { ["X-Request-Id"] = Header("Request id (also in problem.requestId and the logs).") },
        };
        if (retryAfter || status == 503) r.Headers["Retry-After"] = new OpenApiHeader { Description = "Seconds to wait before retrying.", Schema = new OpenApiSchema { Type = JsonSchemaType.Integer } };
        return r;
    }

    private static string Reason(int s) => s switch
    {
        400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 413 => "Payload Too Large", 415 => "Unsupported Media Type",
        422 => "Unprocessable Entity", 429 => "Too Many Requests", 503 => "Service Unavailable", 504 => "Gateway Timeout", _ => "Error",
    };
}
