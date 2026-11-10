using System.Xml;
using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Conversion;

/// <summary>
/// libse builds its own XmlDocument instances (XmlDocument.LoadXml, which parses DTDs), so every decoded text input is
/// screened before libse sees it (design §6.2, H5):
/// 1. Any DOCTYPE or ENTITY declaration, anywhere in the text, is refused (no subtitle format needs one). This does not
///    depend on where the XML starts, so leading BOMs, zero-width or other invisible characters can't hide it.
/// 2. Text that starts with '&lt;' after stripping every leading BOM, zero-width and whitespace character is read with a
///    hardened XmlReader: DtdProcessing.Prohibit, no XmlResolver, a small entity-expansion cap. Malformed XML is left to libse.
/// Every XML parser in this API uses <see cref="SafeSettings"/>.
/// </summary>
public static class XmlGuard
{
    /// <summary>The only XmlReaderSettings this API parses XML with: no DTD, no resolver, bounded entity expansion.</summary>
    public static XmlReaderSettings SafeSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        MaxCharactersInDocument = 0,
        IgnoreComments = true,
    };

    /// <summary>Characters skipped before deciding whether text looks like XML: every BOM, zero-width and whitespace character.</summary>
    public static bool IsInvisible(char c) => char.IsWhiteSpace(c) || c is '\uFEFF' or '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\u180E' or '\u00AD' or '\uFFFE';

    public static ReadOnlySpan<char> StripLeading(ReadOnlySpan<char> text)
    {
        var i = 0;
        while (i < text.Length && IsInvisible(text[i])) i++;
        return text[i..];
    }

    public static void Screen(string text)
    {
        if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
            throw Unsafe(null);
        var start = StripLeading(text);
        if (start.Length == 0 || start[0] != '<') return;
        try
        {
            using var reader = XmlReader.Create(new StringReader(start.ToString()), SafeSettings());
            while (reader.Read()) { }
        }
        catch (XmlException ex) when (ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("entit", StringComparison.OrdinalIgnoreCase) && ex.Message.Contains("expan", StringComparison.OrdinalIgnoreCase))
        {
            throw Unsafe(ex);
        }
        catch (XmlException)
        {
            // Not well-formed XML (e.g. SAMI/HTML-ish formats): let libse decide.
        }
    }

    private static ApiException Unsafe(Exception? inner) => new(422, ErrorCodes.UnsafeXml, "The input contains a DTD or entity declarations (<!DOCTYPE ...> or <!ENTITY ...>).",
        "DTDs and entity declarations are never processed, because they can be used to read server files or exhaust memory. Remove the <!DOCTYPE ...> block; subtitle formats don't need it.", inner);
}
