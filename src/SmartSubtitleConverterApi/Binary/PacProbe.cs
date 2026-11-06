using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using SmartSubtitleConverterApi.Conversion;
using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Binary;

/// <summary>
/// PAC input code page when formatOptions.pac.inputCodePage is auto or omitted. libse's own auto pick
/// settles on one code page (Czech in practice) without saying so; Greek, Cyrillic or Turkish text then
/// comes out as plausible-looking Latin. Instead every one of the 13 code pages decodes the file, and
/// <see cref="TextProbe"/> keeps a code page only when its text forms words of a language it serves.
/// One choice: used, reported as codePageConfidence "probed". None or several: 422 ambiguous-encoding.
/// </summary>
internal static class PacProbe
{
    /// <summary>Languages each PAC code page serves (index = code page). Latin follows libse's own table, plus pt/fi/is/ca.</summary>
    public static readonly string[][] Languages =
    [
        ["en", "da", "no", "nb", "sv", "es", "it", "fr", "de", "nl", "pl", "sq", "hr", "sr", "ro", "id", "pt", "fi", "is", "ca"], // latin
        ["el"], // greek
        ["cs", "sk"], // czech
        ["ar", "fa", "ur"], // arabic
        ["he"], // hebrew
        ["th"], // thai
        ["ru", "uk", "bg", "mk", "sr-cy"], // cyrillic
        ["zh"], // chinese-traditional
        ["zh"], // chinese-simplified
        ["ko"], // korean
        ["ja"], // japanese
        ["tr"], // turkish
        ["pt"], // portuguese
    ];

    public sealed record Result(Subtitle? Subtitle, int CodePage, string? Language, ApiException? Problem, Subtitle? Fallback);

    public static Result Run(byte[] bytes, string path)
    {
        var decoded = new List<(int Page, Subtitle Sub)>();
        for (var i = 0; i < BinaryOptions.PacCodePages.Length; i++)
        {
            try
            {
                var s = BinaryIO.Load(new Pac { CodePage = i }, bytes, path);
                if (s.Paragraphs.Count > 0) decoded.Add((i, s));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // This code page can't read the bytes at all: not a candidate.
            }
        }
        if (decoded.Count == 0) return new(null, -1, null, Problem([], [], "No PAC code page could read the file."), null);

        var scores = decoded.Select(d => TextProbe.Score(new ProbeCandidate(BinaryOptions.PacCodePages[d.Page], Text(d.Sub), Languages[d.Page], d.Page))).ToList();
        var units = decoded[0].Sub.Paragraphs.Count(p => !string.IsNullOrWhiteSpace(p.Text));
        var outcome = TextProbe.Decide(scores, TextProbe.MinHits(units));
        var plausible = decoded.Where(d => ConversionEngine.Plausible(d.Sub)).ToList();
        var fallback = (plausible.Count > 0 ? plausible[0] : decoded[0]).Sub;
        if (outcome.Verdict == ProbeVerdict.Unique)
        {
            var page = outcome.Winner!.Candidate.Order;
            return new(decoded.First(d => d.Page == page).Sub, page, outcome.Winner.Language, null, null);
        }
        string[] candidates;
        string detail;
        if (outcome.Verdict == ProbeVerdict.Conflict)
        {
            candidates = outcome.Matches.Select(m => m.Name).Distinct().ToArray();
            detail = "The PAC text reads as words in more than one code page (" +
                     string.Join(", ", outcome.Matches.Select(m => $"{m.Name}: {m.Language}, {m.Hits} word(s)")) + "), and they decode to different text.";
        }
        else
        {
            candidates = plausible.Select(d => BinaryOptions.PacCodePages[d.Page]).ToArray();
            detail = "No PAC code page turns this file into recognisable words, so its code page can't be told from the content.";
        }
        return new(null, -1, null, Problem(candidates, scores, detail), fallback);
    }

    private static string Text(Subtitle s) => string.Join("\n", s.Paragraphs.Select(p => p.Text));

    private static ApiException Problem(string[] candidates, List<ProbeScore> scores, string detail)
    {
        var list = candidates.Length > 0 ? $" (candidates: {string.Join(", ", candidates)})" : "";
        var ex = new ApiException(422, ErrorCodes.AmbiguousEncoding, detail + " The code page is not guessed.",
            $"Set profile.formatOptions.pac.inputCodePage to the file's code page{list}. For a Unicode PAC file use source.format 'fpc'.")
        {
            Errors = [new FieldError("/formatOptions/pac/inputCodePage", "Auto-detection could not decide the PAC code page.", "Set it to the code page the file was written with.")],
        };
        ex.Extensions["candidates"] = candidates;
        ex.Extensions["probe"] = "pac-code-page";
        ex.Debug["scores"] = scores.Select(s => new { codePage = s.Name, language = s.Language, hits = s.Hits }).ToList();
        return ex;
    }
}
