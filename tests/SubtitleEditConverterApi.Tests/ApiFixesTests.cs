using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SubtitleEditConverterApi.Conversion;
using SubtitleEditConverterApi.Endpoints;
using SubtitleEditConverterApi.Infrastructure;

namespace SubtitleEditConverterApi.Tests;

/// <summary>API fixes A1–A11 from the MCP spec answers.</summary>
public class ApiFixesTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _c = f.CreateClient();
    private static readonly byte[] Srt = Encoding.UTF8.GetBytes(Samples.Srt);

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SubtitleEditConverterApi.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    private async Task<JsonNode> Spec() => JsonNode.Parse(await _c.GetStringAsync("/openapi/v1.json"))!;

    [Fact] // A3
    public async Task Every_error_code_is_in_errors_md_and_the_openapi_enum()
    {
        var md = File.ReadAllText(RepoFile("docs/errors.md"));
        var spec = await Spec();
        var enumValues = spec["components"]!["schemas"]!["ApiProblem"]!["properties"]!["code"]!["enum"]!.AsArray().Select(v => (string)v!).ToHashSet();
        Assert.NotEmpty(OpenApiContract.AllCodes);
        foreach (var code in OpenApiContract.AllCodes)
        {
            Assert.Contains($"<a id=\"{code}\"></a>`{code}`", md);
            Assert.Contains(code, enumValues);
        }
        Assert.Equal(OpenApiContract.AllCodes.Length, enumValues.Count);
    }

    [Fact] // A1, A2, A4
    public async Task Spec_declares_problems_security_headers_and_optional_fields()
    {
        var spec = await Spec();
        var schemas = spec["components"]!["schemas"]!;
        Assert.Null(schemas["ProblemDetails"]);
        Assert.Null(schemas["ManifestEntry"]);
        Assert.True(schemas["ConvertJsonRequest"]!["required"] == null || schemas["ConvertJsonRequest"]!["required"]!.AsArray().Count == 0);
        Assert.Equal("bearer", (string)spec["components"]!["securitySchemes"]!["bearer"]!["scheme"]!);
        foreach (var (path, item) in spec["paths"]!.AsObject())
        foreach (var (method, op) in item!.AsObject())
        {
            var responses = op!["responses"]!.AsObject();
            foreach (var (status, resp) in responses)
            {
                if (int.Parse(status) >= 400)
                    Assert.Equal("#/components/schemas/ApiProblem", (string)resp!["content"]!["application/problem+json"]!["schema"]!["$ref"]!);
            }
            Assert.Contains(op["parameters"]!.AsArray(), p => (string)p!["name"]! == "X-Request-Id" && (string)p["in"]! == "header");
            if (!path.StartsWith("/v1")) continue;
            Assert.True(responses.ContainsKey("401"), $"{method} {path} lacks 401");
            Assert.True(responses.ContainsKey("429"), $"{method} {path} lacks 429");
            Assert.Equal(2, op["security"]!.AsArray().Count);
        }
        var convert = spec["paths"]!["/v1/convert"]!["post"]!["responses"]!;
        Assert.NotNull(convert["503"]!["headers"]!["Retry-After"]);
        Assert.NotNull(convert["504"]);
        Assert.NotNull(convert["200"]!["headers"]!["X-Detected-Code-Page"]);
        Assert.NotNull(spec["paths"]!["/v1/formats"]!["get"]!["responses"]!["400"]);
    }

    [Fact] // A5
    public async Task Binding_failures_are_problem_json()
    {
        var r = await _c.PostAsync("/v1/convert/json", new StringContent("""{"content":"x","target":"srt","responseEncoding":42}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var p = await r.Json();
        Assert.Equal("invalid-request", (string)p["code"]!);
        Assert.Equal("/responseEncoding", (string)p["errors"]![0]!["pointer"]!);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);

        r = await _c.PostAsync("/v1/convert/json", new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal("invalid-request", (string)(await r.Json())["code"]!);

        r = await _c.PostAsync("/v1/convert/json", new StringContent("""{"content":"x"}""", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.StatusCode);
        Assert.Equal("unsupported-media-type", (string)(await r.Json())["code"]!);

        r = await _c.GetAsync("/v1/formats?textOnly=maybe");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        p = await r.Json();
        Assert.Equal("invalid-request", (string)p["code"]!);
        Assert.Equal("/query/textOnly", (string)p["errors"]![0]!["pointer"]!);
    }

    [Fact] // A11
    public async Task Timeout_names_the_phase()
    {
        using var tf = ApiFactory.With(new() { ["CONVERSION_TIMEOUT_SECONDS"] = "1" });
        var q = tf.Services.GetRequiredService<ConversionQueue>();
        using var stuck = new ManualResetEventSlim();
        var running = q.RunAsync(() => { stuck.Wait(); return "never"; }, default);
        var queued = q.RunAsync(() => "late", default);
        var ex1 = await Assert.ThrowsAsync<ApiException>(() => running);
        Assert.Equal("running", ex1.Extensions["phase"]);
        try
        {
            await queued;
        }
        catch (ApiException ex2)
        {
            Assert.Equal("queue", ex2.Extensions["phase"]);
        }
        stuck.Set();
    }
}
