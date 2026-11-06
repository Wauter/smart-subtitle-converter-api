using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SmartSubtitleConverterApi.Formats;
using SmartSubtitleConverterApi.Infrastructure;
using SmartSubtitleConverterApi.Profiles;

namespace SmartSubtitleConverterApi.Tests;

public class ProfileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("se-profiles-").FullName;
    private readonly ApiFactory _factory;

    public ProfileTests() => _factory = ApiFactory.With(new() { ["PROFILES_DIR"] = _dir });

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_dir, true);
    }

    private PresetStore Store => _factory.Services.GetRequiredService<PresetStore>();
    private ProfileResolver Resolver => _factory.Services.GetRequiredService<ProfileResolver>();
    private static JsonObject J(string s) => (JsonObject)JsonNode.Parse(s)!;

    [Fact]
    public void Merge_objects_deep_merge_arrays_replace_null_clears()
    {
        var merged = ProfileJson.Merge(
            J("""{"rules":{"maxLines":2,"maxCps":20},"operations":[{"id":"A"},{"id":"B"}],"timing":{"offsetMs":5}}"""),
            J("""{"rules":{"maxLines":3},"operations":[{"id":"C"}],"timing":null}"""));
        Assert.Equal(3, (int)merged["rules"]!["maxLines"]!);
        Assert.Equal(20, (int)merged["rules"]!["maxCps"]!);
        Assert.Single(merged["operations"]!.AsArray());
        Assert.False(merged.ContainsKey("timing"));
    }

    [Fact]
    public void Builtin_presets_all_load_and_validate()
    {
        var builtins = Store.List().Where(p => p.Origin == "builtin").ToList();
        Assert.True(builtins.Count >= 5);
        Assert.All(builtins, p => Assert.True(p.Valid, $"{p.Name}: {string.Join("; ", p.Errors.Select(e => e.Pointer + " " + e.Message))}"));
        Assert.All(builtins, p => Assert.NotNull(p.Target));
    }

    [Fact]
    public void Unknown_keys_and_bad_values_report_pointers_and_hints()
    {
        var r = Resolver.ValidateStandalone(J("""
            {"target":{"format":"vtt2"},"timing":{"ofsetMs":1},"source":{"fps":0},
             "operations":[{"id":"SplitLong"},{"id":"DeleteFirst"},{"id":"FixCommonErrors","rules":["FixCommonOcrErrors"]}]}
            """));
        var byPointer = r.Errors.ToDictionary(e => e.Pointer);
        Assert.Contains("webvtt", byPointer["/target/format"].Hint);
        Assert.Contains("offsetMs", byPointer["/timing/ofsetMs"].Hint);
        Assert.Contains("23.976", byPointer["/source/fps"].Hint);
        Assert.Contains("SplitLongLines", byPointer["/operations/0/id"].Hint);
        Assert.True(byPointer.ContainsKey("/operations/1/count"));
        Assert.Contains("V1", byPointer["/operations/2/rules/0"].Message);
    }

    [Theory]
    [InlineData("srt", "subrip")]
    [InlineData("VTT", "webvtt")]
    [InlineData("ass", "advancedsubstationalpha")]
    [InlineData("Advanced Sub Station Alpha", "advancedsubstationalpha")]
    [InlineData("ttml", "timedtext10")]
    [InlineData("sbv", "youtubesbv")]
    public void Format_aliases_normalise(string alias, string id) =>
        Assert.Equal(id, _factory.Services.GetRequiredService<FormatRegistry>().Find(alias)?.Id);

    [Fact]
    public void Request_overrides_win_over_preset_and_target_shorthand_wins()
    {
        var (p, _, name) = Resolver.ResolveForConversion("netflix-en-vtt", J("""{"rules":{"maxLineLength":37}}"""), "srt");
        Assert.Equal("netflix-en-vtt", name);
        Assert.Equal(37, p.Rules!.MaxLineLength);
        Assert.Equal(2, p.Rules.MaxLines); // inherited
        Assert.Equal("srt", p.Target!.Format);
        Assert.Equal(4, p.Operations!.Count); // netflix replaced clean-srt's list wholesale
    }

    [Fact]
    public void Missing_target_and_unknown_preset_give_stable_codes()
    {
        var ex = Assert.Throws<ApiException>(() => Resolver.ResolveForConversion(null, J("""{"rules":{"maxLines":2}}"""), null));
        Assert.Equal(ErrorCodes.InvalidRequest, ex.Code);
        var nf = Assert.Throws<ApiException>(() => Resolver.ResolveForConversion("netflx", null, null));
        Assert.Equal(404, nf.Status);
        Assert.Equal(ErrorCodes.PresetNotFound, nf.Code);
        Assert.Contains("netflix-en-vtt", nf.Hint);
    }

    [Fact]
    public void Mounted_folder_valid_invalid_shadowing_nonjson_cycles_and_hot_reload()
    {
        File.WriteAllText(Path.Combine(_dir, "mine.json"), """{"extends":"srt-to-vtt","rules":{"maxLineLength":30}}""");
        File.WriteAllText(Path.Combine(_dir, "broken.json"), """{"target":{"format":"webvtt"},"operations":[{"id":"Nope"}]}""");
        File.WriteAllText(Path.Combine(_dir, "garbage.json"), "{ not json");
        File.WriteAllText(Path.Combine(_dir, "clean-srt.json"), """{"description":"override","target":{"format":"webvtt"}}""");
        File.WriteAllText(Path.Combine(_dir, "loop-a.json"), """{"extends":"loop-b"}""");
        File.WriteAllText(Path.Combine(_dir, "loop-b.json"), """{"extends":"loop-a"}""");
        File.WriteAllText(Path.Combine(_dir, "Bad Name.json"), """{"target":{"format":"srt"}}""");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "ignored");

        var list = Store.List().ToDictionary(p => p.Name);
        Assert.True(list["mine"].Valid);
        Assert.Equal("webvtt", list["mine"].Target);
        Assert.Equal(30, (int)list["mine"].Resolved!["rules"]!["maxLineLength"]!);
        Assert.False(list["broken"].Valid);
        Assert.Equal("/operations/0/id", list["broken"].Errors[0].Pointer);
        Assert.False(list["garbage"].Valid);
        Assert.Equal("mounted", list["clean-srt"].Origin);
        Assert.Equal("builtin://clean-srt.json", list["clean-srt"].Shadows);
        Assert.Contains(list["loop-a"].Errors, e => e.Message.Contains("Cycle"));
        Assert.False(list["Bad Name"].Valid);
        Assert.DoesNotContain("notes", list.Keys);

        // hot reload: edit a file, and new files appear without restart
        File.WriteAllText(Path.Combine(_dir, "broken.json"), """{"target":{"format":"webvtt"}}""");
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "broken.json"), DateTime.UtcNow.AddSeconds(5));
        File.WriteAllText(Path.Combine(_dir, "fresh.json"), """{"target":{"format":"srt"}}""");
        var reloaded = Store.List().ToDictionary(p => p.Name);
        Assert.True(reloaded["broken"].Valid);
        Assert.True(reloaded["fresh"].Valid);
    }

    [Fact]
    public void Extends_depth_is_limited()
    {
        File.WriteAllText(Path.Combine(_dir, "d1.json"), """{"extends":"srt-to-vtt"}""");
        File.WriteAllText(Path.Combine(_dir, "d2.json"), """{"extends":"d1"}""");
        File.WriteAllText(Path.Combine(_dir, "d3.json"), """{"extends":"d2"}""");
        File.WriteAllText(Path.Combine(_dir, "d4.json"), """{"extends":"d3"}""");
        var list = Store.List().ToDictionary(p => p.Name);
        Assert.True(list["d3"].Valid);
        Assert.Contains(list["d4"].Errors, e => e.Message.Contains("deeper"));
    }

    [Fact]
    public void Json_schema_is_rendered_from_the_same_spec()
    {
        var schema = ProfileSpec.ToJsonSchema("x");
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", (string)schema["$schema"]!);
        Assert.False((bool)schema["additionalProperties"]!);
        Assert.NotNull(schema["properties"]!["rules"]!["properties"]!["maxLineLength"]);
        Assert.Equal(OperationCatalogIds.Count, schema["properties"]!["operations"]!["items"]!["oneOf"]!.AsArray().Count);
    }

    private static readonly IReadOnlyList<string> OperationCatalogIds = Conversion.OperationCatalog.Ids;
}
