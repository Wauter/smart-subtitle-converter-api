using SubtitleEditConverterApi.Infrastructure;

namespace SubtitleEditConverterApi.Conversion;

/// <summary>
/// Batch names are never renamed: entries that would produce the same output (same folder + stem,
/// case-insensitive) and outputs that still collide are rejected with 422 ambiguous-batch-names.
/// </summary>
public static class BatchNames
{
    private const string Hint = "Rename the clashing entries so each folder + file name (without extension) is unique, e.g. ep1.srt and ep1-forced.vtt, or put them in different folders.";

    /// <summary>Folder + stem (file name without its last extension), the key outputs are named from.</summary>
    public static string StemKey(string entryPath)
    {
        var slash = entryPath.LastIndexOf('/');
        var dir = slash >= 0 ? entryPath[..(slash + 1)] : "";
        var file = entryPath[(slash + 1)..];
        var dot = file.LastIndexOf('.');
        return (dir + (dot > 0 ? file[..dot] : file)).ToLowerInvariant();
    }

    /// <summary>Checked before any conversion (independent of failFast).</summary>
    public static void CheckEntries(IEnumerable<string> entryPaths)
    {
        var groups = entryPaths.GroupBy(StemKey, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
        if (groups.Count == 0) return;
        var ex = new ApiException(422, ErrorCodes.AmbiguousBatchNames,
            $"{groups.Sum(g => g.Count)} zip entries in {groups.Count} group(s) share a folder and file name (without extension), so their outputs would collide: "
            + string.Join("; ", groups.Select(g => string.Join(", ", g.Select(n => $"'{n}'")))) + ".", Hint)
        {
            Errors = groups.SelectMany(g => g.Select(n => new FieldError(n,
                $"Clashes with {string.Join(", ", g.Where(o => o != n).Select(o => $"'{o}'"))} (same folder and stem, compared case-insensitively)."))).ToList(),
        };
        ex.Extensions["groups"] = groups.Select(g => g.ToArray()).ToArray();
        throw ex;
    }

    /// <summary>Final output paths that still collide (several outputs per entry, manifest.json, ...).</summary>
    public static void CheckOutputs(IEnumerable<(string Output, string Source)> outputs)
    {
        var clashes = outputs.Append((Output: "manifest.json", Source: "manifest.json"))
            .GroupBy(o => o.Output, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Select(o => (o.Output, o.Source)).ToList()).ToList();
        if (clashes.Count > 0) throw OutputClash(clashes);
    }

    internal static ApiException OutputClash(List<List<(string Output, string Source)>> clashes)
    {
        var ex = new ApiException(422, ErrorCodes.AmbiguousBatchNames,
            "Output paths collide: " + string.Join("; ", clashes.Select(g => $"'{g[0].Output}' from " + string.Join(", ", g.Select(o => o.Source == "manifest.json" ? "the manifest" : $"'{o.Source}'")))) + ".",
            "Outputs are never renamed or overwritten. Rename the source entries, or drop the extra target that produces the same file name (manifest.json is reserved).")
        {
            Errors = clashes.SelectMany(g => g.Where(o => o.Source != "manifest.json" || g.All(x => x.Source == "manifest.json")).Select(o => new FieldError(o.Source,
                $"Output '{o.Output}' clashes with " + string.Join(", ", g.Where(x => x != o).Select(x => x.Source == "manifest.json" ? "the reserved manifest.json" : $"'{x.Output}' from '{x.Source}'")) + "."))).ToList(),
        };
        ex.Extensions["groups"] = clashes.Select(g => g.Select(o => o.Source).Distinct().ToArray()).ToArray();
        ex.Extensions["outputs"] = clashes.Select(g => g[0].Output).ToArray();
        return ex;
    }
}
