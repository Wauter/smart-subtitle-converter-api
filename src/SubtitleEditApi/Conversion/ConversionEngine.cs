using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.Enums;
using Nikse.SubtitleEdit.Core.Forms;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using SubtitleEditApi.Formats;
using SubtitleEditApi.Infrastructure;
using SubtitleEditApi.Profiles;

namespace SubtitleEditApi.Conversion;

public sealed record DetectedInfo(string Format, string Id, string Encoding, string EncodingConfidence, string? Language, int Paragraphs, List<string>? Candidates = null);
public sealed record ConvertedOutput(string FileName, FormatInfo Format, byte[] Bytes, string EncodingName, bool Unicode, int Cues);
public sealed record ConversionResult(List<ConvertedOutput> Outputs, DetectedInfo Detected, int ParagraphsIn, List<Warning> Warnings, long ElapsedMs);

/// <summary>
/// decode -> detect/load -> settings -> timing -> operations -> write -> encode, all in memory
/// (design §2.3). MUST run on the conversion worker: it mutates libse's global settings.
/// </summary>
public sealed partial class ConversionEngine(FormatRegistry formats, ApiOptions opts)
{
    public ConversionResult Convert(byte[] bytes, string? fileName, Profile profile)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<Warning>();
        SettingsScope.Apply(profile);
        try
        {
            var (subtitle, sourceFormat, sourceInfo, decoded) = Load(bytes, fileName, profile.Source?.Format, profile.Encoding);
            var paragraphsIn = subtitle.Paragraphs.Count;
            var language = Language.DetectOrNull(subtitle);

            ApplyTiming(subtitle, profile);
            foreach (var op in profile.Operations ?? [])
            {
                var before = subtitle.Paragraphs.Count;
                try
                {
                    OperationCatalog.Apply(subtitle, op, warnings);
                }
                catch (ApiException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new ApiException(422, ErrorCodes.ConversionFailed, $"Operation '{op.Id}' failed: {ex.Message}",
                        $"Remove or reconfigure '{op.Id}' and retry; report it with the requestId if the input is valid.", ex)
                    { Debug = { ["operation"] = op.Id, ["cuesBefore"] = before } };
                }
            }
            ApplyGaps(subtitle, profile.Timing);

            var targets = new List<string> { profile.Target!.Format! };
            targets.AddRange(profile.Target.Additional ?? []);
            var outputs = new List<ConvertedOutput>();
            var stem = SafeStem(fileName);
            foreach (var (t, i) in targets.Select((t, i) => (t, i)))
            {
                var (info, format) = formats.Resolve(t, i == 0 ? "/target/format" : $"/target/additional/{i - 1}");
                var copy = new Subtitle(subtitle, false);
                try
                {
                    if (sourceFormat.GetType() != format.GetType()) sourceFormat.RemoveNativeFormatting(copy, format);
                    ApplyFormatOptions(copy, info, profile, warnings);
                    var text = format.ToText(copy, stem);
                    var (enc, encName) = Encodings.ForOutput(profile.Encoding?.Output, decoded);
                    var name = OutputName(profile.Target.FileName, stem, info);
                    outputs.Add(new ConvertedOutput(name, info, Encodings.Encode(text, enc), encName, Encodings.IsUnicode(enc), copy.Paragraphs.Count));
                }
                catch (ApiException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new ApiException(422, ErrorCodes.ConversionFailed, $"Writing '{info.Id}' failed: {ex.Message}", "Try another target format; report it with the requestId.", ex)
                    { Debug = { ["operation"] = "write:" + info.Id } };
                }
            }
            if (outputs.Select(o => o.FileName).Distinct().Count() != outputs.Count)
            {
                for (var i = 1; i < outputs.Count; i++)
                    if (outputs.Take(i).Any(o => o.FileName == outputs[i].FileName))
                        outputs[i] = outputs[i] with { FileName = $"{stem}.{outputs[i].Format.Id}.{outputs[i].Format.Extension.TrimStart('.')}" };
            }
            var detected = new DetectedInfo(sourceInfo.Name, sourceInfo.Id, decoded.EncodingName, decoded.Confidence, language, paragraphsIn);
            return new ConversionResult(outputs, detected, paragraphsIn, warnings, sw.ElapsedMilliseconds);
        }
        finally
        {
            SettingsScope.Reset();
        }
    }

    public DetectedInfo Detect(byte[] bytes, string? fileName, string? forcedEncoding)
    {
        SettingsScope.Apply(new Profile());
        try
        {
            var (subtitle, _, info, decoded) = Load(bytes, fileName, null, new EncodingOptions(forcedEncoding, null, null), collectCandidates: true, out var candidates);
            return new DetectedInfo(info.Name, info.Id, decoded.EncodingName, decoded.Confidence, Language.DetectOrNull(subtitle), subtitle.Paragraphs.Count, candidates);
        }
        finally
        {
            SettingsScope.Reset();
        }
    }

    private (Subtitle, SubtitleFormat, FormatInfo, DecodedText) Load(byte[] bytes, string? fileName, string? sourceFormat, EncodingOptions? enc) =>
        Load(bytes, fileName, sourceFormat, enc, false, out _);

    private (Subtitle, SubtitleFormat, FormatInfo, DecodedText) Load(byte[] bytes, string? fileName, string? sourceFormat, EncodingOptions? enc, bool collectCandidates, out List<string>? candidates)
    {
        candidates = null;
        if (bytes.Length == 0)
            throw new ApiException(400, ErrorCodes.EmptyInput, "The uploaded file is empty.", "The uploaded file is 0 bytes.");

        var decoded = Encodings.Decode(bytes, enc?.Input, enc?.InputFallback);
        var text = decoded.Text;
        var lines = text.SplitToLines();
        var longest = lines.Count == 0 ? 0 : lines.Max(l => l.Length);
        if (longest > opts.MaxLineChars)
        {
            throw new ApiException(422, ErrorCodes.InputLimitsExceeded, $"A line has {longest} characters; max is {opts.MaxLineChars}.",
                "Very long lines usually mean a binary or minified file. Raise MAX_LINE_CHARS if this input is legitimate.");
        }
        XmlGuard.Screen(text);

        var name = string.IsNullOrEmpty(fileName) ? "subtitle" : Path.GetFileName(fileName);
        Subtitle? loaded = null;
        SubtitleFormat? format = null;
        FormatInfo? info = null;

        if (!string.IsNullOrWhiteSpace(sourceFormat) && !sourceFormat.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            (info, format) = formats.Resolve(sourceFormat, "/source/format");
            loaded = new Subtitle();
            try { format.LoadSubtitle(loaded, lines, name); } catch (Exception ex) { throw NotDetected(decoded, lines, $"source.format '{info.Id}' could not parse the input: {ex.Message}", ex); }
            if (loaded.Paragraphs.Count == 0) throw NotDetected(decoded, lines, $"source.format '{info.Id}' found no cues in the input.", null);
        }
        else
        {
            var found = new List<string>();
            foreach (var (ci, cf) in formats.DetectionCandidates())
            {
                try
                {
                    if (!cf.IsMine(lines, name)) continue;
                    var s = new Subtitle();
                    cf.LoadSubtitle(s, lines, name);
                    if (s.Paragraphs.Count == 0) continue;
                    if (loaded == null) { loaded = s; format = cf; info = ci; }
                    found.Add(ci.Name);
                    if (!collectCandidates || found.Count >= 5) break;
                }
                catch
                {
                    // A format that throws on foreign input simply isn't this format.
                }
            }
            candidates = found;
            if (loaded == null) throw NotDetected(decoded, lines, "No text format recognised the input.", null);
        }

        if (loaded.Paragraphs.Count > opts.MaxCues)
            throw new ApiException(422, ErrorCodes.InputLimitsExceeded, $"Input has {loaded.Paragraphs.Count} cues; max is {opts.MaxCues}.", "Split the file or raise MAX_CUES.");
        return (loaded, format!, info!, decoded);
    }

    private ApiException NotDetected(DecodedText decoded, List<string> lines, string detail, Exception? inner)
    {
        var first = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        var hint = "Set profile.source.format, or check GET /v1/formats.";
        var root = XmlRoot().Match(decoded.Text.TrimStart());
        if (root.Success)
        {
            hint = $"Looks like XML with root <{root.Groups[1].Value}>; " + (root.Groups[1].Value.EndsWith("tt") && !decoded.Text.Contains("http://www.w3.org/ns/ttml")
                ? "but no TTML namespace (http://www.w3.org/ns/ttml). " : "") + hint;
        }
        else if (decoded.Text.Contains('\0'))
        {
            hint = "The input looks binary. V1 supports text formats only. " + hint;
        }
        var ex = new ApiException(422, ErrorCodes.FormatNotDetected, detail, hint, inner);
        ex.Debug["detected"] = new Dictionary<string, object?>
        {
            ["encoding"] = decoded.EncodingName, ["encodingConfidence"] = decoded.Confidence,
            ["lineCount"] = lines.Count, ["firstLineShape"] = opts.ErrorsVerbose ? Shape(first) : null,
        };
        return ex;
    }

    [GeneratedRegex(@"^(?:<\?xml[^>]*>\s*)?(?:<!--.*?-->\s*)*<([A-Za-z_][\w:.-]*)", RegexOptions.Singleline)]
    private static partial Regex XmlRoot();

    /// <summary>Structure without content: letters become 'x' (design §7.1 snippetShape).</summary>
    public static string Shape(string line)
    {
        var s = line.Length > 80 ? line[..80] : line;
        return new string(s.Select(c => char.IsLetter(c) ? 'x' : c is >= ' ' and <= '~' ? c : '?').ToArray());
    }

    private static void ApplyTiming(Subtitle s, Profile p)
    {
        var t = p.Timing;
        if (t?.OffsetMs is { } off && off != 0) s.AddTimeToAllParagraphs(TimeSpan.FromMilliseconds(off));
        if (p.Target?.Fps is > 0 && p.Target.Fps is { } tfps)
            s.ChangeFrameRate(p.Source?.Fps ?? Configuration.Settings.General.CurrentFrameRate, tfps);
        if (t?.ChangeSpeedPercent is { } pct && Math.Abs(pct - 100) > 0.0001)
        {
            var factor = 100.0 / pct;
            foreach (var para in s.Paragraphs)
            {
                para.StartTime.TotalMilliseconds *= factor;
                para.EndTime.TotalMilliseconds *= factor;
            }
        }
        if (t?.RenumberFrom is { } rn) s.Renumber(rn);
        if (t?.AdjustDurationMs is { } adj && adj != 0) s.AdjustDisplayTimeUsingSeconds(adj / 1000.0, null);
    }

    private static void ApplyGaps(Subtitle s, TimingOptions? t)
    {
        if (t?.BridgeGapsMs is > 0 && t.BridgeGapsMs is { } maxMs)
        {
            var minGap = Math.Max(0, Configuration.Settings.General.MinimumMillisecondsBetweenLines);
            if (minGap > maxMs) minGap = Math.Max(0, maxMs - 1);
            DurationsBridgeGaps.BridgeGaps(s, minGap, false, maxMs, null, null, false);
        }
        if (t?.MinGapMs is > 0 && t.MinGapMs is { } minMs)
        {
            for (var i = 0; i < s.Paragraphs.Count - 1; i++)
            {
                var cur = s.Paragraphs[i];
                var next = s.Paragraphs[i + 1];
                if (next.StartTime.TotalMilliseconds - cur.EndTime.TotalMilliseconds >= minMs) continue;
                var newEnd = next.StartTime.TotalMilliseconds - minMs;
                if (newEnd - cur.StartTime.TotalMilliseconds > 0) cur.EndTime.TotalMilliseconds = newEnd;
            }
        }
    }

    private static void ApplyFormatOptions(Subtitle s, FormatInfo info, Profile p, List<Warning> warnings)
    {
        var assa = p.FormatOptions?.Assa;
        if (assa == null) return;
        if (info.Id is not ("advancedsubstationalpha" or "substationalpha"))
        {
            warnings.Add(new("format-option-ignored", $"formatOptions.assa ignored for target '{info.Id}'."));
            return;
        }
        if (string.IsNullOrEmpty(s.Header) || !s.Header.Contains("[V4+ Styles]")) s.Header = AdvancedSubStationAlpha.DefaultHeader;
        if (assa.PlayResX is { } w && assa.PlayResY is { } h) s.Header = AdvancedSubStationAlpha.SetResolution(s.Header, w, h);
        else if (assa.PlayResX != null || assa.PlayResY != null) warnings.Add(new("format-option-ignored", "Set both playResX and playResY."));
        if (!string.IsNullOrWhiteSpace(assa.StyleBlock))
        {
            var styleSub = new Subtitle();
            var block = assa.StyleBlock.Contains("[Script Info]") ? assa.StyleBlock : "[Script Info]\nScriptType: v4.00+\n\n" + assa.StyleBlock + "\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";
            new AdvancedSubStationAlpha().LoadSubtitle(styleSub, block.SplitToLines(), "styles.ass");
            foreach (var style in AdvancedSubStationAlpha.GetSsaStylesFromHeader(styleSub.Header ?? ""))
                s.Header = AdvancedSubStationAlpha.UpdateOrAddStyle(s.Header, style);
        }
    }

    public static string SafeStem(string? fileName)
    {
        var n = Path.GetFileNameWithoutExtension(Path.GetFileName((fileName ?? "").Replace('\\', '/')));
        n = new string(n.Where(c => !char.IsControl(c) && c is not ('"' or '/' or '\\')).ToArray()).Trim();
        return n.Length == 0 ? "subtitle" : n;
    }

    private static string OutputName(string? template, string stem, FormatInfo info)
    {
        var ext = info.Extension.TrimStart('.');
        var name = (string.IsNullOrWhiteSpace(template) ? "{stem}.{ext}" : template)
            .Replace("{stem}", stem).Replace("{ext}", ext).Replace("{format}", info.Id);
        name = Path.GetFileName(name.Replace('\\', '/'));
        return name.Length == 0 ? $"{stem}.{ext}" : name;
    }
}

/// <summary>
/// libse reads process-global Configuration.Settings (design §2.4). Each job starts from a fresh
/// default Settings object with the profile applied, and resets again afterwards (also on error).
/// </summary>
public static class SettingsScope
{
    public static void Reset()
    {
        Configuration.Settings.Reset();
        Configuration.Settings.Tools.OcrUseWordSplitList = false; // seconv Bootstrap default
    }

    public static void Apply(Profile p)
    {
        Reset();
        var g = Configuration.Settings.General;
        if (p.Source?.Fps is { } fps) g.CurrentFrameRate = fps;
        var r = p.Rules;
        if (r == null) return;
        if (r.MaxLineLength is { } a) g.SubtitleLineMaximumLength = a;
        if (r.MaxLines is { } b) g.MaxNumberOfLines = b;
        if (r.MinDurationMs is { } c) g.SubtitleMinimumDisplayMilliseconds = c;
        if (r.MaxDurationMs is { } d) g.SubtitleMaximumDisplayMilliseconds = d;
        if (r.MinGapMs is { } e) g.MinimumMillisecondsBetweenLines = e;
        if (r.MaxCps is { } f) g.SubtitleMaximumCharactersPerSeconds = f;
        if (r.OptimalCps is { } h) g.SubtitleOptimalCharactersPerSeconds = h;
        if (r.MaxWpm is { } i) g.SubtitleMaximumWordsPerMinute = i;
        if (r.MergeLinesShorterThan is { } j) g.MergeLinesShorterThan = j;
        if (r.DialogStyle is { } k) g.DialogStyle = Enum.Parse<DialogType>(k);
        if (r.ContinuationStyle is { } l) g.ContinuationStyle = Enum.Parse<ContinuationStyle>(l);
        var tools = Configuration.Settings.Tools;
        if (r.MergeShortLines?.MaxGapMs is { } m) tools.MergeShortLinesMaxGap = m;
        if (r.MergeShortLines?.OnlyContinuous is { } n) tools.MergeShortLinesOnlyContinuous = n;
        if (r.RemoveTextForHI is { } hi)
        {
            var target = Configuration.Settings.RemoveTextForHearingImpaired;
            foreach (var (key, value) in hi)
            {
                var prop = target.GetType().GetProperty(OperationCatalog.HiSettings[key])!;
                prop.SetValue(target, prop.PropertyType == typeof(bool) ? value.GetBoolean() : value.GetString());
            }
        }
    }
}
