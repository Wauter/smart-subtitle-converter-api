using System.Text.Json.Nodes;
using SmartSubtitleConverterApi.Formats;
using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Profiles;

public sealed record ResolveResult(JsonObject Resolved, List<FieldError> Errors, List<FieldError> Warnings, string? PresetName, int SemanticErrors = 0);

/// <summary>Merges built-in -> mounted -> request layers and validates them (design §2.2, §5).</summary>
public sealed class ProfileResolver(PresetStore store, FormatRegistry formats)
{
    /// <summary>Resolve a convert request: preset + profile overrides + target shorthand. Throws on any error.</summary>
    public (Profile Profile, JsonObject Resolved, string? PresetName) ResolveForConversion(string? preset, JsonObject? overrides, string? target)
    {
        var layer = new JsonObject();
        if (!string.IsNullOrWhiteSpace(preset)) layer["extends"] = preset.Trim();
        layer = ProfileJson.Merge(layer, overrides);
        if (!string.IsNullOrWhiteSpace(preset) && overrides?["extends"] != null)
        {
            throw new ApiException(400, ErrorCodes.InvalidRequest, "Use either the 'preset' field or profile.extends, not both.", "Drop one of them.");
        }
        if (!string.IsNullOrWhiteSpace(target))
        {
            layer = ProfileJson.Merge(layer, new JsonObject { ["target"] = new JsonObject { ["format"] = target.Trim() } });
        }
        if (layer.Count == 0)
        {
            throw new ApiException(400, ErrorCodes.InvalidRequest, "No conversion target given.", "Provide `preset`, `target`, or `profile.target.format`.");
        }

        var r = ResolveLayers(layer, store.Snapshot(), formats, requireTarget: true, selfName: null);
        if (r.Errors.Count > 0)
        {
            var first = r.Errors[0];
            // Option errors are appended after the schema errors; the status is chosen from the schema ones, so a bad
            // country next to an unknown format is still 422 format-not-supported (the response lists both).
            var shape = r.Errors.Take(r.Errors.Count - r.SemanticErrors).ToList();
            var missingTarget = shape.Count > 0 && shape.All(e => e.Pointer == "/target/format" && e.Message.StartsWith("Required"));
            if (shape.Count > 0 && shape.All(ProfileSpec.IsUnsupportedFormatError))
            {
                throw new ApiException(422, ErrorCodes.FormatNotSupported, first.Message, first.Hint ?? "See GET /v1/formats.") { Errors = r.Errors };
            }
            if (shape.Count > 0 && shape.All(e => ProfileSpec.IsNotWritableError(e) || ProfileSpec.IsUnsupportedFormatError(e)))
            {
                var w = shape.First(ProfileSpec.IsNotWritableError);
                throw new ApiException(422, ErrorCodes.FormatNotWritable, w.Message, w.Hint ?? "Pick a writable target.") { Errors = r.Errors };
            }
            if (r.SemanticErrors > 0 && r.SemanticErrors == r.Errors.Count)
            {
                throw new ApiException(400, ErrorCodes.FormatOptionInvalid, $"formatOptions has {r.Errors.Count} invalid value(s); first at '{first.Pointer}': {first.Message}",
                    first.Hint ?? "See errors[].") { Errors = r.Errors };
            }
            throw new ApiException(400, missingTarget ? ErrorCodes.InvalidRequest : ErrorCodes.InvalidProfile,
                missingTarget ? "No target format after merging the profile." : $"The profile has {r.Errors.Count} error(s); first at '{first.Pointer}': {first.Message}",
                missingTarget ? "Provide `preset`, `target`, or `profile.target.format`." : first.Hint ?? "See errors[] and GET /v1/schemas/profile.json.")
            {
                Errors = r.Errors,
            };
        }
        return (ProfileJson.ToProfile(r.Resolved), r.Resolved, r.PresetName);
    }

    /// <summary>For POST /v1/profiles/validate: never throws for invalid profiles.</summary>
    public ResolveResult ValidateStandalone(JsonObject profile) =>
        ResolveLayers(profile, store.Snapshot(), formats, requireTarget: false, selfName: null);

    internal static ResolveResult ResolveLayers(JsonObject top, Dictionary<string, PresetEntry> presets, FormatRegistry formats, bool requireTarget, string? selfName)
    {
        var errors = new List<FieldError>();
        var warnings = new List<FieldError>();
        ProfileSpec.Validate(top, ProfileSpec.Root, "", errors, formats);
        var shapeErrors = errors.Count;
        // The format-option checks run on a copy of the request layer without the values the schema rejected, so one
        // response lists the schema errors and the option errors together (schema errors first, then option errors).
        var semanticTop = shapeErrors == 0 ? top : Prune(top, errors);

        // Walk the extends chain, top first.
        var chain = new List<JsonObject> { top };
        var semanticChain = new List<JsonObject> { semanticTop };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (selfName != null) seen.Add(selfName);
        string? presetName = null;
        var current = top;
        var depth = 0;
        while (current["extends"]?.GetValue<string>() is { } parent)
        {
            presetName ??= parent;
            if (!seen.Add(parent))
            {
                errors.Add(new("/extends", $"Cycle in extends chain at '{parent}'.", "Remove the circular extends.")); break;
            }
            if (++depth > PresetStore.MaxExtendsDepth)
            {
                errors.Add(new("/extends", $"extends chain deeper than {PresetStore.MaxExtendsDepth}.", "Flatten the preset chain.")); break;
            }
            if (!presets.TryGetValue(parent, out var entry))
            {
                var s = Text.Suggest(parent, presets.Keys);
                var msg = $"Unknown preset '{parent}'.";
                var hint = (s != null ? $"Did you mean '{s}'? " : "") + "See GET /v1/profiles (mounted presets with errors are listed there).";
                if (depth == 1 && selfName == null)
                {
                    throw new ApiException(404, ErrorCodes.PresetNotFound, msg, hint + $" Known presets: {string.Join(", ", presets.Keys.Order())}.")
                    { Errors = [new("/extends", msg, s != null ? $"Did you mean '{s}'?" : null)] };
                }
                errors.Add(new("/extends", msg, hint)); break;
            }
            if (entry.Raw == null || (entry.Errors.Count > 0 && entry.Name != selfName))
            {
                errors.Add(new("/extends", $"Preset '{parent}' is invalid ({entry.Errors.FirstOrDefault()?.Message}).", $"Fix it; see GET /v1/profiles/{parent}."));
                break;
            }
            chain.Add(entry.Raw);
            semanticChain.Add(entry.Raw);
            current = entry.Raw;
        }

        var resolved = new JsonObject();
        for (var i = chain.Count - 1; i >= 0; i--) resolved = ProfileJson.Merge(resolved, chain[i]);
        resolved.Remove("extends");
        resolved.Remove("$schema");
        if (selfName != null) resolved["name"] = selfName; else resolved.Remove("name");

        if (resolved["target"]?["format"] == null)
        {
            var e = new FieldError("/target/format", "Required: no target format after merging.", "Set target.format, the 'target' form field, or use a preset that has one.");
            if (requireTarget) errors.Add(e); else warnings.Add(e with { Message = "No target format; callers must supply one." });
        }
        if (resolved["target"]?["fps"] != null && resolved["source"]?["fps"] == null)
        {
            warnings.Add(new("/target/fps",
                "target.fps without source.fps only sets the output frame rate: time-based sources (SRT, WebVTT, ...) keep their times, frame-based sources are rescaled from the rate the file states or implies (EBU STL header, SCC timecodes), and frame-based sources that record no rate (PAC, Cavena 890, ...) need source.fps.",
                "Set source.fps too if the times should be rescaled from one frame rate to the other (e.g. 25 -> 23.976)."));
        }
        // Checks the JSON Schema can't express (plan §2). They are skipped only when an extends problem leaves the merged
        // profile incomplete: a missing inherited value could make them report something that isn't wrong.
        var semantic = 0;
        if (errors.Count == shapeErrors || (errors.Count > shapeErrors && requireTarget && errors.Skip(shapeErrors).All(e => e.Pointer == "/target/format")))
        {
            try
            {
                var merged = new JsonObject();
                for (var i = semanticChain.Count - 1; i >= 0; i--) merged = ProfileJson.Merge(merged, semanticChain[i]);
                merged.Remove("extends"); merged.Remove("$schema"); merged.Remove("name");
                var se = Binary.BinaryOptions.Validate(ProfileJson.ToProfile(merged));
                semantic = se.Count;
                errors.AddRange(se);
            }
            catch (Exception ex) when (shapeErrors > 0 && ex is not OutOfMemoryException)
            {
                // The rejected shape could not be read as a profile at all: the schema errors already say what to fix.
            }
        }
        return new ResolveResult(resolved, errors, warnings, presetName, semantic);
    }

    /// <summary>A copy of a request layer without the nodes the schema check reported (pointers from <see cref="ProfileSpec.Validate"/>).</summary>
    private static JsonObject Prune(JsonObject top, List<FieldError> schemaErrors)
    {
        var copy = (JsonObject)top.DeepClone();
        var doomed = new List<JsonNode>();
        foreach (var e in schemaErrors)
        {
            JsonNode? node = copy;
            foreach (var raw in e.Pointer.Split('/').Skip(1))
            {
                var key = raw.Replace("~1", "/").Replace("~0", "~");
                node = node switch
                {
                    JsonObject o => o[key],
                    JsonArray a when int.TryParse(key, out var i) && i >= 0 && i < a.Count => a[i],
                    _ => null,
                };
                if (node == null) break;
            }
            if (node != null && node != copy) doomed.Add(node);
        }
        foreach (var node in doomed)
        {
            switch (node.Parent)
            {
                case JsonObject o: o.Remove(node.GetPropertyName()); break;
                case JsonArray a: a.Remove(node); break;
            }
        }
        return copy;
    }
}
