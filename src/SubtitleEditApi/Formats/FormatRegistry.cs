using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using SubtitleEditApi.Infrastructure;

namespace SubtitleEditApi.Formats;

public sealed record FormatInfo(
    string Id, string Name, string[] Aliases, string Extension, string[] AlternateExtensions,
    bool TextBased, bool FrameBased, bool StyleSupport, bool PositionSupport,
    bool CanRead, bool CanWrite, string Source, string MediaType);

/// <summary>
/// The set of formats the API exposes (design §2.2). V1 exposes libse's text formats only;
/// binary/image formats are listed with <c>textBased=false</c> so callers get a precise 422.
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
        ["cavena"] = "Cavena 890", ["sup"] = "Blu-ray sup", ["bluraysup"] = "Blu-ray sup", ["vobsub"] = "VobSub",
    };

    private static readonly HashSet<string> PositionFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "advancedsubstationalpha", "substationalpha", "webvtt", "timedtext10", "netflixtimedtext", "dfxpbasic", "itunestimedtext", "imsc11", "smptett2052",
    };

    private readonly Dictionary<string, (FormatInfo Info, Func<SubtitleFormat> Factory)> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _detectOrder = [];

    public IReadOnlyList<FormatInfo> All { get; }

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnum();

    public static string ToId(string name) => NonAlnum().Replace(name.ToLowerInvariant(), "");

    public FormatRegistry()
    {
        var list = new List<FormatInfo>();
        void Add(SubtitleFormat f, bool textBased)
        {
            var id = ToId(f.Name);
            if (id.Length == 0 || _byId.ContainsKey(id)) return;
            var aliases = AliasToName.Where(a => ToId(a.Value) == id).Select(a => a.Key.ToLowerInvariant()).ToArray();
            var type = f.GetType();
            var info = new FormatInfo(id, f.Name, aliases, f.Extension, f.AlternateExtensions?.ToArray() ?? [],
                textBased, f.IsFrameBased, f.HasStyleSupport, PositionFormats.Contains(id),
                CanRead: true, CanWrite: textBased, Source: "libse", MediaType: MediaTypeFor(id));
            _byId[id] = (info, () => (SubtitleFormat)Activator.CreateInstance(type)!);
            list.Add(info);
            if (textBased) _detectOrder.Add(id);
        }

        foreach (var f in SubtitleFormat.AllSubtitleFormats)
        {
            Add(f, f.IsTextBased);
        }
        foreach (var f in SubtitleFormat.GetBinaryFormats(true))
        {
            Add(f, false);
        }
        foreach (var (id, _) in _byId) _lookup[id] = id;
        foreach (var (alias, name) in AliasToName)
        {
            var id = ToId(name);
            if (_byId.ContainsKey(id)) _lookup.TryAdd(ToId(alias), id);
        }
        All = list.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    public static string MediaTypeFor(string id) => id switch
    {
        "subrip" => "application/x-subrip",
        "webvtt" => "text/vtt",
        "advancedsubstationalpha" or "substationalpha" => "text/x-ssa",
        "timedtext10" or "dfxpbasic" or "netflixtimedtext" or "itunestimedtext" or "smptett2052" => "application/ttml+xml",
        "sami" => "application/smil+xml",
        _ => "text/plain",
    };

    /// <summary>Image/container formats Subtitle Edit knows but V1 cannot handle (not in libse's text/binary lists).</summary>
    private static readonly HashSet<string> KnownImageFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "sup", "bluraysup", "vobsub", "idx", "dvdsup", "bdnxml", "imscimage", "dost", "fcpimage", "dcinemainterop", "dcinemasmpte2014", "webvttthumbnail", "mkv", "mks", "mp4", "ts", "mxf", "customtext", "customtextformat",
    };

    public static bool IsKnownUnsupported(string idOrAlias) => KnownImageFormats.Contains(ToId(idOrAlias));

    public FormatInfo? Find(string idOrAlias) =>
        _lookup.TryGetValue(ToId(idOrAlias), out var id) ? _byId[id].Info : null;

    /// <summary>Resolve a format for reading or writing; throws a 422 with a "did you mean" hint.</summary>
    public (FormatInfo Info, SubtitleFormat Format) Resolve(string idOrAlias, string pointer)
    {
        var info = Find(idOrAlias);
        if (info == null && IsKnownUnsupported(idOrAlias))
        {
            throw new ApiException(422, ErrorCodes.FormatNotSupported, $"'{idOrAlias}' is image-based or a container format.",
                "V1 supports text formats only (see GET /v1/formats); OCR and container extraction are on the roadmap.")
            { Errors = [new FieldError(pointer, $"'{idOrAlias}' is not supported in V1")] };
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
        if (!info.TextBased)
        {
            throw new ApiException(422, ErrorCodes.FormatNotSupported, $"'{info.Id}' is a binary/image-based format.",
                "V1 supports text formats only; see GET /v1/formats?textOnly=true. Binary formats are on the roadmap.")
            {
                Errors = [new FieldError(pointer, $"'{info.Id}' is not text-based")],
            };
        }
        return (info, _byId[info.Id].Factory());
    }

    public string? Suggest(string input)
    {
        var target = ToId(input);
        if (target.Length == 0) return null;
        var best = _lookup.Keys
            .Where(k => _byId[_lookup[k]].Info.TextBased)
            .Select(k => (k, d: Text.Levenshtein(k, target)))
            .OrderBy(x => x.d).ThenBy(x => x.k.Length).FirstOrDefault();
        var prefix = _lookup.Keys.FirstOrDefault(k => k.StartsWith(target, StringComparison.OrdinalIgnoreCase) && _byId[_lookup[k]].Info.TextBased);
        if (best.k != null && best.d <= Math.Max(2, target.Length / 3)) return _lookup[best.k];
        return prefix != null ? _lookup[prefix] : null;
    }

    /// <summary>Text formats in libse registry order, fresh instances (formats carry per-load state).</summary>
    public IEnumerable<(FormatInfo Info, SubtitleFormat Format)> DetectionCandidates() =>
        _detectOrder.Select(id => (_byId[id].Info, _byId[id].Factory()));
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
