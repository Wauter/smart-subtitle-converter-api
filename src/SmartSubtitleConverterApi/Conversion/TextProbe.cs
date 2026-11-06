using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.Common;

namespace SmartSubtitleConverterApi.Conversion;

/// <summary>One way of decoding the input (a PAC code page or a text encoding) and the languages it serves.</summary>
internal sealed record ProbeCandidate(string Name, string Text, IReadOnlyCollection<string> Languages, int Order);

/// <summary>A candidate's score: dictionary hits for the best of its languages.</summary>
internal sealed record ProbeScore(ProbeCandidate Candidate, string? Language, int Hits)
{
    public string Name => Candidate.Name;
}

internal enum ProbeVerdict { Unique, Conflict, NoMatch }

internal sealed record ProbeOutcome(ProbeVerdict Verdict, ProbeScore? Winner, List<ProbeScore> Matches, List<ProbeScore> All);

/// <summary>
/// Decides between ways of decoding the same bytes (PAC code pages, ANSI/DBCS text encodings) from the
/// words they produce. A decode "matches" when it yields dictionary words of a language its code page or
/// encoding serves. Only dictionary words count, never letter statistics: text decoded with the wrong table
/// still looks like letters (Greek PAC read as Czech looks Latin, Arabic cp1256 read as windows-1252 looks
/// Spanish), but it rarely forms words. libse's per-language word lists are used, topped up with a few very
/// common function words for scripts where libse's list is thin (Greek, Hebrew, Arabic, Cyrillic) and with
/// frequent characters for scripts without spaces (Chinese, Japanese, Korean, Thai).
///
/// Rules: a candidate needs at least <c>minHits</c> hits, and at least a third of the best candidate's hits
/// (weaker hits are noise from mojibake). Candidates that decode to the same text are one choice. One
/// choice left: it wins. Several: conflict, never a guess. None: no match.
/// Distinctive-script candidates (Arabic, Hebrew, Thai, Greek, Cyrillic, CJK) drop Latin code-page rivals
/// when they have dictionary hits — you cannot get a page of those letters by decoding as windows-1250.
/// </summary>
internal static class TextProbe
{
    /// <summary>A match needs more than 1/NoiseRatio of the best candidate's hits.</summary>
    public const int NoiseRatio = 3;
    private const int MaxSampleChars = 100_000;

    // Word lists (whole words, case-insensitive) added to libse's lists. Chosen so that the Latin letters a
    // wrong code page would map to them are not common Latin words (e.g. Greek "το" comes from Latin "to" in
    // PAC's Greek table, so it is not listed).
    private static readonly Dictionary<string, string[]> ExtraWords = new()
    {
        ["el"] = ["και", "δεν", "είναι", "μου", "για", "από", "που", "ένα", "μια", "την", "της", "του", "των", "αυτό", "εγώ", "εσύ", "όχι", "γιατί", "τώρα", "εδώ", "πώς", "τι", "κάνεις", "καλημέρα", "κόσμε", "ευχαριστώ", "θέλω", "μπορώ", "έχει", "ήταν"],
        ["he"] = ["של", "את", "זה", "לא", "אני", "מה", "על", "עם", "היא", "כן", "יש", "אין", "כל", "גם", "אבל", "אם", "רק", "אותו", "שלי", "היה"],
        ["ar"] = ["في", "من", "على", "أن", "لا", "ما", "هذا", "هذه", "هل", "أنا", "أنت", "كان", "إلى", "عن", "لقد", "مع", "كل", "لم"],
        ["ru"] = ["и", "в", "что", "я", "ты", "это", "на", "мы", "так", "все", "был", "она", "они", "здесь", "когда", "только"],
        ["uk"] = ["і", "що", "це", "не", "ти", "ми", "так", "та", "але", "він", "вона", "його", "мене", "тебе", "щоб"],
        ["bg"] = ["и", "да", "не", "се", "на", "това", "че", "ще", "ти", "аз", "съм", "какво"],
    };

    // Characters (counted wherever they occur) for scripts written without spaces between words.
    private static readonly Dictionary<string, string[]> ExtraChars = new()
    {
        ["zh"] = ["的", "是", "我", "你", "不", "了", "在", "他", "有", "人", "一", "好", "们", "們", "这", "這", "个", "個", "说", "說", "么", "麼", "吗", "嗎", "来", "來", "会", "會", "没", "沒", "就", "也", "都", "要", "到", "她", "什", "那", "吧", "啊", "呢"],
        ["ja"] = ["の", "は", "が", "を", "に", "で", "と", "も", "て", "た", "し", "か", "な", "い", "る", "ま", "す", "だ", "よ", "ね"],
        ["ko"] = ["이", "가", "는", "은", "을", "를", "에", "의", "다", "요", "고", "지", "서", "하", "야", "어"],
        ["th"] = ["ที่", "ไม่", "ครับ", "ค่ะ", "คุณ", "เป็น", "จะ", "ได้", "นี้", "ฉัน", "ผม", "และ", "ว่า", "มี", "อะไร", "เรา"],
    };

    private static readonly Dictionary<string, Regex> WordRegex = ExtraWords.ToDictionary(kv => kv.Key,
        kv => new Regex(@"(?<!\p{L})(" + string.Join("|", kv.Value.Select(Regex.Escape)) + @")(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    public static string Sample(string text) => text.Length > MaxSampleChars ? text[..MaxSampleChars] : text;

    public static ProbeScore Score(ProbeCandidate c)
    {
        var sample = Sample(c.Text);
        var libse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var l in LanguageAutoDetect.GetLanguagesWithCount(sample))
            {
                if (string.IsNullOrEmpty(l.LanguageCode) || l.LanguageCode == "-") continue; // combined lists (cs+sk, hr+sr) have no single code
                libse[l.LanguageCode] = Math.Max(libse.GetValueOrDefault(l.LanguageCode), l.WordCount);
            }
        }
        catch
        {
            // Detection is best effort; extra lists still count.
        }
        string? best = null;
        var bestHits = 0;
        foreach (var lang in c.Languages)
        {
            var hits = libse.GetValueOrDefault(lang);
            if (WordRegex.TryGetValue(lang, out var re)) hits += re.Count(sample);
            if (ExtraChars.TryGetValue(lang, out var chars)) hits += chars.Sum(ch => Count(sample, ch));
            if (hits > bestHits) (best, bestHits) = (lang, hits);
        }
        return new ProbeScore(c, best, bestHits);
    }

    private static int Count(string text, string what)
    {
        var n = 0;
        for (var i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>The hit floor for a text with this many non-empty cues (or lines): at least 1, about one per 30.</summary>
    public static int MinHits(int units) => Math.Max(1, units / 30);

    /// <param name="scores">Scored candidates, in preference order (Order breaks ties inside a group).</param>
    /// <param name="minHits">Hit floor.</param>
    /// <param name="drop">Optional: remove members of the strong set before grouping (e.g. an ISO-8859 page whose Windows sibling also matched).</param>
    public static ProbeOutcome Decide(List<ProbeScore> scores, int minHits, Func<List<ProbeScore>, List<ProbeScore>>? drop = null)
    {
        var matches = scores.Where(s => s.Hits >= minHits).ToList();
        if (matches.Count == 0) return new(ProbeVerdict.NoMatch, null, [], scores);
        var best = matches.Max(s => s.Hits);
        var strong = matches.Where(s => s.Hits * NoiseRatio > best).ToList();
        if (drop != null) strong = drop(strong);
        var groups = strong.GroupBy(s => Normalise(s.Candidate.Text)).ToList();
        var ordered = strong.OrderByDescending(s => s.Hits).ThenBy(s => s.Candidate.Order).ToList();
        if (groups.Count == 1)
            return new(ProbeVerdict.Unique, ordered[0], ordered, scores);
        return new(ProbeVerdict.Conflict, null, ordered, scores);
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n").Trim();

    /// <summary>Unicode scripts that cannot arise from a Latin Windows/ISO decode of the same bytes as real letters.</summary>
    private enum ScriptKind { None, Arabic, Hebrew, Thai, Greek, Cyrillic, Cjk }

    private static readonly HashSet<string> DistinctiveLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "fa", "ur", "he", "th", "el", "ru", "uk", "bg", "mk", "sr-cy", "zh", "ja", "ko",
    };

    private static ScriptKind ScriptFor(string? language) => language?.ToLowerInvariant() switch
    {
        "ar" or "fa" or "ur" => ScriptKind.Arabic,
        "he" => ScriptKind.Hebrew,
        "th" => ScriptKind.Thai,
        "el" => ScriptKind.Greek,
        "ru" or "uk" or "bg" or "mk" or "sr-cy" => ScriptKind.Cyrillic,
        "zh" or "ja" or "ko" => ScriptKind.Cjk,
        _ => ScriptKind.None,
    };

    private static bool IsScriptLetter(char c, ScriptKind script) => script switch
    {
        ScriptKind.Arabic => c is (>= '\u0600' and <= '\u06FF') or (>= '\u0750' and <= '\u077F') or (>= '\u08A0' and <= '\u08FF') or (>= '\uFB50' and <= '\uFDFF') or (>= '\uFE70' and <= '\uFEFF'),
        ScriptKind.Hebrew => c is >= '\u0590' and <= '\u05FF',
        ScriptKind.Thai => c is >= '\u0E00' and <= '\u0E7F',
        ScriptKind.Greek => c is (>= '\u0370' and <= '\u03FF') or (>= '\u1F00' and <= '\u1FFF'),
        ScriptKind.Cyrillic => c is (>= '\u0400' and <= '\u04FF') or (>= '\u0500' and <= '\u052F'),
        ScriptKind.Cjk => c is (>= '\u3040' and <= '\u30FF')
            or (>= '\u3400' and <= '\u4DBF') or (>= '\u4E00' and <= '\u9FFF')
            or (>= '\uAC00' and <= '\uD7AF')
            or (>= '\u1100' and <= '\u11FF') or (>= '\u3130' and <= '\u318F'),
        _ => false,
    };

    /// <summary>
    /// True when a substantial share of letter characters in <paramref name="text"/> belong to the distinctive
    /// script of <paramref name="language"/> (Arabic, Hebrew, Thai, Greek, Cyrillic or CJK). A handful of
    /// accidental characters does not count.
    /// </summary>
    public static bool HasDistinctiveScript(string text, string? language)
    {
        var script = ScriptFor(language);
        if (script == ScriptKind.None || language is null || !DistinctiveLanguages.Contains(language)) return false;
        var sample = Sample(text);
        var letters = 0;
        var inScript = 0;
        foreach (var c in sample)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (IsScriptLetter(c, script)) inScript++;
        }
        // Need enough distinctive letters, and they must be a real share of all letters (not one glyph in Latin mojibake).
        return inScript >= 8 && inScript * 5 >= letters * 2; // >= 40%
    }

    /// <summary>A few cue bodies (skipping indices and arrows), character-capped, for ambiguous-encoding samples.</summary>
    public static string CueSample(string text, int maxCues = 3, int maxChars = 400)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cues = new List<string>();
        var buf = new List<string>();
        void Flush()
        {
            if (buf.Count == 0 || cues.Count >= maxCues) { buf.Clear(); return; }
            cues.Add(string.Join("\n", buf));
            buf.Clear();
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }
            if (Regex.IsMatch(line, @"^\d+$")) continue;
            if (line.Contains("-->", StringComparison.Ordinal)) continue;
            buf.Add(line);
        }
        Flush();
        if (cues.Count == 0)
        {
            var trimmed = text.Trim();
            return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars] + "\u2026";
        }
        var sample = string.Join("\n\n", cues);
        return sample.Length <= maxChars ? sample : sample[..maxChars] + "\u2026";
    }
}
