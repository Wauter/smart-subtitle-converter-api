using System.Text;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using SubtitleEditConverterApi.Conversion;
using SubtitleEditConverterApi.Profiles;

namespace SubtitleEditConverterApi.Binary;

/// <summary>
/// format-defaults-applied: identity/delivery header fields the request left unset, but that the written file
/// carries anyway (libse constructor defaults such as an EBU programme title "No Title" and country "USA", a
/// detected or fallback language, a Cavena title taken from the file name). Read from the bytes actually written,
/// so the warning reports what landed. Mechanical defaults (code page, dates, counters) are not reported.
/// </summary>
internal static class FormatDefaults
{
    public const string Code = "format-defaults-applied";

    /// <summary>A fresh EBU GSI (never one reused from an EBU source: those values come from the source file).</summary>
    public static Warning? Ebu(byte[] bytes, EbuStlOptions? o, Subtitle written)
    {
        Nikse.SubtitleEdit.Core.SubtitleFormats.Ebu.EbuGeneralSubtitleInformation h;
        try { h = Nikse.SubtitleEdit.Core.SubtitleFormats.Ebu.ReadHeader(bytes); } catch { return null; }
        var fields = new Dictionary<string, string>();
        void Add(string name, string? requested, string? landed)
        {
            var v = (landed ?? "").Trim();
            if (requested == null && v.Length > 0) fields[name] = v;
        }
        Add("originalProgrammeTitle", o?.OriginalProgrammeTitle, h.OriginalProgrammeTitle);
        Add("originalEpisodeTitle", o?.OriginalEpisodeTitle, h.OriginalEpisodeTitle);
        Add("translatedProgrammeTitle", o?.TranslatedProgrammeTitle, h.TranslatedProgrammeTitle);
        Add("translatorsName", o?.TranslatorsName, h.TranslatorsName);
        Add("publisher", o?.Publisher, h.Publisher);
        Add("countryOfOrigin", o?.CountryOfOrigin, h.CountryOfOrigin);
        string? languageNote = null;
        var explicitLanguage = o?.Language != null && !o.Language.Equals("auto", StringComparison.OrdinalIgnoreCase);
        if (!explicitLanguage && !string.IsNullOrWhiteSpace(h.LanguageCode))
        {
            var detected = Language.DetectOrNull(written);
            var fromDetection = detected != null && BinaryOptions.EbuLanguages.TryGetValue(detected, out var c) && c == h.LanguageCode;
            // "auto" asked for detection: only a fallback is a default then.
            if (o?.Language == null || !fromDetection)
            {
                fields["language"] = h.LanguageCode.Trim();
                languageNote = fromDetection ? $"language {h.LanguageCode} (auto-detected: {detected})" : $"language {h.LanguageCode} (fallback: no supported language detected)";
            }
        }
        if (fields.Count == 0) return null;
        var parts = fields.Select(kv => kv.Key == "language" ? languageNote! : $"{kv.Key} \"{kv.Value}\"");
        return new Warning(Code,
            $"EBU STL header fields the request didn't set were filled in: {string.Join(", ", parts)}. A MAM or playout system may read them as real programme metadata.",
            fields.Count,
            $"Set formatOptions.ebuStl.{string.Join(", ", fields.Keys)} to the delivery's values (headerFromSource reuses an EBU source's header instead).",
            new WarningDetails(Fields: fields));
    }

    /// <summary>Cavena 890: title (libse uses the output file name) and the language libse picked when none was set.</summary>
    public static Warning? Cavena(byte[] bytes, Cavena890Options? o, string fileName, JobTemp temp)
    {
        var fields = new Dictionary<string, string>();
        var notes = new List<string>();
        if (string.IsNullOrWhiteSpace(o?.Title))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName) ?? "";
            if (stem.Length > 28) stem = stem[..28];
            var landed = Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(stem)).Trim();
            if (landed.Length > 0)
            {
                fields["title"] = landed;
                notes.Add($"title \"{landed}\" (from the output file name)");
            }
        }
        if (o?.Language == null && LandedCavenaLanguage(bytes, temp) is { } id)
        {
            var name = BinaryOptions.CavenaLanguageNames.FirstOrDefault(n => BinaryOptions.CavenaLanguages[n].Id == id);
            fields["language"] = name ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            notes.Add($"language {name ?? "id " + id} (libse's auto-detection; anything it doesn't recognise becomes English)");
        }
        if (fields.Count == 0) return null;
        return new Warning(Code,
            $"Cavena 890 header fields the request didn't set were filled in: {string.Join(", ", notes)}.",
            fields.Count,
            $"Set formatOptions.cavena890.{string.Join(", ", fields.Keys)} to the delivery's values.",
            new WarningDetails(Fields: fields));
    }

    private static int? LandedCavenaLanguage(byte[] bytes, JobTemp temp)
    {
        var ss = Configuration.Settings.SubtitleSettings;
        var (l1, l2) = (ss.CurrentCavena890LanguageIdLine1, ss.CurrentCavena890LanguageIdLine2);
        try
        {
            using var file = new TempFile(temp, bytes, ".890");
            BinaryIO.Load(new Cavena890(), bytes, file.Path);
            return ss.CurrentCavena890LanguageIdLine1 is >= 0 and var id ? id : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            (ss.CurrentCavena890LanguageIdLine1, ss.CurrentCavena890LanguageIdLine2) = (l1, l2);
        }
    }
}
