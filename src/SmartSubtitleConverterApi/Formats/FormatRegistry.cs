using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.Interfaces;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Formats;

public sealed record FormatInfo(
    string Id, string Name, string[] Aliases, string Extension, string[] AlternateExtensions,
    bool TextBased, bool FrameBased, bool StyleSupport, bool PositionSupport,
    bool CanRead, bool CanWrite, string Source, string MediaType,
    string Kind = FormatKinds.Text, bool Lossy = false, string? Options = null);

public static class FormatKinds
{
    public const string Text = "text";
    public const string Binary = "binary";
    public const string Image = "image";
    public const string Container = "container";
    public static readonly string[] All = [Text, Binary, Image, Container];
}

/// <summary>
/// The set of formats the API exposes (design §2.2). <c>kind</c> comes from which libse list a
/// class is in (<c>GetBinaryFormats</c> = binary; libse's IsTextBased is only false for EBU).
/// Image/container formats are listed (kind=image|container) but can't be read or written.
/// Ids are the libse name lower-cased with non-alphanumerics removed; aliases follow seconv.
/// </summary>
public sealed partial class FormatRegistry
{
    // seconv aliases (LibSEIntegration.NormalizeFormatName), plus a few common ones.
    private static readonly Dictionary<string, string> AliasToName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ass"] = "Advanced Sub Station Alpha", ["assa"] = "Advanced Sub Station Alpha",
        ["ssa"] = "Sub Station Alpha", ["srt"] = "SubRip", ["smi"] = "SAMI", ["vtt"] = "WebVTT",
        ["sbv"] = "YouTube sbv", ["ttml"] = "Timed Text 1.0", ["dfxp"] = "DFXP Basic",
        ["txt"] = "Plain text", ["text"] = "Plain text", ["plaintext"] = "Plain text",
        ["sub"] = "MicroDVD", ["scc"] = "Scenarist Closed Captions",
        ["ebu"] = "EBU STL", ["ebustl"] = "EBU STL", ["stl"] = "EBU STL", ["pac"] = "PAC (Screen Electronics)",
        ["cavena"] = "Cavena 890", ["890"] = "Cavena 890", ["sup"] = "Blu-ray sup", ["bluraysup"] = "Blu-ray sup", ["vobsub"] = "VobSub",
        ["idx"] = "VobSub", ["fpc"] = "PAC Unicode (UniPac)", ["unipac"] = "PAC Unicode (UniPac)", ["cheetah"] = "Cheetah Caption",
        ["capmaker"] = "CapMaker Plus", ["avidstl"] = "Avid STL", ["mkv"] = "Matroska", ["mks"] = "Matroska",
    };

    /// <summary>libse writers that lose data (milestone plan §1.1): API adds a lossy-format warning.</summary>
    private static readonly HashSet<string> LossyFormats = ["cheetahcaption", "cheetahcaptionold"];
    /// <summary>IBinaryPersistableSubtitle classes whose writer is known broken in libse 5.1.0 (Ayato drops the last cue).</summary>
    private static readonly HashSet<string> WriteDisabled = ["ayato"];
    /// <summary>Readers that need companion files next to the input (multi-file): listed, but canRead=false.</summary>
    private static readonly HashSet<string> ReadDisabled = ["playcaptionsfreeeditor"];

    /// <summary>formatOptions block per format id.</summary>
    public static string? OptionsBlockFor(string id) => id switch
    {
        "advancedsubstationalpha" or "substationalpha" => "assa",
        "ebustl" => "ebuStl",
        "pacscreenelectronics" or "pacunicodeunipac" => "pac",
        "cavena890" => "cavena890",
        "cheetahcaption" or "cheetahcaptionold" => "cheetah",
        _ => null,
    };

    /// <summary>Image/container formats Subtitle Edit knows but this API can't read or write (listed with kind=image|container).</summary>
    private static readonly (string Name, string Ext, string[] Alt, string Kind)[] ImageFormats =
    [
        ("Blu-ray sup", ".sup", [], FormatKinds.Image), ("VobSub", ".sub", [".idx"], FormatKinds.Image),
        ("BDN XML", ".xml", [], FormatKinds.Image), ("Dost", ".dost", [], FormatKinds.Image),
        ("FCP image", ".xml", [], FormatKinds.Image), ("IMSC image", ".xml", [], FormatKinds.Image),
        ("WebVTT thumbnail", ".vtt", [], FormatKinds.Image),
        ("Matroska", ".mkv", [".mks"], FormatKinds.Container), ("MP4", ".mp4", [], FormatKinds.Container),
        ("MPEG-TS", ".ts", [], FormatKinds.Container), ("MXF", ".mxf", [], FormatKinds.Container),
    ];

    private static readonly HashSet<string> PositionFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "advancedsubstationalpha", "substationalpha", "webvtt", "timedtext10", "netflixtimedtext", "dfxpbasic", "itunestimedtext", "imsc11", "smptett2052",
    };

    private readonly Dictionary<string, (FormatInfo Info, Func<SubtitleFormat> Factory)> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _detectOrder = [];
    private readonly List<string> _binaryOrder = [];

    public IReadOnlyList<FormatInfo> All { get; }

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnum();

    public static string ToId(string name) => NonAlnum().Replace(name.ToLowerInvariant(), "");

    public FormatRegistry()
    {
        var list = new List<FormatInfo>();
        var binaryTypes = SubtitleFormat.GetBinaryFormats(true).Select(f => f.GetType()).ToHashSet();
        void Add(SubtitleFormat f)
        {
            var id = ToId(f.Name);
            if (id.Length == 0 || _byId.ContainsKey(id)) return;
            var type = f.GetType();
            var binary = binaryTypes.Contains(type) || !f.IsTextBased;
            var aliases = AliasToName.Where(a => ToId(a.Value) == id).Select(a => a.Key.ToLowerInvariant()).ToArray();
            var canRead = !ReadDisabled.Contains(id);
            var canWrite = binary ? typeof(IBinaryPersistableSubtitle).IsAssignableFrom(type) && !WriteDisabled.Contains(id) : true;
            var info = new FormatInfo(id, f.Name, aliases, f.Extension, f.AlternateExtensions?.ToArray() ?? [],
                !binary, f.IsFrameBased, f.HasStyleSupport, PositionFormats.Contains(id),
                CanRead: canRead, CanWrite: canWrite, Source: "libse", MediaType: binary ? BinaryMediaType : MediaTypeFor(id),
                Kind: binary ? FormatKinds.Binary : FormatKinds.Text, Lossy: LossyFormats.Contains(id), Options: OptionsBlockFor(id));
            _byId[id] = (info, () => (SubtitleFormat)Activator.CreateInstance(type)!);
            list.Add(info);
            if (!binary) _detectOrder.Add(id);
            else if (canRead) _binaryOrder.Add(id);
        }

        foreach (var f in SubtitleFormat.AllSubtitleFormats) Add(f);
        foreach (var f in SubtitleFormat.GetBinaryFormats(true)) Add(f);
        foreach (var (name, ext, alt, kind) in ImageFormats)
        {
            var id = ToId(name);
            if (_byId.ContainsKey(id)) continue;
            var aliases = AliasToName.Where(a => ToId(a.Value) == id).Select(a => a.Key.ToLowerInvariant()).ToArray();
            var info = new FormatInfo(id, name, aliases, ext, alt, false, false, false, false, CanRead: false, CanWrite: false,
                Source: "libse", MediaType: BinaryMediaType, Kind: kind);
            _byId[id] = (info, () => throw new InvalidOperationException($"{id} has no reader"));
            list.Add(info);
        }
        foreach (var (id, _) in _byId) _lookup[id] = id;
        foreach (var (alias, name) in AliasToName)
        {
            var id = ToId(name);
            if (_byId.ContainsKey(id)) _lookup.TryAdd(ToId(alias), id);
        }
        All = list.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
        BinaryExtensions = list.Where(i => i.Kind == FormatKinds.Binary && i.CanRead)
            .SelectMany(i => i.AlternateExtensions.Prepend(i.Extension))
            .Select(e => e.ToLowerInvariant()).Where(e => SafeExtension().IsMatch(e)).ToHashSet();
    }

    public const string BinaryMediaType = "application/octet-stream";

    /// <summary>Extensions of readable binary formats: the allowlist for temp-file names (plan §1.4).</summary>
    public IReadOnlySet<string> BinaryExtensions { get; }

    [GeneratedRegex("^\\.[a-z0-9]{1,8}$")]
    private static partial Regex SafeExtension();

    public static string MediaTypeFor(string id) => id switch
    {
        "subrip" => "application/x-subrip",
        "webvtt" => "text/vtt",
        "advancedsubstationalpha" or "substationalpha" => "text/x-ssa",
        "timedtext10" or "dfxpbasic" or "netflixtimedtext" or "itunestimedtext" or "smptett2052" => "application/ttml+xml",
        "sami" => "application/smil+xml",
        _ => "text/plain",
    };

    /// <summary>Ids Subtitle Edit knows but this API can't handle and doesn't list (kept for precise errors).</summary>
    private static readonly HashSet<string> KnownImageFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "sup", "bluraysup", "vobsub", "idx", "dvdsup", "bdnxml", "imscimage", "dost", "fcpimage", "dcinemainterop", "dcinemasmpte2014", "webvttthumbnail", "mkv", "mks", "mp4", "ts", "mxf", "customtext", "customtextformat",
    };

    public static bool IsKnownUnsupported(string idOrAlias) => KnownImageFormats.Contains(ToId(idOrAlias));

    public FormatInfo? Find(string idOrAlias) =>
        _lookup.TryGetValue(ToId(idOrAlias), out var id) ? _byId[id].Info : null;

    /// <summary>Why a format can't be used in this role, or null when it can (shared with profile validation).</summary>
    public static (string Code, string Message, string Hint)? Unusable(FormatInfo info, bool write)
    {
        if (info.Kind is FormatKinds.Image or FormatKinds.Container)
            return (ErrorCodes.FormatNotSupported, $"'{info.Id}' is image-based or a container format.",
                "Text and binary subtitle formats are supported (GET /v1/formats); OCR and container extraction are on the roadmap.");
        if (!write && !info.CanRead)
            return (ErrorCodes.FormatNotSupported, $"'{info.Id}' can't be read (it needs companion files next to the input).", "Convert it with Subtitle Edit desktop first.");
        if (write && !info.CanWrite)
            return (ErrorCodes.FormatNotWritable, $"'{info.Id}' is read-only in this API.",
                "Pick another target; GET /v1/formats?writable=true lists the writable formats.");
        return null;
    }

    /// <summary>Resolve a format for reading or writing; throws a 422 with a "did you mean" hint.</summary>
    public (FormatInfo Info, SubtitleFormat Format) Resolve(string idOrAlias, string pointer)
    {
        var write = pointer.StartsWith("/target", StringComparison.Ordinal);
        var info = Find(idOrAlias);
        if (info == null && IsKnownUnsupported(idOrAlias))
        {
            throw new ApiException(422, ErrorCodes.FormatNotSupported, $"'{idOrAlias}' is image-based or a container format.",
                "Text and binary subtitle formats are supported (GET /v1/formats); OCR and container extraction are on the roadmap.")
            { Errors = [new FieldError(pointer, $"'{idOrAlias}' is not supported")] };
        }
        if (info == null)
        {
            var suggestion = Suggest(idOrAlias);
            throw new ApiException(422, ErrorCodes.FormatNotSupported, $"Unknown format '{idOrAlias}'.",
                suggestion != null ? $"Did you mean '{suggestion}'? See GET /v1/formats." : "See GET /v1/formats for valid ids and aliases.")
            {
                Errors = [new FieldError(pointer, $"Unknown format '{idOrAlias}'", suggestion != null ? $"Did you mean '{suggestion}'?" : null)],
            };
        }
        if (Unusable(info, write) is { } u)
        {
            throw new ApiException(422, u.Code, u.Message, u.Hint) { Errors = [new FieldError(pointer, u.Message)] };
        }
        return (info, _byId[info.Id].Factory());
    }

    public string? Suggest(string input)
    {
        var target = ToId(input);
        if (target.Length == 0) return null;
        var best = _lookup.Keys
            .Where(k => _byId[_lookup[k]].Info.Kind is FormatKinds.Text or FormatKinds.Binary)
            .Select(k => (k, d: Text.Levenshtein(k, target)))
            .OrderBy(x => x.d).ThenBy(x => x.k.Length).FirstOrDefault();
        var prefix = _lookup.Keys.FirstOrDefault(k => k.StartsWith(target, StringComparison.OrdinalIgnoreCase) && _byId[_lookup[k]].Info.Kind is FormatKinds.Text or FormatKinds.Binary);
        if (best.k != null && best.d <= Math.Max(2, target.Length / 3)) return _lookup[best.k];
        return prefix != null ? _lookup[prefix] : null;
    }

    /// <summary>Text formats in libse registry order, fresh instances (formats carry per-load state).</summary>
    public IEnumerable<(FormatInfo Info, SubtitleFormat Format)> DetectionCandidates() =>
        _detectOrder.Select(id => (_byId[id].Info, _byId[id].Factory()));

    /// <summary>
    /// Readable binary formats in detection order (plan §3): formats whose extension matches the
    /// caller's first, then the magic-checked ones (EBU GSI, PAC/FPC), then the rest in libse
    /// order, and Cavena 890 always last (its IsMine accepts garbage).
    /// </summary>
    public IEnumerable<(FormatInfo Info, SubtitleFormat Format)> BinaryCandidates(string? callerExtension)
    {
        var ext = (callerExtension ?? "").ToLowerInvariant();
        bool Matches(string id) => ext.Length > 0 && (_byId[id].Info.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase)
                                                      || _byId[id].Info.AlternateExtensions.Any(a => a.Equals(ext, StringComparison.OrdinalIgnoreCase)));
        string[] magic = ["ebustl", "pacscreenelectronics", "pacunicodeunipac"];
        var ordered = _binaryOrder.Where(id => id != "cavena890" && Matches(id))
            .Concat(magic.Where(_binaryOrder.Contains))
            .Concat(_binaryOrder.Where(id => id != "cavena890"))
            .Append("cavena890")
            .Where(_binaryOrder.Contains)
            .Distinct();
        return ordered.Select(id => (_byId[id].Info, _byId[id].Factory()));
    }
}

public static class Text
{
    public static int Levenshtein(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) d[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var prev = d[0];
            d[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return d[b.Length];
    }

    public static string? Suggest(string input, IEnumerable<string> options)
    {
        options = options.ToList();
        var prefix = options.FirstOrDefault(o => input.Length >= 4 && o.StartsWith(input, StringComparison.OrdinalIgnoreCase));
        if (prefix != null) return prefix;
        var best = options.Select(o => (o, d: Levenshtein(o.ToLowerInvariant(), input.ToLowerInvariant())))
            .OrderBy(x => x.d).FirstOrDefault();
        return best.o != null && best.d <= Math.Max(2, input.Length / 3) ? best.o : null;
    }
}
