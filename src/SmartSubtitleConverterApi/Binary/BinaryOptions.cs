using System.Globalization;
using System.Text;
using System.Text.Json;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using SmartSubtitleConverterApi.Conversion;
using SmartSubtitleConverterApi.Infrastructure;
using SmartSubtitleConverterApi.Profiles;

namespace SmartSubtitleConverterApi.Binary;

/// <summary>
/// formatOptions for binary targets (plan §2): value tables, semantic validation
/// (format-option-invalid), the EBU GSI header builder and the global libse settings.
/// </summary>
public static class BinaryOptions
{
    public static readonly string[] EbuCodePages = ["437", "850", "860", "863", "865"];
    public static readonly Dictionary<string, string> EbuCharacterTables = new() { ["latin"] = "00", ["cyrillic"] = "01", ["arabic"] = "02", ["greek"] = "03", ["hebrew"] = "04" };
    public static readonly Dictionary<string, string> EbuDisplayStandards = new() { ["open"] = "0", ["teletext1"] = "1", ["teletext2"] = "2" };
    public static readonly double[] EbuFrameRates = [23, 23.976, 24, 25, 29.97, 30];

    /// <summary>ISO 639-1 -> EBU Tech 3264 language code, the same table libse uses when it auto-detects.</summary>
    public static readonly Dictionary<string, string> EbuLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sq"] = "01", ["br"] = "02", ["ca"] = "03", ["hr"] = "04", ["cy"] = "05", ["cs"] = "06", ["da"] = "07", ["de"] = "08", ["en"] = "09", ["es"] = "0A",
        ["eo"] = "0B", ["et"] = "0C", ["eu"] = "0D", ["fo"] = "0E", ["fr"] = "0F", ["fy"] = "10", ["ga"] = "11", ["gd"] = "12", ["gl"] = "13", ["is"] = "14",
        ["it"] = "15", ["la"] = "17", ["lv"] = "18", ["lb"] = "19", ["lt"] = "1A", ["hu"] = "1B", ["mt"] = "1C", ["nl"] = "1D", ["nb"] = "1E", ["no"] = "1E",
        ["oc"] = "1F", ["pl"] = "20", ["pt"] = "21", ["ro"] = "22", ["rm"] = "23", ["sr"] = "24", ["sk"] = "25", ["sl"] = "26", ["fi"] = "27", ["sv"] = "28", ["tr"] = "29",
    };

    public static readonly string[] PacCodePages =
        ["latin", "greek", "czech", "arabic", "hebrew", "thai", "cyrillic", "chinese-traditional", "chinese-simplified", "korean", "japanese", "turkish", "portuguese"];

    public static readonly Dictionary<string, (int Id, string Script)> CavenaLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = (Cavena890.LanguageIdEnglish, "latin"), ["da"] = (Cavena890.LanguageIdDanish, "latin"), ["sv"] = (Cavena890.LanguageIdSwedish, "latin"),
        ["no"] = (Cavena890.LanguageIdNorwegian, "latin"), ["nb"] = (Cavena890.LanguageIdNorwegian, "latin"), ["ro"] = (Cavena890.LanguageIdRomanian, "latin"),
        ["ru"] = (Cavena890.LanguageIdRussian, "cyrillic"), ["el"] = (Cavena890.LanguageIdGreek, "greek"), ["ar"] = (Cavena890.LanguageIdArabic, "arabic"),
        ["he"] = (Cavena890.LanguageIdHebrew, "hebrew"), ["zh-hant"] = (Cavena890.LanguageIdChineseTraditional, "chinese-traditional"),
        ["zh-hans"] = (Cavena890.LanguageIdChineseSimplified, "chinese-simplified"),
    };

    public static readonly string[] CavenaLanguageNames = ["en", "da", "sv", "no", "nb", "ro", "ru", "el", "ar", "he", "zh-hant", "zh-hans"];

    public const string TimecodePattern = "^[0-9]{2}:[0-5][0-9]:[0-5][0-9]:[0-9]{2}$";

    /// <summary>PAC code page from a name or 0..12 (null = not given, -1 = auto, -2 = invalid).</summary>
    public static int? PacCodePage(JsonElement? e, bool allowAuto)
    {
        if (e is not { } v || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt32(out var n) && n is >= 0 and <= 12 ? n : -2;
        if (v.ValueKind != JsonValueKind.String) return -2;
        var s = v.GetString()!.Trim();
        if (allowAuto && s.Equals("auto", StringComparison.OrdinalIgnoreCase)) return -1;
        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var k)) return k is >= 0 and <= 12 ? k : -2;
        var i = Array.FindIndex(PacCodePages, p => p.Equals(s, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : -2;
    }

    public static string PacCodePageName(int cp) => cp >= 0 && cp < PacCodePages.Length ? PacCodePages[cp] : "auto";

    // ---------------- semantic validation (400 format-option-invalid) ----------------

    public static List<FieldError> Validate(Profile p)
    {
        var errors = new List<FieldError>();
        var fo = p.FormatOptions;
        if (fo == null) return errors;
        if (fo.EbuStl is { } e)
        {
            const string b = "/formatOptions/ebuStl";
            if (e.FrameRate is { } fr && !EbuFrameRates.Any(x => Math.Abs(x - fr) < 0.001))
                errors.Add(new($"{b}/frameRate", $"{fr} is not an EBU STL frame rate.", "Use 23.976 (or 23), 24, 25, 29.97 or 30."));
            if (e.FrameRate is { } fr2 && p.Target?.Fps is { } tfps && Math.Abs(EbuFps(fr2) - tfps) > 0.01)
                errors.Add(new($"{b}/frameRate", $"frameRate {fr2} conflicts with target.fps {tfps}.", "Drop one of them; frameRate defaults to target.fps."));
            if (e.FrameRate == null && p.Target?.Fps is { } t2 && !EbuFrameRates.Any(x => Math.Abs(x - t2) < 0.001))
                errors.Add(new("/target/fps", $"target.fps {t2} is not an EBU STL frame rate.", "Use 23.976, 24, 25, 29.97 or 30, or set formatOptions.ebuStl.frameRate."));
            if (e.ReadFrameRate is { } rfr && rfr <= 20)
                errors.Add(new($"{b}/readFrameRate", "readFrameRate must be > 20.", "Typical values: 23.976, 24, 25, 29.97, 30."));
            if (e.Language is { } lang && !lang.Equals("auto", StringComparison.OrdinalIgnoreCase) && !EbuLanguages.ContainsKey(lang) && !IsEbuHex(lang))
                errors.Add(new($"{b}/language", $"'{lang}' has no EBU STL language code.", $"Use an ISO 639-1 code ({string.Join(", ", EbuLanguages.Keys.Take(12))}, ...), a two-digit EBU hex code like \"0A\", or \"auto\"."));
            var cp = Encoding.GetEncoding(int.Parse(e.CodePage ?? "850", CultureInfo.InvariantCulture));
            foreach (var (name, value) in new[] { ("originalProgrammeTitle", e.OriginalProgrammeTitle), ("originalEpisodeTitle", e.OriginalEpisodeTitle),
                         ("translatedProgrammeTitle", e.TranslatedProgrammeTitle), ("translatorsName", e.TranslatorsName), ("publisher", e.Publisher) })
            {
                if (value != null && cp.GetByteCount(value) > 32)
                    errors.Add(new($"{b}/{name}", $"{name} is {cp.GetByteCount(value)} bytes in code page {e.CodePage ?? "850"}; the GSI field holds 32.", "Shorten it."));
            }
            if (e.TimecodeStartOfProgramme is { } tc && FrameOf(tc) is { } f && f >= Math.Ceiling(EbuFps(e.FrameRate ?? p.Target?.Fps ?? 25)))
                errors.Add(new($"{b}/timecodeStartOfProgramme", $"Frame {f} is out of range for {EbuFps(e.FrameRate ?? p.Target?.Fps ?? 25)} fps.", "Frames run from 00 to fps-1."));
        }
        if (fo.Pac is { } pac)
        {
            if (PacCodePage(pac.CodePage, false) == -2)
                errors.Add(new("/formatOptions/pac/codePage", "Unknown PAC code page.", $"Use 0..12 or one of: {string.Join(", ", PacCodePages)}."));
            if (PacCodePage(pac.InputCodePage, true) == -2)
                errors.Add(new("/formatOptions/pac/inputCodePage", "Unknown PAC code page.", $"Use \"auto\", 0..12 or one of: {string.Join(", ", PacCodePages)}."));
        }
        if (fo.Cavena890 is { } c)
        {
            foreach (var (name, value, max) in new[] { ("title", c.Title, 28), ("originalTitle", c.OriginalTitle, 28), ("translator", c.Translator, 28), ("comment", c.Comment, 24) })
            {
                if (value == null) continue;
                if (value.Length > max) errors.Add(new($"/formatOptions/cavena890/{name}", $"{name} has {value.Length} characters; Cavena 890 holds {max}.", "Shorten it."));
                else if (value.Any(ch => ch > 127)) errors.Add(new($"/formatOptions/cavena890/{name}", $"{name} must be ASCII (libse writes it as ASCII).", "Remove accents and non-Latin characters."));
            }
        }
        return errors;
    }

    private static bool IsEbuHex(string s) => s.Length == 2 && s.All(ch => ch is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static int? FrameOf(string tc) => tc.Length == 11 && int.TryParse(tc[9..], out var f) ? f : null;

    /// <summary>EBU's "23" disk format is 23.976 fps.</summary>
    public static double EbuFps(double fr) => Math.Abs(fr - 23) < 0.001 ? 23.976 : fr;

    // ---------------- frame-rate-changed ----------------

    public const string FrameRateChangedCode = "frame-rate-changed";

    /// <summary>
    /// The frame rate a reader assumes for a written GSI: libse's own Disk Format Code getter
    /// (STL23/24/25/29/30/35/48/50/60, anything else 30), because libse doesn't store
    /// FrameRateFromSaveDialog in the file. STL23 is taken as 23.976, the API's convention (<see cref="EbuFps"/>).
    /// </summary>
    public static double ImpliedFps(string? diskFormatCode) =>
        EbuFps(new Ebu.EbuGeneralSubtitleInformation { DiskFormatCode = diskFormatCode ?? "", FrameRateFromSaveDialog = 0 }.FrameRate);

    /// <summary>
    /// frame-rate-changed: the written Disk Format Code implies a frame rate that differs (by more
    /// than 0.01 fps) from the intended one: the requested rate (formatOptions.ebuStl.frameRate, else
    /// target.fps) or, when none was requested, the source's rate (the rate an EBU source was read at,
    /// else profile source.fps). Null when nothing is known about the intended rate or the rates agree.
    /// </summary>
    public static Warning? FrameRateChanged(string? writtenDiskFormatCode, double? requestedFps, double? sourceFps)
    {
        var (intended, what) = requestedFps is > 0 and { } r ? (EbuFps(r), "requested") : sourceFps is > 0 and { } s ? (s, "source") : (0d, "");
        if (intended <= 0) return null;
        var implied = ImpliedFps(writtenDiskFormatCode);
        if (Math.Abs(implied - intended) <= 0.01) return null;
        var dfc = (writtenDiskFormatCode ?? "").Trim();
        return new Warning(FrameRateChangedCode,
            $"The EBU STL output declares disk format code {dfc}, which readers take as {Fps(implied)} fps, but the {what} frame rate is {Fps(intended)} fps. " +
            "EBU Tech 3264 only defines STL25.01 and STL30.01, so 29.97 has no code of its own. " +
            "Set formatOptions.ebuStl.frameRate to the rate the file should declare, or headerFromSource:false to stop reusing the source header's code.");
    }

    private static string Fps(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ---------------- global settings (SettingsScope.Apply) ----------------

    public static void ApplyGlobals(FormatOptionsSpec? fo)
    {
        if (fo == null) return;
        var ss = Configuration.Settings.SubtitleSettings;
        if (fo.EbuStl is { } e)
        {
            if (e.Teletext?.UseBox is { } a) ss.EbuStlTeletextUseBox = a;
            if (e.Teletext?.DoubleHeight is { } b) ss.EbuStlTeletextUseDoubleHeight = b;
            if (e.Teletext?.MarginTop is { } c) ss.EbuStlMarginTop = c;
            if (e.Teletext?.MarginBottom is { } d) ss.EbuStlMarginBottom = d;
            if (e.Teletext?.NewLineRows is { } f) ss.EbuStlNewLineRows = f;
            if (e.RemoveEmptyLines is { } g) ss.EbuStlRemoveEmptyLines = g;
            if (e.ReadFrameRate is { } r) Ebu.OverrideReadFrameRate = r;
        }
        if (fo.Pac is { } p)
        {
            if (p.VerticalTop is { } a) ss.PacVerticalTop = a;
            if (p.VerticalCenter is { } b) ss.PacVerticalCenter = b;
            if (p.VerticalBottom is { } c) ss.PacVerticalBottom = c;
        }
        if (fo.Cavena890 is { } cv)
        {
            if (cv.Language is { } l && CavenaLanguages.TryGetValue(l, out var id)) ss.CurrentCavena89LanguageId = id.Id;
            if (cv.Title is { } t) ss.CurrentCavena89Title = t;
            if (cv.OriginalTitle is { } o) ss.CurrentCavena890riginalTitle = o;
            if (cv.Translator is { } tr) ss.CurrentCavena890Translator = tr;
            if (cv.Comment is { } cm) ss.CurrentCavena89Comment = cm;
            if (cv.StartOfMessage is { } sm) ss.Cavena890StartOfMessage = sm;
        }
        if (fo.Cheetah?.AlwaysWriteEndTime is { } w) ss.CheetahCaptionAlwayWriteEndTime = w;
    }

    // ---------------- EBU GSI ----------------

    public static bool IsStlHeader(string? h) =>
        h is { Length: 1024 } && h[..3].All(char.IsAsciiDigit) && h.AsSpan(3, 3).SequenceEqual("STL");

    /// <summary>
    /// The GSI for an EBU write. EBU source + headerFromSource (default): the source GSI with the
    /// explicit options on top, written back into <paramref name="copy"/>.Header so libse keeps the
    /// teletext rows. Otherwise a fresh GSI: code page 850, DFC from frameRate/target.fps/25, language
    /// auto-detected like libse, teletext level 1 when the subtitle has colours.
    /// </summary>
    public static Ebu.EbuGeneralSubtitleInformation BuildEbuHeader(Subtitle copy, EbuStlOptions? o, double? targetFps, List<Warning> warnings)
    {
        o ??= new EbuStlOptions();
        if (IsStlHeader(copy.Header))
        {
            if (o.HeaderFromSource != false)
            {
                var src = Ebu.ReadHeader(EbuEncoding(copy.Header![..3]).GetBytes(copy.Header));
                var explicitFrameRate = o.FrameRate ?? targetFps;
                ApplyExplicit(src, o, copy, fresh: false, explicitFrameRate);
                var text = src.ToString();
                if (text.Length == 1024) copy.Header = text;
                return src;
            }
            copy.Header = string.Empty;
        }
        var h = new Ebu.EbuGeneralSubtitleInformation { CodePageNumber = "850" };
        ApplyExplicit(h, o, copy, fresh: true, o.FrameRate ?? targetFps ?? 25);
        return h;
    }

    private static void ApplyExplicit(Ebu.EbuGeneralSubtitleInformation h, EbuStlOptions o, Subtitle s, bool fresh, double? frameRate)
    {
        if (o.CodePage != null) h.CodePageNumber = o.CodePage;
        if (frameRate is { } fr)
        {
            var fps = EbuFps(fr);
            h.DiskFormatCode = fps switch { < 23.99 => "STL23.01", < 24.5 => "STL24.01", < 26 => "STL25.01", _ => "STL30.01" };
            h.FrameRateFromSaveDialog = fps;
        }
        if (o.DisplayStandard != null) h.DisplayStandardCode = EbuDisplayStandards[o.DisplayStandard];
        else if (fresh) h.DisplayStandardCode = s.Paragraphs.Any(p => p.Text?.Contains("<font color", StringComparison.OrdinalIgnoreCase) == true) ? "1" : "0";
        if (o.CharacterTable != null) h.CharacterCodeTableNumber = EbuCharacterTables[o.CharacterTable];
        if (o.Language != null && !o.Language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            h.LanguageCode = EbuLanguages.TryGetValue(o.Language, out var code) ? code : o.Language;
        else if (fresh)
            h.LanguageCode = s.Paragraphs.Count == 0 ? "00" : EbuLanguages.GetValueOrDefault(Language.DetectOrNull(s) ?? "", "09");
        if (o.OriginalProgrammeTitle != null) h.OriginalProgrammeTitle = Pad(o.OriginalProgrammeTitle, 32);
        if (o.OriginalEpisodeTitle != null) h.OriginalEpisodeTitle = Pad(o.OriginalEpisodeTitle, 32);
        if (o.TranslatedProgrammeTitle != null) h.TranslatedProgrammeTitle = Pad(o.TranslatedProgrammeTitle, 32);
        if (o.TranslatorsName != null) h.TranslatorsName = Pad(o.TranslatorsName, 32);
        if (o.Publisher != null) h.Publisher = Pad(o.Publisher, 32);
        if (o.CountryOfOrigin != null) h.CountryOfOrigin = Pad(o.CountryOfOrigin, 3);
        if (o.TimecodeStartOfProgramme != null) h.TimeCodeStartOfProgramme = o.TimecodeStartOfProgramme.Replace(":", "");
        if (o.MaxCharsPerRow is { } mc) h.MaximumNumberOfDisplayableCharactersInAnyTextRow = mc.ToString("D2", CultureInfo.InvariantCulture);
        if (o.MaxRows is { } mr) h.MaximumNumberOfDisplayableRows = mr.ToString("D2", CultureInfo.InvariantCulture);
    }

    private static string Pad(string s, int n) => s.Length >= n ? s[..n] : s.PadRight(n);

    private static Encoding EbuEncoding(string cpn) =>
        int.TryParse(cpn, out var cp) ? SafeEncoding(cp) : Encoding.Latin1;

    private static Encoding SafeEncoding(int cp)
    {
        try { return Encoding.GetEncoding(cp); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.Latin1; }
    }

    // ---------------- names for responses ----------------

    public static string EbuCharacterTableName(string cct) => EbuCharacterTables.FirstOrDefault(kv => kv.Value == cct).Key ?? cct;
    public static string EbuDisplayStandardName(string dsc) => EbuDisplayStandards.FirstOrDefault(kv => kv.Value == dsc).Key ?? dsc;

    public static string CavenaScript(int languageId) =>
        CavenaLanguages.Values.FirstOrDefault(v => v.Id == languageId).Script ?? "latin";

    /// <summary>The Cavena language the writer will use: the option, else libse's auto-detection.</summary>
    public static int CavenaLanguageFor(Subtitle s, Cavena890Options? o)
    {
        if (o?.Language is { } l && CavenaLanguages.TryGetValue(l, out var id)) return id.Id;
        var detected = Language.DetectOrNull(s);
        return detected switch
        {
            "ru" => Cavena890.LanguageIdRussian, "el" => Cavena890.LanguageIdGreek, "ar" => Cavena890.LanguageIdArabic,
            "he" => Cavena890.LanguageIdHebrew, "zh" => Cavena890.LanguageIdChineseSimplified, _ => Cavena890.LanguageIdEnglish,
        };
    }
}
