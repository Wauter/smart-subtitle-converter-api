using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.Enums;
using SubtitleEditConverterApi.Binary;
using SubtitleEditConverterApi.Conversion;
using SubtitleEditConverterApi.Formats;
using SubtitleEditConverterApi.Infrastructure;

namespace SubtitleEditConverterApi.Profiles;

/// <summary>
/// A tiny schema DSL. The same tree validates profiles (collecting every error with a JSON
/// pointer and a hint) and is rendered as the published JSON Schema (design §5), so the two
/// can never drift.
/// </summary>
public abstract record Spec(string? Description = null);
public sealed record ObjSpec(Dictionary<string, Spec> Props, string? Description = null, string[]? Required = null) : Spec(Description);
public sealed record StrSpec(string? Description = null, string[]? Enum = null, string? Pattern = null) : Spec(Description);
public sealed record NumSpec(string? Description = null, bool Integer = false, double? Min = null, bool ExclusiveMin = false, double? Max = null) : Spec(Description);
public sealed record BoolSpec(string? Description = null) : Spec(Description);
public sealed record ArrSpec(Spec Item, string? Description = null, int? MaxItems = null) : Spec(Description);
/// <summary>A string from an enum, or an integer in a range (PAC code page: name or 0..12).</summary>
public sealed record StrOrIntSpec(string[] Enum, int Min, int Max, string? Description = null) : Spec(Description);
/// <summary>An operation entry: an object whose allowed keys depend on its "id".</summary>
public sealed record OpSpec(string? Description = null) : Spec(Description);

public static class ProfileSpec
{
    public static readonly string[] FceRules = OperationCatalog.FixCommonErrorsRules;

    public static readonly ObjSpec Root = new(new()
    {
        ["$schema"] = new StrSpec("Optional schema URL, ignored."),
        ["name"] = new StrSpec("Preset name (presets only).", Pattern: "^[a-z0-9-]+$"),
        ["description"] = new StrSpec("Human readable description."),
        ["extends"] = new StrSpec("Name of a preset to inherit from (single inheritance, depth <= 3).", Pattern: "^[a-z0-9-]+$"),
        ["source"] = new ObjSpec(new()
        {
            ["format"] = new StrSpec("'auto' (default) or a format id/alias from GET /v1/formats."),
            ["fps"] = new NumSpec("Source frame rate, for frame-based inputs and target.fps conversion (seconv --fps).", Min: 0, ExclusiveMin: true, Max: 1000),
        }, "How to read the input."),
        ["target"] = new ObjSpec(new()
        {
            ["format"] = new StrSpec("Output format id/alias (required after merging; seconv <format>)."),
            ["fps"] = new NumSpec("Target frame rate (seconv --target-fps).", Min: 0, ExclusiveMin: true, Max: 1000),
            ["fileName"] = new StrSpec("Output name template. Tokens: {stem}, {ext}, {format}. Default '{stem}.{ext}'."),
            ["additional"] = new ArrSpec(new StrSpec(), "Extra output formats; the multipart response becomes JSON.", MaxItems: 10),
        }, "What to write."),
        ["encoding"] = new ObjSpec(new()
        {
            ["input"] = new StrSpec("Force the input encoding (name or code page), skipping detection."),
            ["inputFallback"] = new StrSpec("Encoding used when input has no BOM and is not valid UTF-8 (seconv --input-encoding-fallback)."),
            ["output"] = new StrSpec("utf-8 (with BOM, default) | utf-8-no-bom | source | <encoding name or code page>."),
        }),
        ["timing"] = new ObjSpec(new()
        {
            ["offsetMs"] = new NumSpec("Shift all cues (signed ms; seconv --offset)."),
            ["changeSpeedPercent"] = new NumSpec("Scale times by 100/percent (seconv --change-speed).", Min: 0, ExclusiveMin: true),
            ["adjustDurationMs"] = new NumSpec("Add ms to every duration (seconv --adjust-duration)."),
            ["bridgeGapsMs"] = new NumSpec("Bridge gaps shorter than this (seconv --bridge-gaps).", Integer: true, Min: 0),
            ["minGapMs"] = new NumSpec("Enforce a minimum gap between cues (seconv --apply-min-gap).", Integer: true, Min: 0),
            ["renumberFrom"] = new NumSpec("Renumber cues starting at this number (seconv --renumber).", Integer: true, Min: 0),
        }),
        ["rules"] = new ObjSpec(new()
        {
            ["maxLineLength"] = new NumSpec("Max characters per line.", Integer: true, Min: 1, Max: 1000),
            ["maxLines"] = new NumSpec("Max lines per cue.", Integer: true, Min: 1, Max: 20),
            ["minDurationMs"] = new NumSpec(Integer: true, Min: 0),
            ["maxDurationMs"] = new NumSpec(Integer: true, Min: 1),
            ["minGapMs"] = new NumSpec("Minimum ms between cues used by the operations.", Integer: true, Min: 0),
            ["maxCps"] = new NumSpec("Max characters per second.", Min: 0, ExclusiveMin: true),
            ["optimalCps"] = new NumSpec(Min: 0, ExclusiveMin: true),
            ["maxWpm"] = new NumSpec(Min: 0, ExclusiveMin: true),
            ["mergeLinesShorterThan"] = new NumSpec(Integer: true, Min: 0),
            ["dialogStyle"] = new StrSpec(Enum: Enum.GetNames<DialogType>()),
            ["continuationStyle"] = new StrSpec(Enum: Enum.GetNames<ContinuationStyle>()),
            ["mergeShortLines"] = new ObjSpec(new()
            {
                ["maxGapMs"] = new NumSpec(Integer: true, Min: 0),
                ["onlyContinuous"] = new BoolSpec(),
            }),
            ["removeTextForHI"] = new ObjSpec(OperationCatalog.HiSettingNames.ToDictionary(k => k, k => (Spec)(k == "ifContainsText" ? new StrSpec() : new BoolSpec()))),
        }, "libse rule settings (Configuration.Settings.General / Tools), applied for this conversion only."),
        ["operations"] = new ArrSpec(new OpSpec(), "Operations executed in array order.", MaxItems: 50),
        ["formatOptions"] = new ObjSpec(new()
        {
            ["assa"] = new ObjSpec(new()
            {
                ["playResX"] = new NumSpec(Integer: true, Min: 1, Max: 16384),
                ["playResY"] = new NumSpec(Integer: true, Min: 1, Max: 16384),
                ["styleBlock"] = new StrSpec("ASSA [V4+ Styles] block (inline replacement for seconv --assa-style-file)."),
            }, "Applied when the target is ASSA/SSA."),
            ["ebuStl"] = new ObjSpec(new()
            {
                ["codePage"] = new StrSpec("GSI code page number (CPN). Default 850.", Enum: BinaryOptions.EbuCodePages),
                ["characterTable"] = new StrSpec("Character code table (CCT 00..04). Default latin.", Enum: [.. BinaryOptions.EbuCharacterTables.Keys]),
                ["displayStandard"] = new StrSpec("Display standard (DSC 0|1|2). Default: open, or teletext1 when the subtitle has colours.", Enum: [.. BinaryOptions.EbuDisplayStandards.Keys]),
                ["frameRate"] = new NumSpec("23.976 (or 23), 24, 25, 29.97 or 30; sets the disk format code. Default target.fps, else 25.", Min: 0, ExclusiveMin: true, Max: 120),
                ["language"] = new StrSpec("ISO 639-1 code (mapped to the EBU code), a two-digit EBU hex code, or 'auto' (default)."),
                ["originalProgrammeTitle"] = new StrSpec("GSI OPT, up to 32 bytes in the code page."),
                ["originalEpisodeTitle"] = new StrSpec("GSI OET, up to 32 bytes."),
                ["translatedProgrammeTitle"] = new StrSpec("GSI TPT, up to 32 bytes."),
                ["translatorsName"] = new StrSpec("GSI TN, up to 32 bytes."),
                ["publisher"] = new StrSpec("GSI PUB, up to 32 bytes."),
                ["countryOfOrigin"] = new StrSpec("ISO 3166 alpha-3, e.g. BEL.", Pattern: "^[A-Z]{3}$"),
                ["timecodeStartOfProgramme"] = new StrSpec("HH:MM:SS:FF.", Pattern: BinaryOptions.TimecodePattern),
                ["maxCharsPerRow"] = new NumSpec("GSI MNC.", Integer: true, Min: 1, Max: 99),
                ["maxRows"] = new NumSpec("GSI MNR.", Integer: true, Min: 1, Max: 99),
                ["teletext"] = new ObjSpec(new()
                {
                    ["useBox"] = new BoolSpec(), ["doubleHeight"] = new BoolSpec(),
                    ["marginTop"] = new NumSpec(Integer: true, Min: 0, Max: 23), ["marginBottom"] = new NumSpec(Integer: true, Min: 0, Max: 23),
                    ["newLineRows"] = new NumSpec(Integer: true, Min: 1, Max: 4),
                }, "Teletext layout (libse SubtitleSettings.EbuStl*)."),
                ["removeEmptyLines"] = new BoolSpec(),
                ["readFrameRate"] = new NumSpec("Source side: override the GSI frame rate when reading EBU STL.", Min: 0, ExclusiveMin: true, Max: 120),
                ["headerFromSource"] = new BoolSpec("Reuse the source GSI when the source is EBU STL (default true); explicit options still apply on top."),
            }, "Applied when the target (or, for readFrameRate, the source) is EBU STL."),
            ["pac"] = new ObjSpec(new()
            {
                ["codePage"] = new StrOrIntSpec(BinaryOptions.PacCodePages, 0, 12, "Output code page: name or 0..12. Default latin."),
                ["inputCodePage"] = new StrOrIntSpec([.. BinaryOptions.PacCodePages.Prepend("auto")], 0, 12, "Input code page: 'auto' (default), name or 0..12."),
                ["verticalTop"] = new NumSpec(Integer: true, Min: 0, Max: 99),
                ["verticalCenter"] = new NumSpec(Integer: true, Min: 0, Max: 99),
                ["verticalBottom"] = new NumSpec(Integer: true, Min: 0, Max: 99),
            }, "Applied when the target or source is PAC / PAC Unicode."),
            ["cavena890"] = new ObjSpec(new()
            {
                ["language"] = new StrSpec("Writer language/character set. Default: auto-detected.", Enum: BinaryOptions.CavenaLanguageNames),
                ["title"] = new StrSpec("Up to 28 ASCII characters."),
                ["originalTitle"] = new StrSpec("Up to 28 ASCII characters."),
                ["translator"] = new StrSpec("Up to 28 ASCII characters."),
                ["comment"] = new StrSpec("Up to 24 ASCII characters."),
                ["startOfMessage"] = new StrSpec("HH:MM:SS:FF, default 10:00:00:00.", Pattern: BinaryOptions.TimecodePattern),
            }, "Applied when the target is Cavena 890."),
            ["cheetah"] = new ObjSpec(new()
            {
                ["alwaysWriteEndTime"] = new BoolSpec("Write every cue's end time (libse default false)."),
            }, "Applied when the target is Cheetah Caption."),
        }),
    }, "A Subtitle Edit Converter API conversion profile.");

    // ---------------- validation ----------------

    public static void Validate(JsonNode? node, Spec spec, string pointer, List<FieldError> errors, FormatRegistry formats)
    {
        if (node is null) return; // null clears an inherited value
        switch (spec)
        {
            case ObjSpec o:
                if (node is not JsonObject obj) { errors.Add(new(pointer, "Expected an object.")); return; }
                foreach (var (key, value) in obj)
                {
                    var p = pointer + "/" + Escape(key);
                    if (!o.Props.TryGetValue(key, out var child))
                    {
                        var s = Text.Suggest(key, o.Props.Keys);
                        errors.Add(new(p, $"Unknown property '{key}'.", s != null ? $"Did you mean '{s}'?" : $"Allowed: {string.Join(", ", o.Props.Keys)}."));
                        continue;
                    }
                    Validate(value, child, p, errors, formats);
                }
                break;
            case StrSpec s:
                if (node is not JsonValue v || !v.TryGetValue<string>(out var str)) { errors.Add(new(pointer, "Expected a string.")); return; }
                if (s.Enum != null && !s.Enum.Contains(str))
                {
                    var sug = Text.Suggest(str, s.Enum);
                    errors.Add(new(pointer, $"'{str}' is not allowed.", sug != null ? $"Did you mean '{sug}'?" : $"Allowed: {string.Join(", ", s.Enum)}."));
                }
                if (s.Pattern != null && !Regex.IsMatch(str, s.Pattern)) errors.Add(new(pointer, $"'{str}' must match {s.Pattern}."));
                if (pointer is "/source/format" && !str.Equals("auto", StringComparison.OrdinalIgnoreCase)) CheckFormat(str, pointer, errors, formats);
                if (pointer is "/target/format" || pointer.StartsWith("/target/additional/")) CheckFormat(str, pointer, errors, formats);
                if (pointer.StartsWith("/encoding/") && !Encodings.IsKnown(str, pointer.EndsWith("/output")))
                    errors.Add(new(pointer, $"Unknown encoding '{str}'.", "Use e.g. utf-8, utf-8-no-bom, windows-1252, iso-8859-1, 1252, utf-16."));
                break;
            case NumSpec n:
                if (node is not JsonValue nv || !nv.TryGetValue<double>(out var d))
                {
                    errors.Add(new(pointer, n.Integer ? "Expected an integer." : "Expected a number.")); return;
                }
                if (n.Integer && d != Math.Floor(d)) errors.Add(new(pointer, "Expected an integer."));
                if (n.Min is { } min && (n.ExclusiveMin ? d <= min : d < min))
                    errors.Add(new(pointer, $"Must be {(n.ExclusiveMin ? ">" : ">=")} {min}.", pointer.EndsWith("fps") ? "Common values: 23.976, 24, 25, 29.97, 30." : null));
                if (n.Max is { } max && d > max) errors.Add(new(pointer, $"Must be <= {max}."));
                break;
            case BoolSpec:
                if (node is not JsonValue bv || bv.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                    errors.Add(new(pointer, "Expected true or false."));
                break;
            case ArrSpec a:
                if (node is not JsonArray arr) { errors.Add(new(pointer, "Expected an array.")); return; }
                if (a.MaxItems is { } mi && arr.Count > mi) errors.Add(new(pointer, $"At most {mi} items."));
                for (var i = 0; i < arr.Count; i++) Validate(arr[i], a.Item, $"{pointer}/{i}", errors, formats);
                break;
            case StrOrIntSpec si:
                if (node is JsonValue iv && iv.TryGetValue<double>(out var num))
                {
                    if (num != Math.Floor(num) || num < si.Min || num > si.Max) errors.Add(new(pointer, $"Must be an integer {si.Min}..{si.Max} or one of: {string.Join(", ", si.Enum)}."));
                }
                else if (node is JsonValue sv && sv.TryGetValue<string>(out var name))
                {
                    if (!si.Enum.Contains(name, StringComparer.OrdinalIgnoreCase) && !(int.TryParse(name, out var k) && k >= si.Min && k <= si.Max))
                    {
                        var sug = Text.Suggest(name, si.Enum);
                        errors.Add(new(pointer, $"'{name}' is not allowed.", sug != null ? $"Did you mean '{sug}'?" : $"Allowed: {string.Join(", ", si.Enum)} or {si.Min}..{si.Max}."));
                    }
                }
                else errors.Add(new(pointer, "Expected a string or an integer."));
                break;
            case OpSpec:
                OperationCatalog.ValidateOperation(node, pointer, errors);
                break;
        }
    }

    private static void CheckFormat(string id, string pointer, List<FieldError> errors, FormatRegistry formats)
    {
        var info = formats.Find(id);
        if (info == null && FormatRegistry.IsKnownUnsupported(id))
        {
            errors.Add(new(pointer, $"'{id}' is image-based or a container format.", "OCR/containers are on the roadmap; see GET /v1/formats?kind=text,binary."));
        }
        else if (info == null)
        {
            var s = formats.Suggest(id);
            errors.Add(new(pointer, $"Unknown format '{id}'.", s != null ? $"Did you mean '{s}'? See GET /v1/formats." : "See GET /v1/formats."));
        }
        else if (FormatRegistry.Unusable(info, write: !pointer.StartsWith("/source", StringComparison.Ordinal)) is { } u)
        {
            errors.Add(new(pointer, u.Message, u.Hint));
        }
    }

    /// <summary>Message markers ProfileResolver maps to 422 codes instead of 400 invalid-profile.</summary>
    public static bool IsUnsupportedFormatError(FieldError e) => e.Message.Contains("image-based") || e.Message.Contains("can't be read");
    public static bool IsNotWritableError(FieldError e) => e.Message.Contains("is read-only in this API");

    public static string Escape(string key) => key.Replace("~", "~0").Replace("/", "~1");

    // ---------------- JSON Schema rendering ----------------

    public static JsonObject ToJsonSchema(string id)
    {
        var schema = (JsonObject)Render(Root);
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["$id"] = id;
        schema["title"] = "Subtitle Edit Converter API profile";
        return schema;
    }

    private static JsonNode Render(Spec spec)
    {
        var o = new JsonObject();
        switch (spec)
        {
            case ObjSpec obj:
                o["type"] = "object";
                var props = new JsonObject();
                foreach (var (k, v) in obj.Props) props[k] = Render(v);
                o["properties"] = props;
                o["additionalProperties"] = false;
                break;
            case StrSpec s:
                o["type"] = new JsonArray("string", "null");
                if (s.Enum != null) o["enum"] = new JsonArray(s.Enum.Select(e => (JsonNode)e).Append(null).ToArray());
                if (s.Pattern != null) o["pattern"] = s.Pattern;
                break;
            case NumSpec n:
                o["type"] = new JsonArray(n.Integer ? "integer" : "number", "null");
                if (n.Min is { } min) o[n.ExclusiveMin ? "exclusiveMinimum" : "minimum"] = min;
                if (n.Max is { } max) o["maximum"] = max;
                break;
            case BoolSpec:
                o["type"] = new JsonArray("boolean", "null");
                break;
            case ArrSpec a:
                o["type"] = new JsonArray("array", "null");
                o["items"] = Render(a.Item);
                if (a.MaxItems is { } mi) o["maxItems"] = mi;
                break;
            case StrOrIntSpec si:
                o["oneOf"] = new JsonArray(
                    new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(si.Enum.Select(e => (JsonNode)e).ToArray()) },
                    new JsonObject { ["type"] = "integer", ["minimum"] = si.Min, ["maximum"] = si.Max },
                    new JsonObject { ["type"] = "null" });
                break;
            case OpSpec:
                return OperationCatalog.OperationSchema();
        }
        if (spec.Description != null) o["description"] = spec.Description;
        return o;
    }
}
