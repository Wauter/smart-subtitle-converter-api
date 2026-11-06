using System.Globalization;
using System.Text;
using Nikse.SubtitleEdit.Core.Common;
using SmartSubtitleConverterApi.Formats;

namespace SmartSubtitleConverterApi.Conversion;

/// <summary>One code point and how often it occurred, e.g. {"codePoint":"U+039A","count":12}.</summary>
public sealed record CodePointCount(string CodePoint, int Count);

/// <summary>Cue numbers (1-based), at most <see cref="Verifier.MaxListed"/> listed; <c>total</c> is exact.</summary>
public sealed record CueList(int Total, int[] Cues);

/// <summary>Start/end shifts between the cues we meant to write and the cues read back from the file.</summary>
public sealed record TimingCheck(double MaxShiftMs, double MeanShiftMs, double FrameMs, int CuesOverOneFrame);

/// <summary>Characters that didn't survive the target's character table (positions and code points only, never text).</summary>
public sealed record ReplacedChars(int Total, int CueCount, int[] Cues, int DistinctCodePoints, List<CodePointCount> CodePoints);

/// <summary>
/// The read-back check of a binary output (design §13 row 25, v1.2.0): the written bytes are re-read with the
/// same libse reader and compared with the cues we meant to write. Cue numbers are 1-based positions in the
/// written output. <c>zeroLengthCues</c>: zero-length cues the target couldn't store (EBU STL reads them back as
/// 99:59:59,999); they are kept out of <c>timing</c>. Proves the file is self-consistent through libse, not that a
/// playout system accepts it.
/// </summary>
public sealed record Verification(
    string Verdict, int CuesWritten, int? CuesReadBack, TimingCheck? Timing, ReplacedChars? ReplacedChars,
    CueList? TruncatedCues, CueList? MissingCues, CueList? ZeroLengthCues = null, string? Reason = null);

/// <summary>Structured warning details (JSON body only, never the X-Warnings header).</summary>
public sealed record WarningDetails(int? CueCount = null, int[]? Cues = null, int? DistinctCodePoints = null, List<CodePointCount>? CodePoints = null, RemedyInfo? Remedy = null,
    List<CueDuration>? Durations = null, Dictionary<string, string>? Fields = null, string? Timecode = null, double? FrameRate = null);

/// <summary>long-cue details: 1-based cue number and its duration in milliseconds.</summary>
public sealed record CueDuration(int Cue, double DurationMs);

/// <summary>
/// characters-replaced (v1.2.1): the facts needed to choose a fix, so the caller decides instead of the API guessing.
/// <c>Suggested</c> is set only when the lost script maps to exactly one value of <c>Option</c>; otherwise it is null and
/// <c>Candidates</c> lists the values that may cover it. Static per format: no cue text.
/// </summary>
public sealed record RemedyInfo(
    string[] Scripts, string? DetectedLanguage, string CurrentEncoding, string? Option, string? CurrentValue, string[]? AllowedValues,
    string? Suggested, string[]? Candidates, bool CandidatesTested, string UnicodeAlternative, string? Note = null);

/// <summary>
/// What the writer used, and (PAC) a probe that writes the given code points (every non-ASCII code point of the
/// output, not just the lost ones: another value may lose others) with each other value of the option and returns
/// the values that keep all of them (null when it can't tell).
/// </summary>
public sealed record RemedyContext(string? CurrentValue, Func<IReadOnlyCollection<int>, string[]?>? Covering = null);

public static class Verdicts
{
    public const string Lossless = "lossless";
    public const string Quantised = "quantised";
    public const string Lossy = "lossy";
    public const string Unverified = "unverified";
}

/// <summary>Compares the intended cues with the cues read back from a binary output.</summary>
public static class Verifier
{
    public const int MaxListed = 20;
    private const double ToleranceMs = 1.5;

    public static Verification Unverified(int cuesWritten, string reason) =>
        new(Verdicts.Unverified, cuesWritten, null, null, null, null, null, null, reason);

    public static (Verification Verification, List<Warning> Warnings) Compare(Subtitle expected, Subtitle back, FormatInfo info, double frameMs, string encName, string? language, RemedyContext? remedyContext = null)
    {
        var warnings = new List<Warning>();
        var e = expected.Paragraphs;
        var b = back.Paragraphs;
        var pairs = e.Count == b.Count ? Enumerable.Range(0, e.Count).Select(i => (i, i)).ToList() : Align(expected, back, frameMs);
        var matched = new HashSet<int>(pairs.Select(p => p.Item1));
        var missing = Enumerable.Range(0, e.Count).Where(i => !matched.Contains(i)).ToList();

        double max = 0, sum = 0, maxInFrame = 0;
        var timed = 0;
        var over = new List<int>();
        var zeroLengthBroken = new List<int>();
        var replacedCues = new List<int>();
        var truncated = new List<int>();
        var truncatedChars = 0;
        var codePoints = new Dictionary<int, int>();
        foreach (var (i, j) in pairs)
        {
            var a = e[i];
            var r = b[j];
            var shift = Math.Max(Math.Abs(a.StartTime.TotalMilliseconds - r.StartTime.TotalMilliseconds), Math.Abs(a.EndTime.TotalMilliseconds - r.EndTime.TotalMilliseconds));
            if (shift > frameMs + ToleranceMs && a.EndTime.TotalMilliseconds - a.StartTime.TotalMilliseconds <= 0)
                zeroLengthBroken.Add(i); // the target can't store it (EBU: 99:59:59,999); reported on its own, kept out of the timing figures
            else
            {
                timed++;
                sum += shift;
                max = Math.Max(max, shift);
                if (shift > frameMs + ToleranceMs) over.Add(i);
                else maxInFrame = Math.Max(maxInFrame, shift);
            }

            var want = Normalize(a.Text).Replace("\uFFFD", ""); // U+FFFD already in the source is reported as source-replacement-chars
            var got = Normalize(r.Text);
            if (want == got) continue;
            var compareTo = want;
            if (got.Length < want.Length && IsTruncation(want, got))
            {
                truncated.Add(i);
                truncatedChars += want.Length - got.Length;
                compareTo = want[..got.Length];
            }
            var lost = MissingRunes(compareTo, got);
            if (lost.Count == 0) continue;
            replacedCues.Add(i);
            foreach (var cp in lost) codePoints[cp] = codePoints.GetValueOrDefault(cp) + 1;
        }

        var replacedTotal = codePoints.Values.Sum();
        var timing = new TimingCheck(Round(max), timed == 0 ? 0 : Round(sum / timed), Round(frameMs), over.Count);
        var replaced = replacedTotal == 0 ? null : new ReplacedChars(replacedTotal, replacedCues.Count, Numbers(replacedCues), codePoints.Count, TopCodePoints(codePoints));
        var verdict = missing.Count > 0 || b.Count != e.Count || replacedTotal > 0 || truncated.Count > 0 || over.Count > 0 || zeroLengthBroken.Count > 0 ? Verdicts.Lossy
            : max > ToleranceMs ? Verdicts.Quantised : Verdicts.Lossless;
        var v = new Verification(verdict, e.Count, b.Count, timing, replaced,
            truncated.Count == 0 ? null : new CueList(truncated.Count, Numbers(truncated)),
            missing.Count == 0 ? null : new CueList(missing.Count, Numbers(missing)),
            zeroLengthBroken.Count == 0 ? null : new CueList(zeroLengthBroken.Count, Numbers(zeroLengthBroken)));

        // ---- warnings (same codes as 1.1, now with cue numbers, code points and a remedy) ----
        if (b.Count != e.Count)
        {
            var zeroMissing = missing.Where(i => e[i].EndTime.TotalMilliseconds - e[i].StartTime.TotalMilliseconds <= 0).ToList();
            warnings.Add(new("lossy-format",
                $"{b.Count} of {e.Count} cues read back from the {info.Name} output" + (missing.Count > 0 ? $"; missing: cue(s) {List(missing)}." : "."),
                Math.Abs(e.Count - b.Count),
                zeroMissing.Count > 0
                    ? $"{info.Name} drops zero-length cues ({List(zeroMissing)}): remove them or give them a duration first (operations ApplyDurationLimits with rules.minDurationMs)."
                    : $"Check the missing cues in the source; {info.Name} may not store them. Try another target to compare.",
                Details(missing)));
        }
        if (zeroLengthBroken.Count > 0)
        {
            var sample = b[pairs.First(p => p.Item1 == zeroLengthBroken[0]).Item2].StartTime;
            warnings.Add(new("lossy-format",
                $"{info.Name} can't store zero-length cue(s) {List(zeroLengthBroken)}: they read back at {Tc(sample.TotalMilliseconds)} instead of their own time, so a player would show them at the wrong time or never.",
                zeroLengthBroken.Count,
                "Remove them (operations DeleteFirst / DeleteContains) or give them a duration (operations ApplyDurationLimits with rules.minDurationMs) before writing " + info.Name + ".",
                Details(zeroLengthBroken)));
        }
        if (replaced != null)
        {
            var scripts = Scripts(codePoints.Keys);
            var top = TopCodePoints(codePoints);
            var (hint, remedy) = Remedy(info, scripts, language, encName, top.Take(5).Select(c => c.CodePoint).ToList(), codePoints.Keys,
                e.SelectMany(p => (p.Text ?? "").EnumerateRunes()).Select(r => r.Value).Where(c => c >= 0x80 && c != 0xFFFD).ToHashSet(), remedyContext);
            warnings.Add(new("characters-replaced",
                $"{replacedTotal} character(s) in {replacedCues.Count} cue(s) couldn't be encoded in {encName} and were replaced or dropped" +
                (scripts.Count > 0 ? $" ({string.Join(", ", scripts)})" : "") + $"; cue(s) {List(replacedCues)}.",
                replacedTotal, hint,
                new WarningDetails(replacedCues.Count, Numbers(replacedCues), codePoints.Count, top, remedy)));
        }
        if (truncated.Count > 0)
        {
            warnings.Add(new("text-truncated",
                $"The {info.Name} writer cut {truncated.Count} cue(s) short ({truncatedChars} character(s) lost at the end of a line, or a dropped line): cue(s) {List(truncated)}.",
                truncatedChars,
                $"{info.Name} limits line length and line count. Split long lines first (operations SplitLongLines / BalanceLines with rules.maxLineLength and rules.maxLines) or pick another target.",
                Details(truncated)));
        }
        var otherOver = over;
        if (otherOver.Count == 0 && maxInFrame > ToleranceMs)
            warnings.Add(new("frame-quantised", $"Times were rounded to {info.Name} frames (up to {Math.Round(maxInFrame)} ms at {Math.Round(1000 / frameMs, 3)} fps)."));
        else if (otherOver.Count > 0 && !info.Lossy)
        {
            var worst = otherOver.Max(i => ShiftOf(e[i], b[pairs.First(p => p.Item1 == i).Item2]));
            warnings.Add(new("lossy-format",
                $"Timing changed by up to {Math.Round(worst)} ms (more than one frame) in {otherOver.Count} cue(s) when read back from the {info.Name} output: cue(s) {List(otherOver)}.",
                otherOver.Count, "Check those cues' times in the source (overlaps, times past the format's maximum timecode).", Details(otherOver)));
        }
        return (v, warnings);
    }

    private static double ShiftOf(Paragraph a, Paragraph r) =>
        Math.Max(Math.Abs(a.StartTime.TotalMilliseconds - r.StartTime.TotalMilliseconds), Math.Abs(a.EndTime.TotalMilliseconds - r.EndTime.TotalMilliseconds));

    /// <summary>
    /// Pair cues by start time when the counts differ: walk both lists in order; a read-back cue within
    /// the tolerance of the next intended cue is its match, an earlier one is extra, a later one means the
    /// intended cue is missing.
    /// </summary>
    internal static List<(int, int)> Align(Subtitle expected, Subtitle back, double frameMs)
    {
        var tol = Math.Max(2 * frameMs + ToleranceMs, 250);
        var pairs = new List<(int, int)>();
        int i = 0, j = 0;
        var e = expected.Paragraphs;
        var b = back.Paragraphs;
        while (i < e.Count && j < b.Count)
        {
            var d = b[j].StartTime.TotalMilliseconds - e[i].StartTime.TotalMilliseconds;
            if (Math.Abs(d) <= tol) pairs.Add((i++, j++));
            else if (d < 0) j++;
            else i++;
        }
        return pairs;
    }

    /// <summary>The read-back is a cut-off prefix of the intended text: its end lines up with the intended text.</summary>
    internal static bool IsTruncation(string want, string got)
    {
        if (got.Length == 0) return true;
        var n = Math.Min(6, got.Length);
        return string.CompareOrdinal(want, got.Length - n, got, got.Length - n, n) == 0;
    }

    internal static string Normalize(string? text) =>
        new(HtmlUtil.RemoveHtmlTags(text ?? "", true).Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>Code points of <paramref name="want"/> missing from <paramref name="got"/> (multiset difference).</summary>
    internal static List<int> MissingRunes(string want, string got)
    {
        var counts = new Dictionary<int, int>();
        foreach (var r in got.EnumerateRunes()) counts[r.Value] = counts.GetValueOrDefault(r.Value) + 1;
        var missing = new List<int>();
        foreach (var r in want.EnumerateRunes())
        {
            if (counts.TryGetValue(r.Value, out var n) && n > 0) counts[r.Value] = n - 1;
            else missing.Add(r.Value);
        }
        return missing;
    }

    public static string U(int cp) => "U+" + cp.ToString(cp > 0xFFFF ? "X5" : "X4", CultureInfo.InvariantCulture);

    private static List<CodePointCount> TopCodePoints(Dictionary<int, int> cps) =>
        cps.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(MaxListed).Select(kv => new CodePointCount(U(kv.Key), kv.Value)).ToList();

    /// <summary>0-based indexes to the first <see cref="MaxListed"/> 1-based cue numbers.</summary>
    public static int[] Numbers(IEnumerable<int> indexes) => indexes.Take(MaxListed).Select(i => i + 1).ToArray();

    public static WarningDetails Details(List<int> indexes) => new(indexes.Count, Numbers(indexes));

    public static string List(List<int> indexes) =>
        string.Join(", ", Numbers(indexes)) + (indexes.Count > MaxListed ? $", … ({indexes.Count} in total)" : "");

    private static double Round(double v) => Math.Round(v, 1);

    /// <summary>HH:MM:SS,mmm (hours not wrapped).</summary>
    public static string Tc(double ms)
    {
        var t = (long)Math.Round(Math.Max(0, ms));
        return string.Create(CultureInfo.InvariantCulture, $"{t / 3600000:00}:{t / 60000 % 60:00}:{t / 1000 % 60:00},{t % 1000:000}");
    }

    // ---------------- remedies ----------------

    /// <summary>Unicode scripts of the lost characters, most specific first (names used in messages and remedies).</summary>
    internal static List<string> Scripts(IEnumerable<int> cps)
    {
        var set = new List<string>();
        foreach (var cp in cps)
        {
            var s = cp switch
            {
                >= 0x0370 and <= 0x03FF or >= 0x1F00 and <= 0x1FFF => "Greek",
                >= 0x0400 and <= 0x052F => "Cyrillic",
                >= 0x0590 and <= 0x05FF => "Hebrew",
                >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFE => "Arabic",
                >= 0x0E00 and <= 0x0E7F => "Thai",
                >= 0x3040 and <= 0x30FF => "Japanese kana",
                >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF => "Hangul",
                >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF => "CJK",
                >= 0x0100 and <= 0x024F => "Latin extended",
                _ => "other",
            };
            if (!set.Contains(s)) set.Add(s);
        }
        return set.OrderBy(s => s == "other" ? 1 : 0).ToList();
    }

    /// <summary>
    /// What to change so the characters survive, as facts (v1.2.1): the option, its allowed values, the value in
    /// effect, and candidates. For PAC the candidates are tested (the lost code points are written with every other
    /// code page and read back); elsewhere they come from the script. <c>Suggested</c> is set only when exactly one
    /// value fits; with several (CJK, Latin extended, …) they are listed and the caller chooses. Never switches anything.
    /// </summary>
    internal static (string Hint, RemedyInfo Remedy) Remedy(FormatInfo info, List<string> scripts, string? language, string encName,
        List<string> topCodePoints, IReadOnlyCollection<int> lost, IReadOnlyCollection<int> used, RemedyContext? ctx)
    {
        var real = scripts.Where(x => x != "other").ToList();
        var lang = language != null ? $" (detected source language: {language})" : "";
        var cps = string.Join(", ", topCodePoints) + (lost.Count > topCodePoints.Count ? ", …" : "");
        var lostText = $"Lost characters are {(real.Count > 0 ? string.Join(" + ", real) : "outside the known scripts")} ({cps}){lang}";
        var current = ctx?.CurrentValue;
        bool IsCurrent(string v) => current != null && v.Equals(current, StringComparison.OrdinalIgnoreCase);
        const string Never = " Never switched automatically.";
        const string UnicodeText = "use target fpc (Unicode PAC)";

        string? option; string[]? allowed; string? note = null;
        Func<string, string[]> byScript;
        switch (info.Id)
        {
            case "pacscreenelectronics":
                option = "formatOptions.pac.codePage"; allowed = Binary.BinaryOptions.PacCodePages;
                byScript = sc => sc switch
                {
                    "Greek" => ["greek"], "Cyrillic" => ["cyrillic"], "Arabic" => ["arabic"], "Hebrew" => ["hebrew"], "Thai" => ["thai"],
                    "Hangul" => ["korean"], "Japanese kana" => ["japanese"], "CJK" => ["chinese-traditional", "chinese-simplified", "japanese"],
                    "Latin extended" => ["latin", "czech", "turkish", "portuguese"], _ => [],
                };
                break;
            case "ebustl":
                option = "formatOptions.ebuStl.characterTable"; allowed = [.. Binary.BinaryOptions.EbuCharacterTables.Keys];
                byScript = sc => sc switch { "Greek" => ["greek"], "Cyrillic" => ["cyrillic"], "Arabic" => ["arabic"], "Hebrew" => ["hebrew"], "Latin extended" => ["latin"], _ => [] };
                break;
            case "cavena890":
                option = "formatOptions.cavena890.language"; allowed = Binary.BinaryOptions.CavenaLanguageNames;
                // libse 5.1.0: Greek, Cyrillic and Arabic don't survive a Cavena 890 round trip whatever the language id
                // (garbled, or the reader throws); Hebrew and Chinese do. The Latin languages (en, da, sv, no, nb, ro)
                // share one character table, so switching between them never helps.
                var broken = real.Where(x => x is "Greek" or "Cyrillic" or "Arabic").ToList();
                if (broken.Count > 0) note = $"libse's Cavena 890 writer doesn't keep {string.Join(" / ", broken)} text intact (it reads back garbled), whatever the language.";
                else if (real.Contains("Latin extended")) note = "Cavena 890's Latin languages (en, da, sv, no, nb, ro) share one character table; none keeps these characters.";
                byScript = sc => sc switch { "Hebrew" => ["he"], "CJK" => ["zh-hant", "zh-hans"], _ => [] };
                break;
            default:
                var r0 = new RemedyInfo([.. scripts], language, encName, null, null, null, null, null, false, "fpc");
                return ($"{lostText}. {info.Name} has a fixed character table; use a Unicode target such as fpc (Unicode PAC) or a text format, or remove the characters (operations MultipleReplace).", r0);
        }

        // Tested coverage (PAC) wins; otherwise values for every lost script (intersection), minus the one in effect.
        var tested = note == null ? ctx?.Covering?.Invoke(used) : null;
        if (tested != null && current != null && tested.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            // The code page in effect keeps every one of these characters on its own, so the loss comes from how
            // libse's PAC writer combines neighbouring characters; another code page wouldn't fix it.
            note = $"Each of these characters survives on its own in PAC code page {current}; they are lost in combination with neighbouring characters (libse's PAC writer), so switching the code page won't help.";
            tested = [];
        }
        string[] candidates;
        if (tested != null) candidates = tested;
        else if (note != null || real.Count == 0) candidates = [];
        else
        {
            var per = real.Select(byScript).ToList();
            candidates = per.Skip(1).Aggregate((IEnumerable<string>)per[0], (acc, c) => acc.Intersect(c)).Where(v => !IsCurrent(v)).ToArray();
        }
        var suggested = candidates.Length == 1 ? candidates[0] : null;
        var remedy = new RemedyInfo([.. scripts], language, encName, option, current, allowed, suggested, candidates.Length > 0 ? candidates : null, tested != null, "fpc", note);

        var basis = tested != null ? ", tested: each keeps every character of this text" : "";
        var cur = current != null ? $"; current: {current}" : "";
        string hint;
        if (note != null)
            hint = $"{lostText}. {note} Use a Unicode target such as fpc (Unicode PAC), or remove them (operations MultipleReplace).";
        else if (suggested != null)
            hint = $"{lostText}; set {option} to \"{suggested}\" (the only value that {(tested != null ? "keeps every character of this text, tested" : "covers " + string.Join(" + ", real))}{cur}) or {UnicodeText}.{Never}";
        else if (candidates.Length > 0)
            hint = $"{lostText}; choose the {option} that covers them (options: {string.Join(", ", candidates)}{basis}{cur}) or {UnicodeText}.{Never}";
        else
            hint = $"{lostText}; no {option} value keeps them{(real.Count > 1 && tested == null ? " all at once" : "")} ({(tested != null ? "tested" : "by script")}{cur}); {UnicodeText}, or remove them (operations MultipleReplace).";
        return (hint, remedy);
    }

    private static string BinaryCavenaScript(string language) =>
        Binary.BinaryOptions.CavenaLanguages.TryGetValue(language, out var v) ? v.Script : "latin";
}

/// <summary>
/// Cheap source diagnostics: zero-length cues, overlapping cues, very long cues, U+FFFD already in the text,
/// a guessed text encoding, plus line endings. Warnings only; timings are never changed here.
/// </summary>
public static class SourceChecks
{
    public const string ZeroLengthCue = "zero-length-cue";
    public const string SourceReplacementChars = "source-replacement-chars";
    public const string OverlappingCues = "overlapping-cues";
    public const string LongCue = "long-cue";
    public const string EncodingGuessed = "encoding-guessed";
    public const string EncodingForced = "encoding-forced";
    public const string LanguageApplied = "language-applied";

    /// <summary>A cue longer than this (ms) is reported as long-cue: far beyond any reading time, usually a broken or merged cue.</summary>
    public const double LongCueMs = 30_000;

    public static List<Warning> Check(Subtitle s, DecodedText decoded, bool binary)
    {
        var warnings = new List<Warning>();
        var zero = new List<int>();
        var overlap = new List<int>();
        var longCues = new List<int>();
        var fffdCues = new List<int>();
        var fffd = 0;
        var latestEnd = double.MinValue;
        for (var i = 0; i < s.Paragraphs.Count; i++)
        {
            var p = s.Paragraphs[i];
            var start = p.StartTime.TotalMilliseconds;
            var duration = p.EndTime.TotalMilliseconds - start;
            if (duration <= 0) zero.Add(i);
            else
            {
                // Overlap with any earlier cue (zero-length cues are reported on their own and don't count here).
                if (start < latestEnd) overlap.Add(i);
                latestEnd = Math.Max(latestEnd, p.EndTime.TotalMilliseconds);
                if (duration > LongCueMs) longCues.Add(i);
            }
            var n = (p.Text ?? "").Count(c => c == '\uFFFD');
            if (n == 0) continue;
            fffd += n;
            fffdCues.Add(i);
        }
        if (overlap.Count > 0)
        {
            warnings.Add(new(OverlappingCues,
                $"{overlap.Count} cue(s) in the source start before an earlier cue has ended: cue(s) {Verifier.List(overlap)}.",
                overlap.Count,
                "Check them: overlaps are often timing errors, but can be intended (two speakers). Formats and players that show one cue at a time may hide or cut one of them. Fix the times at the source, or run operations FixCommonErrors with rule FixOverlappingDisplayTimes if they are errors.",
                Verifier.Details(overlap)));
        }
        if (longCues.Count > 0)
        {
            var durations = longCues.Take(20).Select(i => new CueDuration(i + 1, Math.Round(s.Paragraphs[i].EndTime.TotalMilliseconds - s.Paragraphs[i].StartTime.TotalMilliseconds))).ToList();
            warnings.Add(new(LongCue,
                $"{longCues.Count} cue(s) in the source last longer than {LongCueMs / 1000:0} seconds: cue(s) {Verifier.List(longCues)} (longest {Verifier.Tc(durations.Max(d => d.DurationMs))}).",
                longCues.Count,
                "This often indicates a broken or merged cue (a wrong end time, or several cues joined). Check them at the source; the times were left as they are.",
                Verifier.Details(longCues) with { Durations = durations }));
        }
        if (!binary && decoded.Confidence == "heuristic")
        {
            warnings.Add(new(EncodingGuessed,
                $"The input is not UTF-8 and no probed encoding turned it into recognisable words, so it was read as {decoded.EncodingName} by libse's heuristic.",
                null,
                "Check the text; if it looks wrong, set profile.encoding.input (or encoding in /v1/detect) to the file's encoding, or save the file as UTF-8."));
        }
        if (!binary && decoded.Confidence == "forced")
        {
            warnings.Add(new(EncodingForced,
                $"The input was decoded as {decoded.EncodingName} because encoding.input (or encoding in /v1/detect) forced that page; auto-detection was skipped.",
                null,
                "Confirm the text looks right. A wrong forced page yields mojibake with no further error."));
        }
        if (!binary && decoded.Confidence == "language")
        {
            warnings.Add(new(LanguageApplied,
                $"The input was decoded as {decoded.EncodingName} because encoding.language narrowed the probe to encodings that serve that language.",
                null,
                "Confirm the text looks right. If it is wrong, set encoding.input to the exact page instead."));
        }
        if (zero.Count > 0)
        {
            warnings.Add(new(ZeroLengthCue,
                $"{zero.Count} cue(s) in the source have zero or negative duration: cue(s) {Verifier.List(zero)}. These are often metadata or placeholder cues.",
                zero.Count,
                "Check them: remove them (operations DeleteFirst / DeleteContains) or give them a duration (operations ApplyDurationLimits with rules.minDurationMs). EBU STL and CapMaker Plus can't store them.",
                Verifier.Details(zero)));
        }
        if (fffd > 0)
        {
            var how = binary ? "the source file" : $"the input (read as {decoded.EncodingName}, {decoded.Confidence})";
            warnings.Add(new(SourceReplacementChars,
                $"{fffd} U+FFFD replacement character(s) are already in {how}, in {fffdCues.Count} cue(s): cue(s) {Verifier.List(fffdCues)}. The text was damaged before this conversion, usually by decoding with the wrong encoding at an earlier step.",
                fffd,
                binary ? "Re-export the source from the tool that made it." : "Re-export the source with the right encoding; if this file is not UTF-8, set profile.encoding.input (or encoding in /v1/detect).",
                Verifier.Details(fffdCues)));
        }
        return warnings;
    }

    /// <summary>crlf | lf | cr | mixed | none, for decoded text.</summary>
    public static string LineEndings(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n') lf++;
        }
        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        return kinds == 0 ? "none" : kinds > 1 ? "mixed" : crlf > 0 ? "crlf" : lf > 0 ? "lf" : "cr";
    }
}
