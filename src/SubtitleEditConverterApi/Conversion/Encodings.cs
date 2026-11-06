using System.Text;
using Nikse.SubtitleEdit.Core.Common;
using SubtitleEditConverterApi.Infrastructure;

namespace SubtitleEditConverterApi.Conversion;

public sealed record DecodedText(string Text, Encoding Encoding, string EncodingName, string Confidence, bool HadBom);

/// <summary>Input encoding detection and output encoding selection (design §2.3, step 2 and 5).</summary>
public static class Encodings
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool IsKnown(string name, bool output)
    {
        if (output && name.ToLowerInvariant() is "utf-8" or "utf8" or "utf-8-no-bom" or "utf8-no-bom" or "source") return true;
        return TryGet(name) != null;
    }

    public static Encoding? TryGet(string name)
    {
        try
        {
            return int.TryParse(name, out var cp) ? Encoding.GetEncoding(cp) : Encoding.GetEncoding(name);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static DecodedText Decode(byte[] bytes, string? forced, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(forced))
        {
            var enc = TryGet(forced) ?? throw new ApiException(400, ErrorCodes.InvalidProfile, $"Unknown encoding '{forced}'.", "Use e.g. utf-8, windows-1252, iso-8859-1.");
            var bomLen = enc.Preamble.Length > 0 && bytes.AsSpan().StartsWith(enc.Preamble) ? enc.Preamble.Length : 0;
            return new DecodedText(enc.GetString(bytes, bomLen, bytes.Length - bomLen), enc, enc.WebName, "forced", bomLen > 0);
        }

        // 1. BOM
        foreach (var enc in new Encoding[] { new UTF8Encoding(true), new UTF32Encoding(false, true), Encoding.Unicode, Encoding.BigEndianUnicode, new UTF32Encoding(true, true) })
        {
            var pre = enc.Preamble;
            if (bytes.AsSpan().StartsWith(pre))
            {
                return new DecodedText(enc.GetString(bytes, pre.Length, bytes.Length - pre.Length), enc, enc.WebName, "bom", true);
            }
        }

        // 2. Valid UTF-8
        try
        {
            var text = StrictUtf8.GetString(bytes);
            return new DecodedText(text, new UTF8Encoding(false), "utf-8", "utf8-valid", false);
        }
        catch (DecoderFallbackException)
        {
        }

        // 3. Profile fallback, else libse's ANSI heuristic.
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            var enc = TryGet(fallback) ?? throw new ApiException(400, ErrorCodes.InvalidProfile, $"Unknown encoding '{fallback}'.", "Use e.g. windows-1252.");
            return new DecodedText(enc.GetString(bytes), enc, enc.WebName, "fallback", false);
        }
        // 4. Probe ANSI/DBCS encodings by the words they produce (TextProbe); a conflict is rejected, never guessed.
        if (ProbeAnsi(bytes) is { } probed) return probed;

        // 5. Nothing formed words: libse's ANSI heuristic, reported as "heuristic" (SourceChecks warns encoding-guessed).
        Encoding ansi;
        try
        {
            ansi = LanguageAutoDetect.DetectAnsiEncoding(bytes) ?? Encoding.GetEncoding(1252);
        }
        catch
        {
            ansi = Encoding.GetEncoding(1252);
        }
        return new DecodedText(ansi.GetString(bytes), ansi, ansi.WebName, "heuristic", false);
    }

    private static readonly string[] Western = ["en", "da", "no", "nb", "sv", "es", "it", "fr", "de", "nl", "pt", "is", "fi", "af", "ca", "id", "tl", "sq"];
    private static readonly string[] CentralEuropean = ["cs", "sk", "pl", "hu", "hr", "sl", "ro", "sq", "sr"];

    /// <summary>
    /// The encodings probed when the input is neither BOM-marked nor valid UTF-8, in preference order (Windows code
    /// pages before their ISO-8859 siblings), with the languages each one serves.
    /// </summary>
    internal static readonly (int CodePage, string[] Languages)[] ProbeSet =
    [
        (1252, Western), (1250, CentralEuropean), (1251, ["ru", "uk", "bg", "mk", "sr-cy"]), (1253, ["el"]), (1254, ["tr"]),
        (1255, ["he"]), (1256, ["ar", "fa", "ur"]), (1257, ["lt", "lv", "et"]), (874, ["th"]),
        (28591, Western), (28592, CentralEuropean), (28597, ["el"]), (28599, ["tr"]),
        (54936, ["zh"]), (950, ["zh"]), (932, ["ja"]), (51949, ["ko"]),
    ];

    /// <summary>ISO-8859 page -> its Windows sibling: when both match, the Windows code page is kept.</summary>
    private static readonly Dictionary<int, int> WindowsSibling = new() { [28591] = 1252, [28592] = 1250, [28597] = 1253, [28599] = 1254 };

    /// <summary>
    /// Decode with every encoding of <see cref="ProbeSet"/> and let <see cref="TextProbe"/> decide. Decodes with
    /// U+FFFD, C1 controls or private-use characters are dropped when a clean one exists. Same text from several
    /// encodings is one choice (windows-1252 first). Null when no encoding forms words; 422 ambiguous-encoding when
    /// different texts remain.
    /// </summary>
    internal static DecodedText? ProbeAnsi(byte[] bytes)
    {
        var decoded = new List<(Encoding Enc, string Text, string[] Langs, int Order, bool Clean)>();
        for (var i = 0; i < ProbeSet.Length; i++)
        {
            Encoding enc;
            try { enc = Encoding.GetEncoding(ProbeSet[i].CodePage); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
            var text = enc.GetString(bytes);
            decoded.Add((enc, text, ProbeSet[i].Languages, i, Clean(text)));
        }
        if (decoded.Any(d => d.Clean)) decoded.RemoveAll(d => !d.Clean);
        if (decoded.Count == 0) return null;
        var scores = decoded.Select(d => TextProbe.Score(new ProbeCandidate(d.Enc.WebName, d.Text, d.Langs, d.Order))).ToList();
        var units = decoded[0].Text.SplitToLines().Count(l => l.Any(char.IsLetter));
        var outcome = TextProbe.Decide(scores, TextProbe.MinHits(units), strong =>
        {
            var cps = strong.Select(s => ProbeSet[s.Candidate.Order].CodePage).ToHashSet();
            return strong.Where(s => !(WindowsSibling.TryGetValue(ProbeSet[s.Candidate.Order].CodePage, out var win) && cps.Contains(win))).ToList();
        });
        switch (outcome.Verdict)
        {
            case ProbeVerdict.Unique:
                var w = decoded.First(d => d.Order == outcome.Winner!.Candidate.Order);
                return new DecodedText(w.Text, w.Enc, w.Enc.WebName, "probed", false);
            case ProbeVerdict.Conflict:
                var names = outcome.Matches.Select(m => m.Name).Distinct().ToArray();
                var ex = new ApiException(422, ErrorCodes.AmbiguousEncoding,
                    "The text is not UTF-8, and it reads as words in more than one encoding (" +
                    string.Join(", ", outcome.Matches.Select(m => $"{m.Name}: {m.Language}, {m.Hits} word(s)")) + "), which decode to different text. The encoding is not guessed.",
                    $"Set profile.encoding.input to the file's encoding (candidates: {string.Join(", ", names)}); for /v1/detect send encoding. Saving the file as UTF-8 avoids the question.")
                {
                    Errors = [new FieldError("/encoding/input", "Auto-detection could not decide the text encoding.", "Set it to the encoding the file was written in.")],
                };
                ex.Extensions["candidates"] = names;
                ex.Extensions["probe"] = "text-encoding";
                ex.Debug["scores"] = scores.Select(s => new { encoding = s.Name, language = s.Language, hits = s.Hits }).ToList();
                throw ex;
            default:
                return null;
        }
    }

    private static bool Clean(string text)
    {
        foreach (var c in text)
        {
            if (c == '\uFFFD' || c is >= '\u0080' and <= '\u009F' || c is >= '\uE000' and <= '\uF8FF') return false;
        }
        return true;
    }

    public static (Encoding Encoding, string Name) ForOutput(string? output, DecodedText source)
    {
        switch ((output ?? "utf-8").Trim().ToLowerInvariant())
        {
            case "utf-8" or "utf8": return (new UTF8Encoding(true), "utf-8");
            case "utf-8-no-bom" or "utf8-no-bom": return (new UTF8Encoding(false), "utf-8-no-bom");
            case "source":
                return source.Encoding is UTF8Encoding
                    ? (new UTF8Encoding(source.HadBom), source.HadBom ? "utf-8" : "utf-8-no-bom")
                    : (source.Encoding, source.EncodingName);
            default:
                var enc = TryGet(output!) ?? throw new ApiException(400, ErrorCodes.InvalidProfile, $"Unknown output encoding '{output}'.", "Use utf-8, utf-8-no-bom, source, or an encoding name.");
                return (enc, enc.WebName);
        }
    }

    public static bool IsUnicode(Encoding e) => e is UTF8Encoding or UnicodeEncoding or UTF32Encoding;

    public static byte[] Encode(string text, Encoding enc)
    {
        var pre = enc.Preamble;
        var body = enc.GetBytes(text);
        if (pre.Length == 0) return body;
        var all = new byte[pre.Length + body.Length];
        pre.CopyTo(all);
        body.CopyTo(all.AsSpan(pre.Length));
        return all;
    }
}
