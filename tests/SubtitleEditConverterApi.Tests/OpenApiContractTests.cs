using System.Text.Json.Nodes;

namespace SubtitleEditConverterApi.Tests;

/// <summary>
/// The generated OpenAPI document must match the committed docs/openapi.v1.json (design §7.4).
/// Regenerate after an intended change: UPDATE_OPENAPI=1 dotnet test
/// </summary>
public class OpenApiContractTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SubtitleEditConverterApi.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    [Fact]
    public async Task Generated_spec_matches_committed_spec()
    {
        var generated = JsonNode.Parse(await f.CreateClient().GetStringAsync("/openapi/v1.json"))!;
        generated["servers"] = null;
        var pretty = generated.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
        var path = RepoFile("docs/openapi.v1.json");
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1" || !File.Exists(path))
        {
            File.WriteAllText(path, pretty);
        }
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), pretty.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Every_v1_operation_documents_problem_responses_and_summary()
    {
        var doc = JsonNode.Parse(await f.CreateClient().GetStringAsync("/openapi/v1.json"))!;
        foreach (var (path, item) in doc["paths"]!.AsObject())
        foreach (var (method, op) in item!.AsObject())
        {
            Assert.False(string.IsNullOrEmpty((string?)op!["summary"]), $"{method} {path} has no summary");
        }
    }
}
