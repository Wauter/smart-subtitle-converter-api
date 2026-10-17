using System.Globalization;
using System.Text;
using Nikse.SubtitleEdit.Core.Common;
using SubtitleEditConverterApi.Formats;

namespace SubtitleEditConverterApi.Conversion;

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
public sealed record WarningDetails(int? CueCount = null, int[]? Cues = null, int? DistinctCodePoints = null, List<CodePointCount>? CodePoints = null);

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

    public static (Verification Verification, List<Warning> Warnings) Compare(Subtitle expected, Subtitle back, FormatInfo info, double frameMs, string encName, string? language)
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
            warnings.Add(new("characters-replaced",
                $"{replacedTotal} character(s) in {replacedCues.Count} cue(s) couldn't be encoded in {encName} and were replaced or dropped" +
                (scripts.Count > 0 ? $" ({string.Join(", ", scripts)})" : "") + $"; cue(s) {List(replacedCues)}.",
                replacedTotal, Remedy(info, scripts, language, encName),
                new WarningDetails(replacedCues.Count, Numbers(replacedCues), codePoints.Count, TopCodePoints(codePoints))));
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
    /// What to change so the characters survive. Suggests, never switches: the code page or language stays
    /// what the caller (or the default) chose.
    /// </summary>
    internal static string Remedy(FormatInfo info, List<string> scripts, string? language, string encName)
    {
        var main = scripts.FirstOrDefault(s => s is not ("other" or "Latin extended")) ?? scripts.FirstOrDefault();
        var lang = language != null ? $" (detected source language: {language})" : "";
        const string Unicode = "use a Unicode target such as fpc (PAC Unicode)";
        // The suggested setting is already in effect (e.g. pac-greek and a few characters still don't fit).
        bool Active(string value) => encName.EndsWith("-" + value, StringComparison.OrdinalIgnoreCase) || encName.Equals(value, StringComparison.OrdinalIgnoreCase);
        switch (info.Id)
        {
            case "pacscreenelectronics":
                var cp = main switch
                {
                    "Greek" => "greek", "Cyrillic" => "cyrillic", "Arabic" => "arabic", "Hebrew" => "hebrew", "Thai" => "thai",
                    "Hangul" => "korean", "Japanese kana" => "japanese",
                    "CJK" => language is "zh-hant" or "zh-tw" ? "chinese-traditional" : language == "ja" ? "japanese" : "chinese-simplified",
                    "Latin extended" => language == "tr" ? "turkish" : language is "pt" ? "portuguese" : "czech",
                    _ => null,
                };
                if (cp != null && !Active(cp))
                    return $"Set formatOptions.pac.codePage to \"{cp}\"{lang}, or write Unicode PAC with target fpc. The code page is never switched automatically.";
                return $"These characters don't fit PAC code page {encName}{lang}; write Unicode PAC with target fpc, or remove them (operations MultipleReplace).";
            case "ebustl":
                var table = main switch { "Greek" => "greek", "Cyrillic" => "cyrillic", "Arabic" => "arabic", "Hebrew" => "hebrew", _ => null };
                if (table != null)
                    return $"Set formatOptions.ebuStl.characterTable to \"{table}\"{lang} (if it isn't already), or {Unicode}. The character table is never switched automatically.";
                return $"EBU STL's character tables can't hold these characters{lang}; {Unicode}, or remove them (operations MultipleReplace).";
            case "cavena890":
                // libse 5.1.0: Greek, Cyrillic and Arabic don't survive a Cavena 890 round trip whatever the language id
                // (garbled, or the reader throws); Hebrew and Chinese do.
                if (main is "Greek" or "Cyrillic" or "Arabic")
                    return $"libse's Cavena 890 writer doesn't keep {main} text intact{lang} (it reads back garbled); {Unicode}.";
                var cl = main switch { "Hebrew" => "he", "CJK" => language is "zh-hant" or "zh-tw" ? "zh-hant" : "zh-hans", _ => null };
                if (cl != null && !Active(BinaryCavenaScript(cl)))
                    return $"Set formatOptions.cavena890.language to \"{cl}\"{lang}, or {Unicode}. The language is never switched automatically.";
                return $"Cavena 890 ({encName}) can't hold these characters{lang}; {Unicode}, or remove them (operations MultipleReplace).";
            default:
                return $"{info.Name} has a fixed character table{lang}; {Unicode} or a text format, or remove the characters (operations MultipleReplace).";
        }
    }

    private static string BinaryCavenaScript(string language) =>
        Binary.BinaryOptions.CavenaLanguages.TryGetValue(language, out var v) ? v.Script : "latin";
}

/// <summary>Cheap source diagnostics (v1.2.0): zero-length cues and U+FFFD already in the text, plus line endings.</summary>
public static class SourceChecks
{
    public const string ZeroLengthCue = "zero-length-cue";
    public const string SourceReplacementChars = "source-replacement-chars";

    public static List<Warning> Check(Subtitle s, DecodedText decoded, bool binary)
    {
        var warnings = new List<Warning>();
        var zero = new List<int>();
        var fffdCues = new List<int>();
        var fffd = 0;
        for (var i = 0; i < s.Paragraphs.Count; i++)
        {
            var p = s.Paragraphs[i];
            if (p.EndTime.TotalMilliseconds - p.StartTime.TotalMilliseconds <= 0) zero.Add(i);
            var n = (p.Text ?? "").Count(c => c == '\uFFFD');
            if (n == 0) continue;
            fffd += n;
            fffdCues.Add(i);
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
