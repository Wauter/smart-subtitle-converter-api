using Ebu = Nikse.SubtitleEdit.Core.SubtitleFormats.Ebu;

namespace SmartSubtitleConverterApi.Binary;

/// <summary>What was written to the GSI header of an EBU STL output, read back from the bytes. Empty fields are left out.</summary>
public sealed record EbuHeaderEcho(string? Language = null, string? CountryOfOrigin = null, string? OriginalProgrammeTitle = null,
    string? OriginalEpisodeTitle = null, string? TranslatedProgrammeTitle = null, string? TranslatorsName = null, string? Publisher = null)
{
    public static EbuHeaderEcho? From(byte[] bytes)
    {
        Ebu.EbuGeneralSubtitleInformation h;
        try { h = Ebu.ReadHeader(bytes); } catch { return null; }
        static string? Text(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var lang = Text(h.LanguageCode)?.ToUpperInvariant();
        var co = Text(h.CountryOfOrigin)?.ToUpperInvariant();
        return new EbuHeaderEcho(
            lang == null ? null : BinaryOptions.EbuLanguageNames.TryGetValue(lang, out var ln) ? $"{lang} ({ln})" : lang,
            co == null ? null : CountryCodes.ByCode(co) is { } c ? $"{co} ({c.DisplayName})" : RetiredCountryCodes.Find(co) is { } old ? $"{co} ({old.Name}, retired)" : co,
            Text(h.OriginalProgrammeTitle), Text(h.OriginalEpisodeTitle), Text(h.TranslatedProgrammeTitle), Text(h.TranslatorsName), Text(h.Publisher));
    }
}
