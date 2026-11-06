using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SubtitleEditConverterApi.Endpoints;

/// <summary>
/// Request bodies for endpoints that read the body themselves. `.Accepts()` would also act as a
/// routing constraint (a wrong Content-Type would 404 instead of our 415), so we describe the
/// body in OpenAPI only.
/// </summary>
public static class OpenApiBodies
{
    private static OpenApiSchema Str(string description, string? format = null) =>
        new() { Type = JsonSchemaType.String, Description = description, Format = format };

    private static OpenApiSchema Part(string key, string description) => key == "ifExists"
        ? new() { Type = JsonSchemaType.String, Description = description, Enum = ["error", "replace"], Default = System.Text.Json.Nodes.JsonValue.Create("error") }
        : Str(description);

    public static OpenApiMediaType Multipart(Dictionary<string, string> textParts) => new()
    {
        Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Required = new HashSet<string>(),
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["file"] = Str("One subtitle file.", "binary"),
            }.Concat(textParts.Select(p => KeyValuePair.Create(p.Key, (IOpenApiSchema)Part(p.Key, p.Value)))).ToDictionary(),
        },
    };

    public static RouteHandlerBuilder WithBody(this RouteHandlerBuilder b, Dictionary<string, OpenApiMediaType> content) =>
        b.AddOpenApiOperationTransformer((op, _, _) =>
        {
            op.RequestBody = new OpenApiRequestBody { Required = true, Content = content };
            return Task.CompletedTask;
        });

    public static readonly Dictionary<string, string> ConvertParts = new()
    {
        ["ifExists"] = "error (default) | replace. Filesystem/S3 reject existing outputs by default; HTTPS follows signer/receiver policy.",
        ["input"] = "Storage URI; mutually exclusive with file.",
        ["output"] = "Exact output URI; omit to receive converted bytes.",
        ["sidecar"] = "true | false (default false), write output.json beside output.",
        ["preset"] = "Preset name (GET /v1/profiles).",
        ["target"] = "Target format id/alias; shorthand for profile.target.format.",
        ["profile"] = "Profile JSON (full profile or overrides on top of preset). Schema: GET /v1/schemas/profile.json.",
    };

    public static OpenApiMediaType JsonObject(Dictionary<string, string> props, params string[] required) => new()
    {
        Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Required = required.ToHashSet(),
            Properties = props.ToDictionary(p => p.Key, p => (IOpenApiSchema)Part(p.Key, p.Value)),
        },
    };
}
