using System.Globalization;
using System.Text;
using Nikse.SubtitleEdit.Core.Common;

namespace SubtitleEditApi.Endpoints;

/// <summary>
/// Proves the ICU data shipped in the image is complete enough for libse: culture-specific casing,
/// collation tailorings, culture-aware search, number formats, display names, normalization,
/// time zones and libse's own language/encoding detection. The image trims ICU data that .NET
/// never reads (converters, transliteration, unit names, break dictionaries, ...), so this runs
/// at startup and gates /readyz. A failing probe names itself.
/// </summary>
public static class GlobalizationCheck
{
    public static IReadOnlyList<string> Run()
    {
        var failures = new List<string>();
        void Probe(string name, Func<bool> f)
        {
            try { if (!f()) failures.Add(name); }
            catch (Exception ex) { failures.Add($"{name} ({ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()})"); }
        }

        static CultureInfo C(string name) => CultureInfo.GetCultureInfo(name);
        Probe("casing-tr", () => "i".ToUpper(C("tr-TR")) == "\u0130" && "I".ToLower(C("tr-TR")) == "\u0131");
        Probe("casing-el", () => "\u03AC".ToUpper(C("el-GR")) == "\u0386");
        Probe("collation-root", () => string.Compare("a", "B", C("en-US"), CompareOptions.None) < 0);
        Probe("collation-sv", () => string.Compare("\u00E4", "z", C("sv-SE"), CompareOptions.None) > 0
                                    && string.Compare("\u00E4", "z", C("de-DE"), CompareOptions.None) < 0);
        Probe("collation-ja", () => C("ja-JP").CompareInfo.Compare("\u3042", "\u30A2", CompareOptions.IgnoreKanaType) == 0);
        Probe("search", () => C("en-US").CompareInfo.IndexOf("caf\u00E9 au lait", "e\u0301") == 3
                              && C("en-US").CompareInfo.IndexOf("HELLO world", "hello", CompareOptions.IgnoreCase) == 0);
        Probe("numbers", () => 1234.5.ToString("N1", C("de-DE")) == "1.234,5");
        Probe("dates", () => new DateTime(2026, 1, 5).ToString("MMMM", C("fr-FR")) == "janvier");
        Probe("names", () => C("nl-BE").NativeName.StartsWith("Nederlands", StringComparison.Ordinal)
                             && new RegionInfo("BE").EnglishName == "Belgium");
        Probe("normalization", () => "e\u0301".Normalize(NormalizationForm.FormC) == "\u00E9");
        Probe("idn", () => new IdnMapping().GetAscii("b\u00FCcher.example") == "xn--bcher-kva.example");
        Probe("timezone", () => TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid").BaseUtcOffset == TimeSpan.FromHours(1)
                                && TimeZoneInfo.TryConvertIanaIdToWindowsId("Europe/Madrid", out var w) && w == "Romance Standard Time");
        Probe("codepages", () => Encoding.GetEncoding(1252).GetString([0x80]) == "\u20AC");
        Probe("libse-language", () => Detect(French) == "fr" && Detect(German) == "de" && Detect(Turkish) == "tr");
        return failures;
    }

    private static string? Detect(string[] lines)
    {
        var s = new Subtitle();
        for (var i = 0; i < lines.Length; i++)
            s.Paragraphs.Add(new Paragraph(lines[i], i * 3000, i * 3000 + 2500));
        return LanguageAutoDetect.AutoDetectGoogleLanguageOrNull(s);
    }

    private static readonly string[] French =
    [
        "Je ne sais pas ce que tu veux dire.", "Il est dans la maison avec les enfants.",
        "Nous avons besoin de toi, mais pas maintenant.", "Est-ce que vous pouvez m'aider, s'il vous plaît ?",
        "C'est pour ça que je suis venu ici.", "Elle a dit qu'il était très fatigué.",
    ];
    private static readonly string[] German =
    [
        "Ich weiß nicht, was du meinst.", "Er ist mit den Kindern im Haus.",
        "Wir brauchen dich, aber nicht jetzt.", "Kannst du mir bitte helfen?",
        "Das ist der Grund, warum ich hier bin.", "Sie hat gesagt, dass er sehr müde war.",
    ];
    private static readonly string[] Turkish =
    [
        "Ne demek istediğini bilmiyorum.", "O çocuklarla birlikte evde.",
        "Sana ihtiyacımız var ama şimdi değil.", "Bana yardım edebilir misin lütfen?",
        "Bu yüzden buraya geldim, bir şey yok.", "Çok yorgun olduğunu söyledi, ben de evet dedim.",
    ];
}
