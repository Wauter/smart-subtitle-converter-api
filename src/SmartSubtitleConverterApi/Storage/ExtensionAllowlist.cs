using SmartSubtitleConverterApi.Formats;
using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Storage;

/// <summary>
/// Subtitle file extensions that may be listed, read and written by location (H3). Derived at runtime from libse's formats
/// (FormatRegistry: Extension + AlternateExtensions of every libse text and binary format; image and container formats are left out because they cannot be converted), so a libse upgrade updates it;
/// ExtensionAllowlistTests pins a snapshot so additions and removals get reviewed. Case-insensitive. Applies to file and S3
/// locations, never to HTTPS URLs (a remote server's names are its own business). The JSON sidecar ({output}.json) is exempt.
/// </summary>
public sealed class ExtensionAllowlist
{
    private static readonly Lazy<ExtensionAllowlist> SharedInstance = new(() => new ExtensionAllowlist(new FormatRegistry()));
    /// <summary>The process-wide list (libse's formats don't change at runtime).</summary>
    public static ExtensionAllowlist Shared => SharedInstance.Value;
    private readonly HashSet<string> _set;
    public IReadOnlyList<string> Sorted { get; }

    public ExtensionAllowlist(FormatRegistry formats)
    {
        _set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in formats.All.Where(f => f.Source == "libse" && f.Kind is FormatKinds.Text or FormatKinds.Binary)) // image/container formats (mkv, mp4, sup, ...) cannot be converted
            foreach (var e in new[] { f.Extension }.Concat(f.AlternateExtensions))
                if (Normalize(e) is { } n) _set.Add(n);
        Sorted = _set.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>".SRT" / "srt" -> ".srt"; null for empty or malformed values.</summary>
    public static string? Normalize(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return null;
        var e = ext.Trim().ToLowerInvariant();
        if (!e.StartsWith('.')) e = "." + e;
        return e.Length > 1 && e.Skip(1).All(c => c != '.' && c != '/' && !char.IsWhiteSpace(c)) ? e : null;
    }

    public static string ExtensionOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant();

    public bool Allowed(string fileName) => ExtensionOf(fileName) is { Length: > 1 } e && _set.Contains(e);

    /// <summary>Throws extension-not-allowed for file and S3 locations whose name has no allowed extension. HTTPS and sidecars are exempt.</summary>
    public void Check(StorageLocation location, string? purpose = null)
    {
        if (location.Scheme is not ("file" or "s3") || location.Sidecar) return;
        if (!Allowed(location.FileName)) throw NotAllowed(location.FileName, purpose);
    }

    public static ApiException NotAllowed(string fileName, string? purpose = null)
    {
        var ext = ExtensionOf(fileName);
        var detail = ext.Length <= 1
            ? $"'{fileName}' has no file extension, and only subtitle files can be {purpose ?? "read, listed or written"} by location."
            : $"The extension '{ext}' is not a subtitle format this API knows, so '{fileName}' can't be {purpose ?? "read, listed or written"} by location.";
        return new ApiException(422, ErrorCodes.ExtensionNotAllowed, detail,
            "Only files with a subtitle extension (.srt, .vtt, .stl, .pac, ... the list is in GET /v1/limits under storage.extensions) are read, listed or written in file and S3 locations. "
            + "Rename the file to its real subtitle extension, or upload the content instead.") { Extensions = { ["extension"] = ext } };
    }

    /// <summary>The output name must carry one of the target format's own extensions (sidecars and HTTPS exempt).</summary>
    public static void CheckOutput(StorageLocation output, FormatInfo target)
    {
        if (output.Scheme is not ("file" or "s3") || output.Sidecar) return;
        var ext = ExtensionOf(output.FileName);
        var own = new[] { target.Extension }.Concat(target.AlternateExtensions).Select(Normalize).OfType<string>().Distinct().ToList();
        if (own.Contains(ext)) return;
        throw new ApiException(422, ErrorCodes.ExtensionNotAllowed,
            ext.Length <= 1
                ? $"The output name '{output.FileName}' has no file extension; target format '{target.Id}' is written as {string.Join(" or ", own)}."
                : $"The output name '{output.FileName}' ends in '{ext}', but target format '{target.Id}' is written as {string.Join(" or ", own)}.",
            $"Give the output a {string.Join(" or ", own)} name, or choose the target format that matches the name you want.")
        { Extensions = { ["extension"] = ext, ["allowed"] = own } };
    }
}
