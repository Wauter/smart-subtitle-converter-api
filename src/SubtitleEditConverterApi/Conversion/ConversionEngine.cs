using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.Enums;
using Nikse.SubtitleEdit.Core.Forms;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using SubtitleEditConverterApi.Binary;
using SubtitleEditConverterApi.Formats;
using SubtitleEditConverterApi.Infrastructure;
using SubtitleEditConverterApi.Profiles;

namespace SubtitleEditConverterApi.Conversion;

public sealed record DetectedInfo(string Format, string Id, string Encoding, string EncodingConfidence, string? Language, int Paragraphs, List<string>? Candidates = null,
    string Kind = FormatKinds.Text, string? CodePage = null, Dictionary<string, object?>? FormatDetails = null);
public sealed record ConvertedOutput(string FileName, FormatInfo Format, byte[] Bytes, string EncodingName, bool Unicode, int Cues);
public sealed record ConversionResult(List<ConvertedOutput> Outputs, DetectedInfo Detected, int ParagraphsIn, List<Warning> Warnings, long ElapsedMs);

/// <summary>What Load found: the subtitle, its format, and how it was decoded (text) or read (binary).</summary>
internal sealed record Loaded(Subtitle Subtitle, SubtitleFormat Format, FormatInfo Info, DecodedText Decoded, string? CodePage, Dictionary<string, object?>? Details, List<string> Candidates)
{
    public bool IsBinary => Info.Kind == FormatKinds.Binary;
}

/// <summary>
/// sniff -> decode -> detect/load -> settings -> timing -> operations -> write -> encode (design §2.3).
/// Text stays in memory; binary formats go through a private temp file because libse's binary
/// readers need a path (BinaryIO). MUST run on the conversion worker: it mutates libse's global settings.
/// </summary>
public sealed partial class ConversionEngine(FormatRegistry formats, ApiOptions opts)
{
    /// <summary>Stand-in DecodedText for binary input: reported as encoding "binary", confidence "n/a".</summary>
    private static DecodedText BinaryDecoded() => new("", new UTF8Encoding(true), "binary", "n/a", true);

    public ConversionResult Convert(byte[] bytes, string? fileName, Profile profile)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<Warning>();
        SettingsScope.Apply(profile);
        var ownTemp = BinaryIO.Current == null ? new JobTemp() : null;
        var temp = BinaryIO.Current ?? ownTemp!;
        try
        {
            var loaded = Load(bytes, fileName, profile, temp, collectCandidates: false);
            var (subtitle, sourceFormat, sourceInfo, decoded) = (loaded.Subtitle, loaded.Format, loaded.Info, loaded.Decoded);
            var paragraphsIn = subtitle.Paragraphs.Count;
            var language = Language.DetectOrNull(subtitle);
            if (loaded.IsBinary && (profile.Encoding?.Input != null || profile.Encoding?.InputFallback != null))
                warnings.Add(new("encoding-ignored", $"encoding.input/inputFallback don't apply to binary source '{sourceInfo.Id}'; its character set comes from the file."));

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
                    WarnIgnoredOptions(info, sourceInfo, profile, warnings);
                    var name = OutputName(profile.Target.FileName, stem, info);
                    if (info.Kind == FormatKinds.Binary)
                    {
                        if (profile.Encoding?.Output != null)
                            warnings.Add(new("encoding-ignored", $"encoding.output doesn't apply to binary target '{info.Id}'; its character set comes from formatOptions."));
                        var (data, encName) = WriteBinary(copy, info, format, loaded, profile, name, temp, warnings);
                        outputs.Add(new ConvertedOutput(name, info, data, encName, false, copy.Paragraphs.Count));
                        continue;
                    }
                    if (loaded.IsBinary && string.Equals(profile.Encoding?.Output, "source", StringComparison.OrdinalIgnoreCase))
                        warnings.Add(new("encoding-ignored", "encoding.output=source has no text encoding to reuse for a binary source; wrote UTF-8."));
                    ApplyFormatOptions(copy, info, profile, warnings);
                    var text = format.ToText(copy, stem);
                    var (enc, encName2) = Encodings.ForOutput(profile.Encoding?.Output, decoded);
                    outputs.Add(new ConvertedOutput(name, info, Encodings.Encode(text, enc), encName2, Encodings.IsUnicode(enc), copy.Paragraphs.Count));
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
            var detected = new DetectedInfo(sourceInfo.Name, sourceInfo.Id, decoded.EncodingName, decoded.Confidence, language, paragraphsIn,
                Kind: sourceInfo.Kind, CodePage: loaded.CodePage, FormatDetails: loaded.Details);
            return new ConversionResult(outputs, detected, paragraphsIn, warnings, sw.ElapsedMilliseconds);
        }
        finally
        {
            ownTemp?.Dispose();
            SettingsScope.Reset();
        }
    }

    public DetectedInfo Detect(byte[] bytes, string? fileName, string? forcedEncoding)
    {
        SettingsScope.Apply(new Profile());
        var ownTemp = BinaryIO.Current == null ? new JobTemp() : null;
        var temp = BinaryIO.Current ?? ownTemp!;
        try
        {
            var l = Load(bytes, fileName, new Profile { Encoding = new EncodingOptions(forcedEncoding, null, null) }, temp, collectCandidates: true);
            return new DetectedInfo(l.Info.Name, l.Info.Id, l.Decoded.EncodingName, l.Decoded.Confidence, Language.DetectOrNull(l.Subtitle), l.Subtitle.Paragraphs.Count,
                l.Candidates, l.Info.Kind, l.CodePage, l.Details);
        }
        finally
        {
            ownTemp?.Dispose();
            SettingsScope.Reset();
        }
    }

    private Loaded Load(byte[] bytes, string? fileName, Profile profile, JobTemp temp, bool collectCandidates)
    {
        if (bytes.Length == 0)
            throw new ApiException(400, ErrorCodes.EmptyInput, "The uploaded file is empty.", "The uploaded file is 0 bytes.");

        var sourceFormat = profile.Source?.Format;
        var enc = profile.Encoding;
        var name = string.IsNullOrEmpty(fileName) ? "subtitle" : Path.GetFileName(fileName);
        var explicitSource = !string.IsNullOrWhiteSpace(sourceFormat) && !sourceFormat.Equals("auto", StringComparison.OrdinalIgnoreCase);
        FormatInfo? forced = null;
        SubtitleFormat? forcedFormat = null;
        if (explicitSource) (forced, forcedFormat) = formats.Resolve(sourceFormat!, "/source/format");

        // 1. Binary: explicit binary source, or auto detection when the bytes look binary (before any text decoding).
        if (forced is { Kind: FormatKinds.Binary })
            return CheckCues(LoadBinary(bytes, forced, forcedFormat!, profile, temp));
        var callerExt = CallerExtension(fileName);
        var binaryCandidates = new List<string>();
        if (!explicitSource && LooksBinary(bytes, callerExt))
        {
            var found = DetectBinary(bytes, callerExt, profile, temp, collectCandidates);
            binaryCandidates = found.Candidates;
            if (found.Result != null) return CheckCues(found.Result with { Candidates = found.Candidates });
        }

        // 2. Text: decode, then the text-only guards (they used to run first and rejected binary files).
        var decoded = Encodings.Decode(bytes, enc?.Input, enc?.InputFallback);
        var text = decoded.Text;
        var lines = text.SplitToLines();
        var longest = lines.Count == 0 ? 0 : lines.Max(l => l.Length);
        if (longest > opts.MaxLineChars)
        {
            throw new ApiException(422, ErrorCodes.InputLimitsExceeded, $"A line has {longest} characters; max is {opts.MaxLineChars}.",
                "Very long lines usually mean a binary or minified file: for a binary format set profile.source.format (GET /v1/formats?kind=binary). Raise MAX_LINE_CHARS if this input is legitimate text.");
        }
        XmlGuard.Screen(text);

        Subtitle? loaded = null;
        SubtitleFormat? format = null;
        FormatInfo? info = null;
        var candidates = new List<string>(binaryCandidates);

        if (forced != null)
        {
            (info, format) = (forced, forcedFormat!);
            loaded = new Subtitle();
            try { format.LoadSubtitle(loaded, lines, name); } catch (Exception ex) { throw NotDetected(decoded, lines, $"source.format '{info.Id}' could not parse the input: {ex.Message}", ex); }
            if (loaded.Paragraphs.Count == 0) throw NotDetected(decoded, lines, $"source.format '{info.Id}' found no cues in the input.", null);
        }
        else
        {
            foreach (var (ci, cf) in formats.DetectionCandidates())
            {
                try
                {
                    if (!cf.IsMine(lines, name)) continue;
                    var s = new Subtitle();
                    cf.LoadSubtitle(s, lines, name);
                    if (s.Paragraphs.Count == 0) continue;
                    if (loaded == null) { loaded = s; format = cf; info = ci; }
                    candidates.Add(ci.Name);
                    if (!collectCandidates || candidates.Count >= 5) break;
                }
                catch
                {
                    // A format that throws on foreign input simply isn't this format.
                }
            }
            if (loaded == null) throw NotDetected(decoded, lines, "No subtitle format recognised the input.", null);
        }
        return CheckCues(new Loaded(loaded, format!, info!, decoded, null, null, candidates));
    }

    private Loaded CheckCues(Loaded l)
    {
        if (l.Subtitle.Paragraphs.Count > opts.MaxCues)
            throw new ApiException(422, ErrorCodes.InputLimitsExceeded, $"Input has {l.Subtitle.Paragraphs.Count} cues; max is {opts.MaxCues}.", "Split the file or raise MAX_CUES.");
        return l;
    }

    // ---------------- binary ----------------

    /// <summary>The caller's extension, only when it is one of the registry's binary extensions (temp names never use anything else).</summary>
    private string? CallerExtension(string? fileName)
    {
        var ext = Path.GetExtension(Path.GetFileName((fileName ?? "").Replace('\\', '/'))).ToLowerInvariant();
        return formats.BinaryExtensions.Contains(ext) ? ext : null;
    }

    /// <summary>
    /// Sniff (plan §3): a known binary extension, NUL bytes, or many C0 control characters. UTF-16/32
    /// with a BOM is text. A false positive only costs time: no binary match falls through to text.
    /// </summary>
    internal static bool LooksBinary(byte[] bytes, string? allowlistedExtension)
    {
        if (allowlistedExtension != null) return true;
        var span = bytes.AsSpan(0, Math.Min(bytes.Length, 64 * 1024));
        if (span.StartsWith(new byte[] { 0xFF, 0xFE }) || span.StartsWith(new byte[] { 0xFE, 0xFF }) || span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return false;
        var controls = 0;
        foreach (var b in span)
        {
            if (b == 0) return true;
            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0D or 0x0C or 0x1A)) controls++;
        }
        return controls > Math.Max(2, span.Length / 100);
    }

    private (Loaded? Result, List<string> Candidates) DetectBinary(byte[] bytes, string? callerExt, Profile profile, JobTemp temp, bool collect)
    {
        Loaded? first = null;
        var candidates = new List<string>();
        using var file = new TempFile(temp, bytes, ".bin");
        foreach (var (ci, cf) in formats.BinaryCandidates(callerExt))
        {
            try
            {
                file.WithExtension(ci.Extension);
                if (!cf.IsMine([], file.Path)) continue;
                var (s, cp, details) = ReadWith(cf, ci, bytes, file.Path, profile);
                if (s.Paragraphs.Count == 0 || !Plausible(s)) continue;
                first ??= new Loaded(s, cf, ci, BinaryDecoded(), cp, details, []);
                candidates.Add(ci.Name);
                if (!collect || candidates.Count >= 5) break;
            }
            catch
            {
                // A reader that throws on foreign bytes simply isn't this format.
            }
        }
        if (first != null) RestoreAfterProbes(first, bytes, file, profile);
        return (first, candidates);
    }

    /// <summary>
    /// Probing later candidates can leave libse globals (CurrentFrameRate from an EBU header, Cavena
    /// language ids) pointing at the wrong format; re-read the winner so the job sees its state.
    /// </summary>
    private void RestoreAfterProbes(Loaded winner, byte[] bytes, TempFile file, Profile profile)
    {
        SettingsScope.Apply(profile);
        file.WithExtension(winner.Info.Extension);
        ReadWith(formats.Resolve(winner.Info.Id, "/source/format").Format, winner.Info, bytes, file.Path, profile);
    }

    private Loaded LoadBinary(byte[] bytes, FormatInfo info, SubtitleFormat format, Profile profile, JobTemp temp)
    {
        using var file = new TempFile(temp, bytes, info.Extension);
        bool isMine;
        try { isMine = format.IsMine([], file.Path); } catch { isMine = false; }
        Subtitle s;
        string? cp;
        Dictionary<string, object?>? details;
        try
        {
            (s, cp, details) = ReadWith(formats.Resolve(info.Id, "/source/format").Format, info, bytes, file.Path, profile);
        }
        catch (Exception ex)
        {
            throw InvalidBinary(info, bytes, isMine, $"libse's {info.Name} reader failed: {ex.GetType().Name}.", ex);
        }
        if (s.Paragraphs.Count == 0)
            throw InvalidBinary(info, bytes, isMine, $"The bytes don't parse as {info.Name} (0 cues).", null);
        return new Loaded(s, format, info, BinaryDecoded(), cp, details, [info.Name]);
    }

    private ApiException InvalidBinary(FormatInfo info, byte[] bytes, bool isMine, string detail, Exception? inner)
    {
        var ex = new ApiException(422, ErrorCodes.InvalidBinaryInput, detail,
            $"Check that the file is a complete {info.Name} file (not truncated or re-encoded as text); in JSON send it with contentEncoding=base64. Use source.format=auto to let detection try other formats.", inner);
        ex.Debug["format"] = info.Id;
        ex.Debug["byteLength"] = bytes.Length;
        ex.Debug["isMine"] = isMine;
        return ex;
    }

    /// <summary>
    /// Read with a fresh instance. Returns the code page as a string (EBU: the GSI CPN "850"; PAC:
    /// "pac-&lt;name&gt;"; omitted for others) and an open per-format details object (contract §3).
    /// </summary>
    private static (Subtitle Subtitle, string? CodePage, Dictionary<string, object?>? Details) ReadWith(SubtitleFormat f, FormatInfo info, byte[] bytes, string path, Profile profile)
    {
        var inputCp = BinaryOptions.PacCodePage(profile.FormatOptions?.Pac?.InputCodePage, allowAuto: true);
        if (f is Pac pac) pac.CodePage = inputCp is >= 0 ? inputCp.Value : -1;
        var s = BinaryIO.Load(f, bytes, path);
        switch (f)
        {
            case Ebu:
                var h = Ebu.ReadHeader(bytes);
                return (s, h.CodePageNumber, new Dictionary<string, object?>
                {
                    ["characterTable"] = BinaryOptions.EbuCharacterTableName(h.CharacterCodeTableNumber), ["frameRate"] = h.FrameRate,
                    ["displayStandard"] = BinaryOptions.EbuDisplayStandardName(h.DisplayStandardCode), ["languageCode"] = h.LanguageCode,
                });
            case PacUnicode:
                return (s, "pac-unicode", new Dictionary<string, object?> { ["codePageConfidence"] = "format" });
            case Pac p:
                return (s, "pac-" + BinaryOptions.PacCodePageName(p.CodePage), new Dictionary<string, object?> { ["codePageConfidence"] = inputCp is >= 0 ? "forced" : "auto" });
            case Cavena890:
                var id = Configuration.Settings.SubtitleSettings.CurrentCavena890LanguageIdLine1;
                return (s, null, new Dictionary<string, object?> { ["languageId"] = id, ["characterSet"] = BinaryOptions.CavenaScript(id) });
            default:
                return (s, null, null);
        }
    }

    /// <summary>
    /// Auto-detection only: reject reads that are mostly not text. Cavena 890's IsMine accepts random
    /// bytes and its reader turns them into cues with ~75% letters; real subtitles are ~100%.
    /// </summary>
    internal static bool Plausible(Subtitle s)
    {
        var total = 0;
        var good = 0;
        foreach (var p in s.Paragraphs)
        {
            foreach (var c in p.Text ?? "")
            {
                if (char.IsWhiteSpace(c)) continue;
                total++;
                if (char.IsLetterOrDigit(c) || ".,!?'\"-:;()<>/[]{}#&%$€£*+=@_~♪♫…«»¿¡‘’“”–—".Contains(c)) good++;
            }
        }
        return total > 0 && good >= total * 0.9;
    }

    private (byte[] Bytes, string EncodingName) WriteBinary(Subtitle copy, FormatInfo info, SubtitleFormat format, Loaded source, Profile profile, string fileName, JobTemp temp, List<Warning> warnings)
    {
        var g = Configuration.Settings.General;
        var savedFps = g.CurrentFrameRate;
        var fo = profile.FormatOptions;
        try
        {
            if (profile.Target?.Fps is { } tfps) g.CurrentFrameRate = tfps;
            Ebu.EbuGeneralSubtitleInformation? header = null;
            string encName;
            var expected = new Subtitle(copy, false);
            switch (format)
            {
                case Ebu:
                    header = BinaryOptions.BuildEbuHeader(copy, fo?.EbuStl, profile.Target?.Fps, warnings);
                    var sourceFps = source.Format is Ebu
                        ? fo?.EbuStl?.ReadFrameRate is > 20 and { } rfr ? rfr : source.Details?.GetValueOrDefault("frameRate") as double?
                        : profile.Source?.Fps;
                    if (BinaryOptions.FrameRateChanged(header.DiskFormatCode, fo?.EbuStl?.FrameRate ?? profile.Target?.Fps, sourceFps is { } sf ? BinaryOptions.EbuFps(sf) : null) is { } frw)
                        warnings.Add(frw);
                    encName = "cp" + (BinaryOptions.IsStlHeader(copy.Header) ? copy.Header![..3] : header.CodePageNumber);
                    g.CurrentFrameRate = header.FrameRate;
                    break;
                case PacUnicode:
                    encName = "pac-unicode";
                    break;
                case Pac pac:
                    var cp = BinaryOptions.PacCodePage(fo?.Pac?.CodePage, allowAuto: false)
                             ?? (source.Format is Pac { CodePage: >= 0 } sp and not PacUnicode ? sp.CodePage : Pac.CodePageLatin);
                    pac.CodePage = cp;
                    encName = "pac-" + BinaryOptions.PacCodePageName(cp);
                    break;
                case Cavena890:
                    encName = "cavena-" + BinaryOptions.CavenaScript(BinaryOptions.CavenaLanguageFor(copy, fo?.Cavena890));
                    break;
                default:
                    encName = info.Id switch { "cheetahcaptionold" => "cheetah-ascii", "cheetahcaption" => "cheetah-latin", "capmakerplus" => "capmaker-latin", _ => "binary" };
                    break;
            }
            var frame = 1000.0 / (g.CurrentFrameRate > 1 ? g.CurrentFrameRate : 25);
            var bytes = BinaryIO.Save(format, copy, fileName, header);
            if (info.Lossy)
                warnings.Add(new("lossy-format", $"{info.Name} can't store everything: characters outside its table are dropped or replaced, and timing may shift."));
            Verify(bytes, info, format, expected, frame, encName, temp, profile, warnings);
            return (bytes, encName);
        }
        finally
        {
            g.CurrentFrameRate = savedFps;
        }
    }

    /// <summary>
    /// Re-read the written bytes and compare with what we meant to write (plan §4.3): characters the
    /// target couldn't encode, cues lost, and timing moved to frame boundaries become warnings, never silence.
    /// </summary>
    private void Verify(byte[] bytes, FormatInfo info, SubtitleFormat writer, Subtitle expected, double frameMs, string encName, JobTemp temp, Profile profile, List<Warning> warnings)
    {
        var g = Configuration.Settings.General;
        var ss = Configuration.Settings.SubtitleSettings;
        var (fps, ovr, l1, l2) = (g.CurrentFrameRate, Ebu.OverrideReadFrameRate, ss.CurrentCavena890LanguageIdLine1, ss.CurrentCavena890LanguageIdLine2);
        Subtitle back;
        try
        {
            Ebu.OverrideReadFrameRate = 0;
            using var file = new TempFile(temp, bytes, info.Extension);
            var reader = formats.Resolve(info.Id, "/source/format").Format;
            if (reader is Pac rp && writer is Pac wp) rp.CodePage = wp.CodePage;
            back = BinaryIO.Load(reader, bytes, file.Path);
        }
        catch
        {
            return; // verification is best effort; the bytes were written
        }
        finally
        {
            (g.CurrentFrameRate, Ebu.OverrideReadFrameRate, ss.CurrentCavena890LanguageIdLine1, ss.CurrentCavena890LanguageIdLine2) = (fps, ovr, l1, l2);
        }
        if (back.Paragraphs.Count != expected.Paragraphs.Count)
        {
            warnings.Add(new("lossy-format", $"{back.Paragraphs.Count} of {expected.Paragraphs.Count} cues read back from the {info.Name} output.", expected.Paragraphs.Count - back.Paragraphs.Count));
            return;
        }
        var replaced = 0;
        var drift = 0.0;
        for (var i = 0; i < expected.Paragraphs.Count; i++)
        {
            var a = expected.Paragraphs[i];
            var b = back.Paragraphs[i];
            replaced += MissingChars(Normalize(a.Text), Normalize(b.Text));
            drift = Math.Max(drift, Math.Max(Math.Abs(a.StartTime.TotalMilliseconds - b.StartTime.TotalMilliseconds), Math.Abs(a.EndTime.TotalMilliseconds - b.EndTime.TotalMilliseconds)));
        }
        if (replaced > 0)
            warnings.Add(new("characters-replaced", $"{replaced} character(s) couldn't be encoded in {encName} and were replaced or dropped.", replaced));
        if (drift > 1.5 && drift <= frameMs + 1.5)
            warnings.Add(new("frame-quantised", $"Times were rounded to {info.Name} frames (up to {Math.Round(drift)} ms at {Math.Round(1000 / frameMs, 3)} fps)."));
        else if (drift > frameMs + 1.5 && !info.Lossy)
            warnings.Add(new("lossy-format", $"Timing changed by up to {Math.Round(drift)} ms when read back from the {info.Name} output."));
    }

    private static string Normalize(string? text) =>
        new((HtmlUtil.RemoveHtmlTags(text ?? "", true)).Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>Characters of <paramref name="want"/> missing from <paramref name="got"/> (multiset difference).</summary>
    internal static int MissingChars(string want, string got)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in got) counts[c] = counts.GetValueOrDefault(c) + 1;
        var missing = 0;
        foreach (var c in want)
        {
            if (counts.TryGetValue(c, out var n) && n > 0) counts[c] = n - 1;
            else missing++;
        }
        return missing;
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
            hint = "The input looks binary but no binary format recognised it; set profile.source.format to the binary format (GET /v1/formats?kind=binary) for a precise error. " + hint;
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
        if (assa == null || info.Options != "assa") return;
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

    /// <summary>format-option-ignored for blocks that don't belong to this target (or, for source-side options, to the source).</summary>
    private static void WarnIgnoredOptions(FormatInfo target, FormatInfo source, Profile p, List<Warning> warnings)
    {
        var fo = p.FormatOptions;
        if (fo == null) return;
        foreach (var (block, present) in new[] { ("assa", fo.Assa != null), ("ebuStl", fo.EbuStl != null), ("pac", fo.Pac != null), ("cavena890", fo.Cavena890 != null), ("cheetah", fo.Cheetah != null) })
        {
            if (!present || block == target.Options || block == source.Options) continue;
            warnings.Add(new("format-option-ignored", $"formatOptions.{block} ignored for target '{target.Id}'."));
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
        Ebu.OverrideReadFrameRate = 0; // static, not part of Settings (plan §1)
        BinaryIO.RegisterEbuHelper();
    }

    public static void Apply(Profile p)
    {
        Reset();
        var g = Configuration.Settings.General;
        if (p.Source?.Fps is { } fps) g.CurrentFrameRate = fps;
        BinaryOptions.ApplyGlobals(p.FormatOptions);
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
