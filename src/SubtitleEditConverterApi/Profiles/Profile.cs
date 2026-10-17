using System.Text.Json;
using System.Text.Json.Nodes;
using SubtitleEditConverterApi.Infrastructure;

namespace SubtitleEditConverterApi.Profiles;

// Typed view of a merged, validated profile (design §5). Everything is optional; null = libse default.
public sealed record Profile
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Extends { get; init; }
    public SourceOptions? Source { get; init; }
    public TargetOptions? Target { get; init; }
    public EncodingOptions? Encoding { get; init; }
    public TimingOptions? Timing { get; init; }
    public RuleOptions? Rules { get; init; }
    public List<OperationSpec>? Operations { get; init; }
    public FormatOptionsSpec? FormatOptions { get; init; }
}

public sealed record SourceOptions(string? Format, double? Fps);
public sealed record TargetOptions(string? Format, double? Fps, string? FileName, List<string>? Additional);
public sealed record EncodingOptions(string? Input, string? InputFallback, string? Output);
public sealed record TimingOptions(double? OffsetMs, double? ChangeSpeedPercent, double? AdjustDurationMs, int? BridgeGapsMs, int? MinGapMs, int? RenumberFrom);
public sealed record MergeShortLinesOptions(int? MaxGapMs, bool? OnlyContinuous);
public sealed record RuleOptions
{
    public int? MaxLineLength { get; init; }
    public int? MaxLines { get; init; }
    public int? MinDurationMs { get; init; }
    public int? MaxDurationMs { get; init; }
    public int? MinGapMs { get; init; }
    public double? MaxCps { get; init; }
    public double? OptimalCps { get; init; }
    public double? MaxWpm { get; init; }
    public int? MergeLinesShorterThan { get; init; }
    public string? DialogStyle { get; init; }
    public string? ContinuationStyle { get; init; }
    public MergeShortLinesOptions? MergeShortLines { get; init; }
    public Dictionary<string, JsonElement>? RemoveTextForHI { get; init; }
}
public sealed record ReplaceRule(string Find, string? Replace, string? Mode);
public sealed record OperationSpec
{
    public string Id { get; init; } = "";
    public List<string>? Rules { get; init; }
    public string? Language { get; init; }
    public int? Count { get; init; }
    public string? Text { get; init; }
    public List<ReplaceRule>? Replacements { get; init; }
}
public sealed record AssaOptions(int? PlayResX, int? PlayResY, string? StyleBlock);
public sealed record FormatOptionsSpec(AssaOptions? Assa, EbuStlOptions? EbuStl = null, PacOptions? Pac = null, Cavena890Options? Cavena890 = null, CheetahOptions? Cheetah = null);
public sealed record EbuTeletextOptions(bool? UseBox, bool? DoubleHeight, int? MarginTop, int? MarginBottom, int? NewLineRows);
public sealed record EbuStlOptions
{
    public string? CodePage { get; init; }
    public string? CharacterTable { get; init; }
    public string? DisplayStandard { get; init; }
    public double? FrameRate { get; init; }
    public string? Language { get; init; }
    public string? OriginalProgrammeTitle { get; init; }
    public string? OriginalEpisodeTitle { get; init; }
    public string? TranslatedProgrammeTitle { get; init; }
    public string? TranslatorsName { get; init; }
    public string? Publisher { get; init; }
    public string? CountryOfOrigin { get; init; }
    public string? TimecodeStartOfProgramme { get; init; }
    public int? MaxCharsPerRow { get; init; }
    public int? MaxRows { get; init; }
    public EbuTeletextOptions? Teletext { get; init; }
    public bool? RemoveEmptyLines { get; init; }
    public double? ReadFrameRate { get; init; }
    public bool? HeaderFromSource { get; init; }
}
/// <summary>codePage/inputCodePage take a name or a number 0..12 (seconv --pac-codepage), so they stay JSON.</summary>
public sealed record PacOptions(JsonElement? CodePage, JsonElement? InputCodePage, int? VerticalTop, int? VerticalCenter, int? VerticalBottom);
public sealed record Cavena890Options(string? Language, string? Title, string? OriginalTitle, string? Translator, string? Comment, string? StartOfMessage);
public sealed record CheetahOptions(bool? AlwaysWriteEndTime);

public static class ProfileJson
{
    /// <summary>Deep merge (design §5): objects merge, arrays replace wholesale, null clears.</summary>
    public static JsonObject Merge(JsonObject? baseObj, JsonObject? overlay)
    {
        var result = baseObj?.DeepClone() as JsonObject ?? new JsonObject();
        if (overlay == null) return result;
        foreach (var (key, value) in overlay)
        {
            if (value is null)
            {
                result.Remove(key);
            }
            else if (value is JsonObject o && result[key] is JsonObject existing)
            {
                result[key] = Merge(existing, o);
            }
            else
            {
                result[key] = value.DeepClone();
            }
        }
        return result;
    }

    public static Profile ToProfile(JsonObject merged)
    {
        // MultipleReplace uses "rules" for its replacement list in the profile; map it to a typed list.
        var clone = (JsonObject)merged.DeepClone();
        if (clone["operations"] is JsonArray ops)
        {
            foreach (var op in ops.OfType<JsonObject>())
            {
                if (op["id"]?.GetValue<string>() is { } id && id.Equals("MultipleReplace", StringComparison.OrdinalIgnoreCase) && op["rules"] is JsonArray r)
                {
                    op.Remove("rules");
                    op["replacements"] = r.DeepClone();
                }
            }
        }
        clone.Remove("$schema");
        return clone.Deserialize<Profile>(Json.Options) ?? new Profile();
    }

    public static JsonObject ParseObject(string json, string what)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new ApiException(400, ErrorCodes.InvalidProfile, $"{what} is not valid JSON: {ex.Message}", "Fix the JSON syntax; see GET /v1/schemas/profile.json.", ex)
            {
                Errors = [new FieldError("", $"Invalid JSON at line {ex.LineNumber + 1}, byte {ex.BytePositionInLine}.")],
            };
        }
        return node as JsonObject ?? throw new ApiException(400, ErrorCodes.InvalidProfile, $"{what} must be a JSON object.", "Send an object like {\"target\":{\"format\":\"webvtt\"}}.");
    }
}
