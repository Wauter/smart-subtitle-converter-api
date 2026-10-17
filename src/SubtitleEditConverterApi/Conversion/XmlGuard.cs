using System.Xml;
using SubtitleEditConverterApi.Infrastructure;

namespace SubtitleEditConverterApi.Conversion;

/// <summary>
/// libse builds its own XmlDocument instances, so every XML-looking input is pre-screened with a
/// hardened reader first (design §6.2): DTDs are prohibited and no resolver is used, which blocks
/// XXE and entity-expansion ("billion laughs") payloads before libse ever sees them.
/// </summary>
public static class XmlGuard
{
    public static void Screen(string text)
    {
        var start = text.AsSpan().TrimStart();
        if (start.Length == 0 || start[0] != '<') return;
        var looksLikeDtd = text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            IgnoreComments = true,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            while (reader.Read()) { }
        }
        catch (XmlException ex) when (looksLikeDtd || ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(422, ErrorCodes.UnsafeXml, "The input contains a DTD or entity declarations.", "DTDs are not allowed. Remove the <!DOCTYPE ...> block.", ex);
        }
        catch (XmlException)
        {
            // Not well-formed XML (e.g. SAMI/HTML-ish formats): let libse decide.
        }
    }
}
