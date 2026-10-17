using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.Forms;
using Nikse.SubtitleEdit.Core.Forms.FixCommonErrors;
using Nikse.SubtitleEdit.Core.Interfaces;
using SkiaSharp;
using SubtitleEditConverterApi.Formats;
using SubtitleEditConverterApi.Infrastructure;
using SubtitleEditConverterApi.Profiles;

namespace SubtitleEditConverterApi.Conversion;

/// <summary>
/// The "recipe" adapter (design §2.1, option A): operation ids, defaults and constants mirror
/// seconv's LibSEIntegration.ApplyOperations / FixCommonErrorsRunner / RemoveFormattingRunner,
/// calling the same public libse APIs. Review against upstream on every libse bump (§8).
/// </summary>
public static class OperationCatalog
{
    public static readonly string[] Ids =
    [
        "RemoveTextForHI", "FixCommonErrors", "RemoveFormatting", "MergeSameTexts", "MergeSameTimeCodes",
        "MergeShortLines", "SplitLongLines", "BalanceLines", "RedoCasing", "RemoveLineBreaks",
        "ApplyDurationLimits", "BeautifyTimeCodes", "ConvertColorsToDialog", "FixRtlViaUnicodeChars",
        "RemoveUnicodeControlChars", "ReverseRtlStartEnd", "DeleteFirst", "DeleteLast", "DeleteContains", "MultipleReplace",
    ];

    private static readonly Dictionary<string, string[]> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FixCommonErrors"] = ["rules", "language"],
        ["RemoveFormatting"] = ["rules"],
        ["DeleteFirst"] = ["count"],
        ["DeleteLast"] = ["count"],
        ["DeleteContains"] = ["text"],
        ["MultipleReplace"] = ["rules"],
    };

    public static readonly string[] RemoveFormattingRules =
        ["RemoveItalic", "RemoveBold", "RemoveUnderline", "RemoveFontName", "RemoveAlignment", "RemoveColor"];

    // UPSTREAM: seconv FixCommonErrorsRunner.BuildRules order (FixMisreadQuotes before AddMissingQuotes).
    private static readonly (string Id, Func<IFixCommonError> Factory)[] FceFactories =
    [
        (nameof(AddMissingQuotes), () => new AddMissingQuotes()),
        (nameof(Fix3PlusLines), () => new Fix3PlusLines()),
        (nameof(FixAloneLowercaseIToUppercaseI), () => new FixAloneLowercaseIToUppercaseI()),
        (nameof(FixCommas), () => new FixCommas()),
        (nameof(FixContinuationStyle), () => new FixContinuationStyle { FixAction = "Fix continuation style" }),
        (nameof(FixDanishLetterI), () => new FixDanishLetterI()),
        (nameof(FixDialogsOnOneLine), () => new FixDialogsOnOneLine()),
        (nameof(FixDoubleApostrophes), () => new FixDoubleApostrophes()),
        (nameof(FixDoubleDash), () => new FixDoubleDash()),
        (nameof(FixDoubleGreaterThan), () => new FixDoubleGreaterThan()),
        (nameof(FixEllipsesStart), () => new FixEllipsesStart()),
        (nameof(FixEmptyLines), () => new FixEmptyLines()),
        (nameof(FixHyphensInDialog), () => new FixHyphensInDialog()),
        (nameof(FixHyphensRemoveDashSingleLine), () => new FixHyphensRemoveDashSingleLine()),
        (nameof(FixInvalidItalicTags), () => new FixInvalidItalicTags()),
        (nameof(FixLongDisplayTimes), () => new FixLongDisplayTimes()),
        (nameof(FixLongLines), () => new FixLongLines()),
        (nameof(FixMissingOpenBracket), () => new FixMissingOpenBracket()),
        (nameof(FixMissingPeriodsAtEndOfLine), () => new FixMissingPeriodsAtEndOfLine()),
        (nameof(FixMissingSpaces), () => new FixMissingSpaces()),
        (nameof(FixMusicNotation), () => new FixMusicNotation()),
        (nameof(FixOverlappingDisplayTimes), () => new FixOverlappingDisplayTimes()),
        (nameof(FixShortDisplayTimes), () => new FixShortDisplayTimes()),
        (nameof(FixShortGaps), () => new FixShortGaps()),
        (nameof(FixShortLines), () => new FixShortLines()),
        (nameof(FixShortLinesAll), () => new FixShortLinesAll()),
        (nameof(FixShortLinesPixelWidth), () => new FixShortLinesPixelWidth(MeasurePixelWidth)),
        (nameof(FixSpanishInvertedQuestionAndExclamationMarks), () => new FixSpanishInvertedQuestionAndExclamationMarks()),
        (nameof(FixStartWithUppercaseLetterAfterColon), () => new FixStartWithUppercaseLetterAfterColon()),
        (nameof(FixStartWithUppercaseLetterAfterParagraph), () => new FixStartWithUppercaseLetterAfterParagraph()),
        (nameof(FixStartWithUppercaseLetterAfterPeriodInsideParagraph), () => new FixStartWithUppercaseLetterAfterPeriodInsideParagraph()),
        (nameof(FixTurkishAnsiToUnicode), () => new FixTurkishAnsiToUnicode()),
        (nameof(FixUnnecessaryLeadingDots), () => new FixUnnecessaryLeadingDots()),
        (nameof(FixUnneededPeriods), () => new FixUnneededPeriods()),
        (nameof(FixUnneededSpaces), () => new FixUnneededSpaces()),
        (nameof(FixUppercaseIInsideWords), () => new FixUppercaseIInsideWords()),
        (nameof(NormalizeStrings), () => new NormalizeStrings()),
        (nameof(RemoveDialogFirstLineInNonDialogs), () => new RemoveDialogFirstLineInNonDialogs()),
        (nameof(RemoveSpaceBetweenNumbers), () => new RemoveSpaceBetweenNumbers()),
    ];

    public static readonly string[] FixCommonErrorsRules = FceFactories.Select(f => f.Id).ToArray();
    private static readonly HashSet<string> FceOptIn = new(StringComparer.OrdinalIgnoreCase); // FixMisreadQuotes (opt-in upstream) is not in libse 5.1.0
    private static readonly Dictionary<string, string> FceLanguageGates = new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(FixAloneLowercaseIToUppercaseI)] = "en",
        [nameof(FixDanishLetterI)] = "da",
        [nameof(FixSpanishInvertedQuestionAndExclamationMarks)] = "es",
        [nameof(FixTurkishAnsiToUnicode)] = "tr",
    };

    /// <summary>Profile key -> RemoveTextForHearingImpairedSettings property.</summary>
    public static readonly Dictionary<string, string> HiSettings = new()
    {
        ["betweenBrackets"] = "RemoveTextBetweenBrackets",
        ["betweenParentheses"] = "RemoveTextBetweenParentheses",
        ["betweenCurlyBrackets"] = "RemoveTextBetweenCurlyBrackets",
        ["betweenQuestionMarks"] = "RemoveTextBetweenQuestionMarks",
        ["betweenOnlySeparateLines"] = "RemoveTextBetweenOnlySeparateLines",
        ["beforeColon"] = "RemoveTextBeforeColon",
        ["beforeColonOnlyIfUppercase"] = "RemoveTextBeforeColonOnlyIfUppercase",
        ["beforeColonOnlyOnSeparateLine"] = "RemoveTextBeforeColonOnlyOnSeparateLine",
        ["interjections"] = "RemoveInterjections",
        ["interjectionsOnlyOnSeparateLine"] = "RemoveInterjectionsOnlyOnSeparateLine",
        ["ifAllUppercase"] = "RemoveIfAllUppercase",
        ["ifOnlyMusicSymbols"] = "RemoveIfOnlyMusicSymbols",
        ["ifContains"] = "RemoveIfContains",
        ["ifContainsText"] = "RemoveIfContainsText",
    };
    public static IEnumerable<string> HiSettingNames => HiSettings.Keys;

    // ---------------- validation ----------------

    public static void ValidateOperation(JsonNode node, string pointer, List<FieldError> errors)
    {
        if (node is not JsonObject op) { errors.Add(new(pointer, "Expected an object like {\"id\":\"SplitLongLines\"}.")); return; }
        var idNode = op["id"];
        if (idNode is not JsonValue v || !v.TryGetValue<string>(out var id))
        {
            errors.Add(new(pointer + "/id", "Required: operation id.", $"One of: {string.Join(", ", Ids)}.")); return;
        }
        var canonical = Ids.FirstOrDefault(i => i.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (canonical == null)
        {
            var s = Text.Suggest(id, Ids);
            var notV1 = id.Equals("Translate", StringComparison.OrdinalIgnoreCase) || id.Equals("FixCommonOcrErrors", StringComparison.OrdinalIgnoreCase);
            errors.Add(new(pointer + "/id", $"Unknown operation '{id}'.",
                notV1 ? "Not available in V1 (needs libuilogic/OCR); see docs/roadmap.md." : s != null ? $"Did you mean '{s}'?" : $"One of: {string.Join(", ", Ids)}."));
            return;
        }
        var allowed = AllowedKeys.GetValueOrDefault(canonical, []);
        foreach (var (key, val) in op)
        {
            if (key == "id") continue;
            var p = $"{pointer}/{key}";
            if (!allowed.Contains(key))
            {
                errors.Add(new(p, $"'{canonical}' has no option '{key}'.", allowed.Length > 0 ? $"Allowed: {string.Join(", ", allowed)}." : "This operation takes no options."));
                continue;
            }
            switch (canonical, key)
            {
                case ("FixCommonErrors", "rules"): ValidateRuleSpec(val, p, FixCommonErrorsRules, errors, "FixCommonOcrErrors"); break;
                case ("RemoveFormatting", "rules"): ValidateRuleSpec(val, p, RemoveFormattingRules, errors, null); break;
                case ("FixCommonErrors", "language"):
                    if (val is not JsonValue lv || !lv.TryGetValue<string>(out _)) errors.Add(new(p, "Expected a language code like 'en' or 'auto'."));
                    break;
                case (_, "count"):
                    if (val is not JsonValue cv || !cv.TryGetValue<int>(out var c) || c < 1) errors.Add(new(p, "Expected an integer >= 1."));
                    break;
                case ("DeleteContains", "text"):
                    if (val is not JsonValue tv || !tv.TryGetValue<string>(out var t) || t.Length == 0) errors.Add(new(p, "Expected a non-empty string."));
                    break;
                case ("MultipleReplace", "rules"): ValidateReplaceRules(val, p, errors); break;
            }
        }
        if (canonical is "DeleteFirst" or "DeleteLast" && op["count"] == null) errors.Add(new(pointer + "/count", "Required: count.", "e.g. {\"id\":\"DeleteFirst\",\"count\":1}"));
        if (canonical is "DeleteContains" && op["text"] == null) errors.Add(new(pointer + "/text", "Required: text."));
        if (canonical is "MultipleReplace" && op["rules"] == null) errors.Add(new(pointer + "/rules", "Required: rules (list of {find, replace, mode})."));
    }

    private static void ValidateRuleSpec(JsonNode? val, string pointer, string[] available, List<FieldError> errors, string? notInV1)
    {
        if (val is not JsonArray arr) { errors.Add(new(pointer, "Expected an array of rule ids, e.g. [\"all\", \"-FixCommas\"].")); return; }
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonValue v || !v.TryGetValue<string>(out var raw)) { errors.Add(new($"{pointer}/{i}", "Expected a string.")); continue; }
            var id = raw.TrimStart('-');
            if (id.Equals("all", StringComparison.OrdinalIgnoreCase) || available.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
            if (notInV1 != null && id.Equals(notInV1, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new($"{pointer}/{i}", $"'{id}' is not available in V1.", "It needs Hunspell/libuilogic; see docs/roadmap.md.")); continue;
            }
            var s = Text.Suggest(id, available);
            errors.Add(new($"{pointer}/{i}", $"Unknown rule '{id}'.", s != null ? $"Did you mean '{s}'?" : $"One of: all, {string.Join(", ", available)}."));
        }
    }

    private static void ValidateReplaceRules(JsonNode? val, string pointer, List<FieldError> errors)
    {
        if (val is not JsonArray arr) { errors.Add(new(pointer, "Expected an array of {find, replace, mode}.")); return; }
        if (arr.Count > 500) errors.Add(new(pointer, "At most 500 replace rules."));
        for (var i = 0; i < arr.Count; i++)
        {
            var p = $"{pointer}/{i}";
            if (arr[i] is not JsonObject r) { errors.Add(new(p, "Expected an object.")); continue; }
            foreach (var (k, _) in r)
                if (k is not ("find" or "replace" or "mode")) errors.Add(new($"{p}/{k}", $"Unknown property '{k}'.", "Allowed: find, replace, mode."));
            var find = r["find"] is JsonValue fv && fv.TryGetValue<string>(out var f) ? f : null;
            if (string.IsNullOrEmpty(find)) errors.Add(new($"{p}/find", "Required: non-empty string."));
            if (r["replace"] is { } rv && (rv is not JsonValue rvv || !rvv.TryGetValue<string>(out _))) errors.Add(new($"{p}/replace", "Expected a string."));
            var mode = r["mode"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : "text";
            if (mode is not ("text" or "caseSensitive" or "regex")) errors.Add(new($"{p}/mode", $"'{mode}' is not allowed.", "One of: text, caseSensitive, regex."));
            if (mode == "regex" && find != null)
            {
                try { _ = new Regex(find, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException ex) { errors.Add(new($"{p}/find", "Invalid regex: " + ex.Message)); }
            }
        }
    }

    public static JsonObject OperationSchema()
    {
        var variants = new JsonArray();
        foreach (var id in Ids)
        {
            var props = new JsonObject { ["id"] = new JsonObject { ["const"] = id } };
            foreach (var k in AllowedKeys.GetValueOrDefault(id, []))
            {
                props[k] = k switch
                {
                    "count" => new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                    "text" or "language" => new JsonObject { ["type"] = "string" },
                    "rules" when id == "MultipleReplace" => new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("find"),
                            ["properties"] = new JsonObject
                            {
                                ["find"] = new JsonObject { ["type"] = "string" },
                                ["replace"] = new JsonObject { ["type"] = "string" },
                                ["mode"] = new JsonObject { ["enum"] = new JsonArray("text", "caseSensitive", "regex") },
                            },
                        },
                    },
                    _ => new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Rule ids; 'all', and '-Id' to exclude. Available: " +
                                          string.Join(", ", id == "FixCommonErrors" ? FixCommonErrorsRules : RemoveFormattingRules),
                    },
                };
            }
            var v = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray("id"), ["additionalProperties"] = false };
            variants.Add(v);
        }
        return new JsonObject { ["oneOf"] = variants, ["description"] = "An operation; executed in array order." };
    }

    // ---------------- execution ----------------

    public static IReadOnlyList<string> ResolveRuleSpec(IReadOnlyList<string>? spec, string[] available, ISet<string>? optIn = null)
    {
        if (spec == null || spec.Count == 0) return available.Where(a => optIn?.Contains(a) != true).ToArray();
        var hasPositive = spec.Any(t => !t.StartsWith('-'));
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!hasPositive) selected.UnionWith(available.Where(a => optIn?.Contains(a) != true));
        foreach (var raw in spec)
        {
            var neg = raw.StartsWith('-');
            var id = raw.TrimStart('-');
            if (id.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                if (neg) selected.Clear(); else selected.UnionWith(available.Where(a => optIn?.Contains(a) != true));
                continue;
            }
            if (neg) selected.Remove(id); else { selected.Add(id); named.Add(id); }
        }
        return available.Where(selected.Contains).ToArray();
    }

    public static void Apply(Subtitle subtitle, OperationSpec op, List<Warning> warnings)
    {
        switch (op.Id.ToLowerInvariant())
        {
            case "fixcommonerrors": FixCommonErrors(subtitle, op, warnings); break;
            case "removetextforhi":
                {
                    var lib = new RemoveTextForHI(new RemoveTextForHISettings(subtitle));
                    var lang = Language.Detect(subtitle);
                    for (var i = subtitle.Paragraphs.Count - 1; i >= 0; i--)
                    {
                        var p = subtitle.Paragraphs[i];
                        p.Text = lib.RemoveTextFromHearImpaired(p.Text, lang);
                        if (string.IsNullOrWhiteSpace(p.Text)) subtitle.Paragraphs.RemoveAt(i);
                    }
                    subtitle.Renumber();
                    break;
                }
            case "mergesametexts":
                Replace(subtitle, MergeLinesSameTextUtils.MergeLinesWithSameTextInSubtitle(subtitle, true, 100)); // GUI default 100 ms
                break;
            case "mergesametimecodes":
                Replace(subtitle, MergeLinesWithSameTimeCodes.Merge(subtitle, new List<int>(), out _, true, false, false, 250, "en", new List<int>(), new Dictionary<int, bool>(), new Subtitle()));
                break;
            case "mergeshortlines":
                Replace(subtitle, MergeShortLinesUtils.MergeShortLinesInSubtitle(subtitle, Configuration.Settings.Tools.MergeShortLinesMaxGap,
                    Configuration.Settings.General.SubtitleLineMaximumLength, Configuration.Settings.Tools.MergeShortLinesOnlyContinuous));
                break;
            case "splitlonglines":
                {
                    var max = Configuration.Settings.General.SubtitleLineMaximumLength;
                    var split = SplitLongLinesHelper.SplitLongLinesInSubtitle(subtitle, max * 2, max);
                    subtitle.Paragraphs.Clear();
                    subtitle.Paragraphs.AddRange(split.Paragraphs);
                    break;
                }
            case "balancelines":
                {
                    var lang = Language.Detect(subtitle);
                    foreach (var p in subtitle.Paragraphs) p.Text = Utilities.AutoBreakLine(p.Text, lang, false);
                    break;
                }
            case "redocasing":
                new FixCasing(Language.Detect(subtitle)) { FixNormal = true, FixNormalOnlyAllUppercase = false, FixMakeUppercase = false, FixMakeLowercase = false }.Fix(subtitle);
                break;
            case "removeformatting":
                {
                    // libse 5.1.0 has no RemoveFormattingUtil; map seconv's rule ids onto HtmlUtil.
                    var rules = ResolveRuleSpec(op.Rules, RemoveFormattingRules);
                    var all = rules.Count == RemoveFormattingRules.Length;
                    foreach (var p in subtitle.Paragraphs)
                    {
                        if (all) { p.Text = HtmlUtil.RemoveHtmlTags(p.Text, true); continue; }
                        var t = p.Text;
                        foreach (var r in rules)
                        {
                            t = r switch
                            {
                                "RemoveItalic" => HtmlUtil.RemoveOpenCloseTags(t, HtmlUtil.TagItalic).Replace("{\\i1}", "").Replace("{\\i0}", ""),
                                "RemoveBold" => HtmlUtil.RemoveOpenCloseTags(t, HtmlUtil.TagBold).Replace("{\\b1}", "").Replace("{\\b0}", ""),
                                "RemoveUnderline" => HtmlUtil.RemoveOpenCloseTags(t, HtmlUtil.TagUnderline).Replace("{\\u1}", "").Replace("{\\u0}", ""),
                                "RemoveFontName" => HtmlUtil.RemoveFontName(t),
                                "RemoveAlignment" => HtmlUtil.RemoveAssAlignmentTags(t),
                                "RemoveColor" => HtmlUtil.RemoveAssaColor(HtmlUtil.RemoveColorTags(t)),
                                _ => t,
                            };
                        }
                        p.Text = t;
                    }
                    break;
                }
            case "removelinebreaks":
                foreach (var p in subtitle.Paragraphs) p.Text = Utilities.RemoveLineBreaks(p.Text);
                break;
            case "applydurationlimits":
                Replace(subtitle, new FixDurationLimits(Configuration.Settings.General.SubtitleMinimumDisplayMilliseconds,
                    Configuration.Settings.General.SubtitleMaximumDisplayMilliseconds, new List<double>()).Fix(subtitle), always: true);
                break;
            case "beautifytimecodes":
                new Nikse.SubtitleEdit.Core.Forms.TimeCodesBeautifier(subtitle, Configuration.Settings.General.CurrentFrameRate, new List<double>(), new List<double>()).Beautify();
                break;
            case "convertcolorstodialog":
                ConvertColorsToDialogUtils.ConvertColorsToDialogInSubtitle(subtitle, true, false, false, false, false, Language.Detect(subtitle));
                break;
            case "fixrtlviaunicodechars":
                foreach (var p in subtitle.Paragraphs) p.Text = Utilities.FixRtlViaUnicodeChars(p.Text);
                break;
            case "removeunicodecontrolchars":
                foreach (var p in subtitle.Paragraphs) p.Text = p.Text.RemoveControlCharacters();
                break;
            case "reversertlstartend":
                foreach (var p in subtitle.Paragraphs) p.Text = Utilities.ReverseStartAndEndingForRightToLeft(p.Text);
                break;
            case "deletefirst":
                subtitle.Paragraphs.RemoveRange(0, Math.Min(op.Count ?? 0, subtitle.Paragraphs.Count));
                subtitle.Renumber();
                break;
            case "deletelast":
                {
                    var n = Math.Min(op.Count ?? 0, subtitle.Paragraphs.Count);
                    subtitle.Paragraphs.RemoveRange(subtitle.Paragraphs.Count - n, n);
                    subtitle.Renumber();
                    break;
                }
            case "deletecontains":
                subtitle.Paragraphs.RemoveAll(p => p.Text.Contains(op.Text ?? "\0", StringComparison.Ordinal));
                subtitle.Renumber();
                break;
            case "multiplereplace": MultipleReplace(subtitle, op, warnings); break;
        }
    }

    private static void Replace(Subtitle target, Subtitle result, bool always = false)
    {
        if (ReferenceEquals(result, target)) return;
        if (!always && result.Paragraphs.Count == target.Paragraphs.Count) return; // seconv: only replace when something merged
        var list = result.Paragraphs.ToList();
        target.Paragraphs.Clear();
        target.Paragraphs.AddRange(list);
    }

    private static void FixCommonErrors(Subtitle subtitle, OperationSpec op, List<Warning> warnings)
    {
        if (subtitle.Paragraphs.Count == 0) return;
        var wanted = new HashSet<string>(ResolveRuleSpec(op.Rules, FixCommonErrorsRules, FceOptIn), StringComparer.OrdinalIgnoreCase);
        var language = !string.IsNullOrWhiteSpace(op.Language) && !op.Language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? op.Language.Trim().ToLowerInvariant()
            : Language.DetectOrNull(subtitle) ?? "en";
        var callbacks = new EmptyFixCallback { Language = language };

        var before = Signature(subtitle);
        var previous = before;
        for (var pass = 0; pass < 10; pass++) // seconv MaxPasses
        {
            foreach (var (id, factory) in FceFactories)
            {
                if (!wanted.Contains(id)) continue;
                if (FceLanguageGates.TryGetValue(id, out var lang) && !lang.Equals(language, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    factory().Fix(subtitle, callbacks);
                }
                catch (Exception ex)
                {
                    // seconv swallows rule failures; we surface them as warnings instead.
                    warnings.Add(new("fce-rule-failed", $"FixCommonErrors rule {id} failed: {ex.GetType().Name}"));
                }
            }
            var now = Signature(subtitle);
            if (now == previous) break;
            previous = now;
        }
        if (previous == before) warnings.Add(new("op-noop", "FixCommonErrors changed nothing"));
    }

    private static void MultipleReplace(Subtitle subtitle, OperationSpec op, List<Warning> warnings)
    {
        foreach (var rule in op.Replacements ?? [])
        {
            var replace = rule.Replace ?? "";
            Func<string, string> fn = (rule.Mode ?? "text") switch
            {
                "regex" => BuildRegex(rule.Find, replace),
                "caseSensitive" => s => s.Replace(rule.Find, replace, StringComparison.Ordinal),
                _ => s => s.Replace(rule.Find, replace, StringComparison.OrdinalIgnoreCase),
            };
            foreach (var p in subtitle.Paragraphs)
            {
                try
                {
                    p.Text = fn(p.Text);
                }
                catch (RegexMatchTimeoutException)
                {
                    throw new ApiException(422, ErrorCodes.ConversionFailed, $"MultipleReplace regex '{rule.Find}' timed out (100 ms).",
                        "Simplify the pattern; avoid nested quantifiers.");
                }
            }
        }
    }

    private static Func<string, string> BuildRegex(string pattern, string replace)
    {
        Regex re;
        try
        {
            re = new Regex(pattern, RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
        }
        catch (NotSupportedException)
        {
            re = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)); // backreferences/lookarounds
        }
        return s => re.Replace(s, replace);
    }

    private static long Signature(Subtitle s)
    {
        var h = new HashCode();
        foreach (var p in s.Paragraphs) { h.Add(p.Text); h.Add(p.StartTime.TotalMilliseconds); h.Add(p.EndTime.TotalMilliseconds); }
        return h.ToHashCode() ^ ((long)s.Paragraphs.Count << 32);
    }

    [ThreadStatic] private static SKFont? _font;

    private static int MeasurePixelWidth(string text)
    {
        _font ??= new SKFont(SKTypeface.Default, 14);
        return (int)Math.Round(_font.MeasureText(text), MidpointRounding.AwayFromZero);
    }
}

public sealed record Warning(string Code, string Message);

/// <summary>Language detection with a one-entry cache, like seconv's SubtitleLanguageDetector.</summary>
public static class Language
{
    public static string? DetectOrNull(Subtitle s)
    {
        try { return LanguageAutoDetect.AutoDetectGoogleLanguageOrNull(s); }
        catch { return null; }
    }

    public static string Detect(Subtitle s) => DetectOrNull(s) ?? "en";
}
