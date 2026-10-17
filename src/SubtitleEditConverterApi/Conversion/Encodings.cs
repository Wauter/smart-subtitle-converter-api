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
