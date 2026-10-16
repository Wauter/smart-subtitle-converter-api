using System.Reflection;
using System.Text.Json.Nodes;
using SubtitleEditApi.Formats;
using SubtitleEditApi.Infrastructure;

namespace SubtitleEditApi.Profiles;

public sealed record PresetEntry
{
    public required string Name { get; init; }
    public required string Origin { get; init; } // builtin | mounted
    public required string Path { get; init; }
    public bool Valid { get; set; }
    public string? Description { get; init; }
    public string? Target { get; set; }
    public DateTimeOffset LoadedAt { get; init; }
    public string? Shadows { get; set; }
    public List<FieldError> Errors { get; init; } = [];
    public List<FieldError> Warnings { get; init; } = [];
    public JsonObject? Raw { get; init; }
    public JsonObject? Resolved { get; set; }
}

/// <summary>
/// Built-in presets (embedded) plus presets mounted at PROFILES_DIR. The folder is re-scanned
/// whenever a file or directory mtime changes, so no restart is needed (design §4.5). Invalid
/// presets are kept and listed with their errors for troubleshooting.
/// </summary>
public sealed class PresetStore(ApiOptions opts, FormatRegistry formats, ILogger<PresetStore> log)
{
    public const int MaxExtendsDepth = 3;
    private readonly Lock _lock = new();
    private string _signature = "<none>";
    private Dictionary<string, PresetEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<PresetEntry> _builtins = LoadBuiltins();
    public string? MountError { get; private set; }

    private static List<PresetEntry> LoadBuiltins()
    {
        var asm = Assembly.GetExecutingAssembly();
        var list = new List<PresetEntry>();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("preset:")).Order())
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            var file = res["preset:".Length..];
            list.Add(Parse(System.IO.Path.GetFileNameWithoutExtension(file), "builtin", "builtin://" + file, r.ReadToEnd()));
        }
        return list;
    }

    private static PresetEntry Parse(string name, string origin, string path, string json)
    {
        var errors = new List<FieldError>();
        JsonObject? raw = null;
        try
        {
            raw = ProfileJson.ParseObject(json, "Preset");
        }
        catch (ApiException ex)
        {
            errors.Add(new FieldError("", ex.Message, "Fix the file; it must be a JSON object."));
        }
        var warnings = new List<FieldError>();
        if (raw?["name"]?.GetValue<string>() is { } declared && declared != name)
        {
            warnings.Add(new("/name", $"Declared name '{declared}' differs from file name; the file name '{name}' is used."));
        }
        return new PresetEntry
        {
            Name = name, Origin = origin, Path = path, Raw = raw, Errors = errors, Warnings = warnings,
            Description = raw?["description"]?.GetValue<string>(), LoadedAt = DateTimeOffset.UtcNow,
        };
    }

    private string ComputeSignature()
    {
        var dir = opts.ProfilesDir;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "<none>";
        try
        {
            var files = Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal)
                .Select(f => { var i = new FileInfo(f); return $"{i.Name}:{i.LastWriteTimeUtc.Ticks}:{i.Length}"; });
            return Directory.GetLastWriteTimeUtc(dir).Ticks + "|" + string.Join("|", files);
        }
        catch (Exception ex)
        {
            return "<error>" + ex.Message;
        }
    }

    private void Refresh()
    {
        var sig = ComputeSignature();
        if (sig == _signature && _entries.Count > 0) return;
        lock (_lock)
        {
            if (sig == _signature && _entries.Count > 0) return;
            var entries = new Dictionary<string, PresetEntry>(StringComparer.Ordinal);
            foreach (var b in _builtins) entries[b.Name] = b with { Errors = [.. b.Errors], Warnings = [.. b.Warnings] };

            MountError = null;
            var dir = opts.ProfilesDir;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                string[] files = [];
                try { files = Directory.GetFiles(dir, "*.json"); }
                catch (Exception ex) { MountError = ex.Message; log.LogWarning("profiles.dir-unreadable: {error}", ex.Message); }
                foreach (var f in files.Order(StringComparer.Ordinal))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(f);
                    PresetEntry entry;
                    try
                    {
                        var info = new FileInfo(f);
                        entry = info.Length > opts.ProfileMaxBytes
                            ? new PresetEntry { Name = name, Origin = "mounted", Path = f, LoadedAt = DateTimeOffset.UtcNow, Errors = [new("", $"File is {info.Length} bytes; max is {opts.ProfileMaxBytes}.")] }
                            : Parse(name, "mounted", f, File.ReadAllText(f));
                    }
                    catch (Exception ex)
                    {
                        entry = new PresetEntry { Name = name, Origin = "mounted", Path = f, LoadedAt = DateTimeOffset.UtcNow, Errors = [new("", "File could not be read: " + ex.Message, "Check file permissions on the mount.")] };
                    }
                    if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9-]+$"))
                    {
                        entry.Errors.Add(new("", $"File name '{name}' is not a valid preset name.", "Use lowercase letters, digits and '-' only."));
                    }
                    if (entries.TryGetValue(name, out var existing) && existing.Origin == "builtin") entry.Shadows = existing.Path;
                    entries[name] = entry;
                }
            }
            else if (!string.IsNullOrEmpty(dir))
            {
                MountError = $"PROFILES_DIR '{dir}' does not exist (no presets mounted).";
            }

            foreach (var e in entries.Values) ResolveEntry(e, entries);
            _entries = entries;
            _signature = sig;
            log.LogInformation("profiles.loaded valid={valid} invalid={invalid}", entries.Values.Count(e => e.Valid), entries.Values.Count(e => !e.Valid));
        }
    }

    private void ResolveEntry(PresetEntry e, Dictionary<string, PresetEntry> all)
    {
        if (e.Raw == null) { e.Valid = false; return; }
        try
        {
            var r = ProfileResolver.ResolveLayers(e.Raw, all, formats, requireTarget: false, selfName: e.Name);
            e.Errors.AddRange(r.Errors);
            e.Warnings.AddRange(r.Warnings);
            e.Resolved = r.Resolved;
            e.Target = r.Resolved["target"]?["format"]?.GetValue<string>();
        }
        catch (ApiException ex)
        {
            e.Errors.AddRange(ex.Errors.Count > 0 ? ex.Errors : [new FieldError("", ex.Message, ex.Hint)]);
        }
        e.Valid = e.Errors.Count == 0;
    }

    public IReadOnlyCollection<PresetEntry> List()
    {
        Refresh();
        return _entries.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    public PresetEntry? Get(string name)
    {
        Refresh();
        return _entries.GetValueOrDefault(name);
    }

    internal Dictionary<string, PresetEntry> Snapshot()
    {
        Refresh();
        return _entries;
    }

    public (int Valid, int Invalid) Counts()
    {
        var l = List();
        return (l.Count(e => e.Valid), l.Count(e => !e.Valid));
    }
}
