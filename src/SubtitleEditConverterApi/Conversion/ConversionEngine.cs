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

/// <summary>
/// What detection found. <c>bom</c> and <c>lineEndings</c> (crlf|lf|cr|mixed|none) are set for text input only.
/// <c>warnings</c> (zero-length-cue, source-replacement-chars) is set by /v1/detect only; conversions put them in the
/// response's warnings.
/// </summary>
public sealed record DetectedInfo(string Format, string Id, string Encoding, string EncodingConfidence, string? Language, int Paragraphs, List<string>? Candidates = null,
    string Kind = FormatKinds.Text, string? CodePage = null, Dictionary<string, object?>? FormatDetails = null,
    bool? Bom = null, string? LineEndings = null, List<Warning>? Warnings = null);
public sealed record ConvertedOutput(string FileName, FormatInfo Format, byte[] Bytes, string EncodingName, bool Unicode, int Cues, Verification? Verification = null);
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
public sealed partial class ConversionEngine(FormatRegistry formats, ApiOptions opts, ILogger<ConversionEngine> log)
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
            warnings.AddRange(SourceChecks.Check(subtitle, decoded, loaded.IsBinary));
            warnings.AddRange(FormatSourceChecks(loaded, bytes));
            if (loaded.IsBinary && (profile.Encoding?.Input != null || profile.Encoding?.InputFallback != null))
                warnings.Add(new("encoding-ignored", $"encoding.input/inputFallback don't apply to binary source '{sourceInfo.Id}'; its character set comes from the file."));

            var targetIds = new List<string> { profile.Target!.Format! };
            targetIds.AddRange(profile.Target.Additional ?? []);
            var (knownFps, fpsSource) = SourceFrameRate(loaded, profile);
            loaded = loaded with { Details = WithFrameRate(loaded.Details, knownFps ?? Configuration.Settings.General.CurrentFrameRate, fpsSource) };
            if (fpsSource == FpsAssumed && !FrameExactRoundTrip(targetIds, profile))
                warnings.Add(FrameRateAssumed(sourceInfo, Configuration.Settings.General.CurrentFrameRate, profile.Target.Fps));
            ApplyTiming(subtitle, profile, fpsSource == FpsTimeBased ? null : knownFps);
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
                        var (data, encName, verification) = WriteBinary(copy, info, format, loaded, profile, name, temp, language, warnings);
                        outputs.Add(new ConvertedOutput(name, info, data, encName, false, copy.Paragraphs.Count, verification));
                        continue;
                    }
                    if (loaded.IsBinary && string.Equals(profile.Encoding?.Output, "source", StringComparison.OrdinalIgnoreCase))
                        warnings.Add(new("encoding-ignored", "encoding.output=source has no text encoding to reuse for a binary source; wrote UTF-8."));
                    ApplyFormatOptions(copy, info, profile, warnings);
                    var g = Configuration.Settings.General;
                    var savedFps = g.CurrentFrameRate;
                    string text;
                    try
                    {
                        // target.fps is the rate frame-based text writers (SCC, MicroDVD, ...) write at.
                        if (profile.Target.Fps is > 0 and { } wfps) g.CurrentFrameRate = wfps;
                        text = format.ToText(copy, stem);
                    }
                    finally
                    {
                        g.CurrentFrameRate = savedFps;
                    }
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
                Kind: sourceInfo.Kind, CodePage: loaded.CodePage, FormatDetails: loaded.Details,
                Bom: loaded.IsBinary ? null : decoded.HadBom, LineEndings: loaded.IsBinary ? null : SourceChecks.LineEndings(decoded.Text));
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
            var warnings = SourceChecks.Check(l.Subtitle, l.Decoded, l.IsBinary);
            warnings.AddRange(FormatSourceChecks(l, bytes));
            var (knownFps, fpsSource) = SourceFrameRate(l, new Profile());
            l = l with { Details = WithFrameRate(l.Details, knownFps ?? Configuration.Settings.General.CurrentFrameRate, fpsSource) };
            return new DetectedInfo(l.Info.Name, l.Info.Id, l.Decoded.EncodingName, l.Decoded.Confidence, Language.DetectOrNull(l.Subtitle), l.Subtitle.Paragraphs.Count,
                l.Candidates, l.Info.Kind, l.CodePage, l.Details,
                Bom: l.IsBinary ? null : l.Decoded.HadBom, LineEndings: l.IsBinary ? null : SourceChecks.LineEndings(l.Decoded.Text),
                Warnings: warnings.Count > 0 ? warnings : null);
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
        Dictionary<string, object?>? details = null;
        if (format is ScenaristClosedCaptions && profile.Source?.Fps == null && InferSccFrameRate(lines) is { } scc)
        {
            // libse reads SCC frames at the global frame rate (default 23.976); read again at the rate the timecodes imply.
            Configuration.Settings.General.CurrentFrameRate = scc.Fps;
            var again = new Subtitle();
            format!.LoadSubtitle(again, lines, name);
            if (again.Paragraphs.Count > 0) loaded = again;
            details = new() { ["frameRate"] = scc.Fps, ["frameRateSource"] = FpsInferred, ["frameRateBasis"] = scc.Basis };
        }
        return CheckCues(new Loaded(loaded, format!, info!, decoded, null, details, candidates));
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
        ApiException? firstDeferred = null;
        foreach (var (ci, cf) in formats.BinaryCandidates(callerExt))
        {
            try
            {
                file.WithExtension(ci.Extension);
                bool mine;
                try { mine = cf.IsMine([], file.Path); } catch { mine = false; }
                // libse's PAC IsMine misses some real files; a .pac name is enough to try the PAC reader (and its code-page probe).
                if (!mine && !(IsPac8Bit(cf) && callerExt == ".pac")) continue;
                var (s, cp, details) = ReadWithProbe(cf, ci, bytes, file.Path, profile, out var deferred);
                if (s.Paragraphs.Count == 0) continue;
                if (deferred == null ? !Plausible(s) : !DeferredReadable(deferred)) continue;
                if (first == null)
                {
                    first = new Loaded(s, cf, ci, BinaryDecoded(), cp, details, []);
                    firstDeferred = deferred;
                }
                candidates.Add(ci.Name);
                if (!collect || candidates.Count >= 5) break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A reader that throws on foreign bytes simply isn't this format.
            }
        }
        if (first != null)
        {
            CheckPacAmbiguity(first, bytes, callerExt, file, profile);
            if (firstDeferred != null) throw firstDeferred;
            RestoreAfterProbes(first, bytes, file, profile);
        }
        return (first, candidates);
    }

    private static readonly string[] PacFamily = ["pacscreenelectronics", "pacunicodeunipac"];

    /// <summary>
    /// PAC and PAC Unicode (FPC) share their header bytes, so the bytes alone can't tell them apart. Only a
    /// .pac / .fpc file name decides; otherwise reject (Wauter, 2026-10-17: ambiguity is rejected, never guessed).
    /// An explicit source.format never gets here.
    /// </summary>
    private void CheckPacAmbiguity(Loaded winner, byte[] bytes, string? callerExt, TempFile file, Profile profile)
    {
        if (!PacFamily.Contains(winner.Info.Id) || callerExt is ".pac" or ".fpc") return;
        var otherId = PacFamily.First(id => id != winner.Info.Id);
        var (otherInfo, other) = formats.Resolve(otherId, "/source/format");
        bool both;
        try
        {
            file.WithExtension(otherInfo.Extension);
            if (!other.IsMine([], file.Path)) both = false;
            else
            {
                var (s, _, _) = ReadWithProbe(other, otherInfo, bytes, file.Path, profile, out var deferred);
                both = s.Paragraphs.Count > 0 && (deferred == null ? Plausible(s) : DeferredReadable(deferred));
            }
        }
        catch
        {
            both = false;
        }
        if (!both) return;
        var names = PacFamily.Select(id => formats.Find(id)!).ToList();
        var ex = new ApiException(422, ErrorCodes.AmbiguousFormat,
            $"The bytes are valid as both {names[0].Name} and {names[1].Name}: the two formats share their header, and no .pac or .fpc file name decides it.",
            "Set profile.source.format to 'pac' (PAC, 8-bit code pages) or 'fpc' (PAC Unicode), or send the file with its .pac / .fpc name (for /v1/detect: fileName).")
        {
            Errors = [new FieldError("/source/format", "Ambiguous: pacscreenelectronics or pacunicodeunipac.", "Set it to 'pac' or 'fpc'.")],
        };
        ex.Extensions["candidates"] = PacFamily;
        ex.Debug["callerExtension"] = callerExt;
        throw ex;
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
        catch (ApiException ex) when (ex.Code == ErrorCodes.AmbiguousEncoding && !DeferredReadable(ex))
        {
            // No PAC code page gives readable text: these bytes aren't a PAC file, whatever the code page.
            throw InvalidBinary(info, bytes, isMine, $"The bytes don't decode as {info.Name} text in any code page.", ex);
        }
        catch (Exception ex) when (ex is not ApiException)
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
        var r = ReadWithProbe(f, info, bytes, path, profile, out var deferred);
        if (deferred != null) throw deferred;
        return r;
    }

    private static bool IsPac8Bit(SubtitleFormat f) => f is Pac and not PacUnicode;

    /// <summary>A deferred PAC code-page problem still proves the bytes read as PAC when some code page gave text.</summary>
    private static bool DeferredReadable(ApiException deferred) => deferred.Extensions.GetValueOrDefault("candidates") is string[] { Length: > 0 };

    /// <summary>
    /// <see cref="ReadWith"/>, but a PAC code page that the probe can't decide comes back in <paramref name="deferred"/>
    /// (the subtitle is then the first readable decoding, only good for format detection) instead of being thrown,
    /// so detection can still weigh the PAC/FPC ambiguity first.
    /// </summary>
    private static (Subtitle Subtitle, string? CodePage, Dictionary<string, object?>? Details) ReadWithProbe(SubtitleFormat f, FormatInfo info, byte[] bytes, string path, Profile profile, out ApiException? deferred)
    {
        deferred = null;
        var inputCp = BinaryOptions.PacCodePage(profile.FormatOptions?.Pac?.InputCodePage, allowAuto: true);
        if (f is Pac pac8 && IsPac8Bit(f) && inputCp is not >= 0)
        {
            var probe = PacProbe.Run(bytes, path);
            if (probe.Problem != null)
            {
                deferred = probe.Problem;
                return (probe.Fallback ?? new Subtitle(), null, null);
            }
            pac8.CodePage = probe.CodePage;
            return (probe.Subtitle!, "pac-" + BinaryOptions.PacCodePageName(probe.CodePage),
                new Dictionary<string, object?> { ["codePageConfidence"] = "probed", ["probedLanguage"] = probe.Language });
        }
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
                return (s, "pac-" + BinaryOptions.PacCodePageName(p.CodePage), new Dictionary<string, object?> { ["codePageConfidence"] = "forced" });
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

    private (byte[] Bytes, string EncodingName, Verification Verification) WriteBinary(Subtitle copy, FormatInfo info, SubtitleFormat format, Loaded source, Profile profile, string fileName, JobTemp temp, string? language, List<Warning> warnings)
    {
        var g = Configuration.Settings.General;
        var savedFps = g.CurrentFrameRate;
        var fo = profile.FormatOptions;
        try
        {
            if (profile.Target?.Fps is { } tfps) g.CurrentFrameRate = tfps;
            Ebu.EbuGeneralSubtitleInformation? header = null;
            var freshEbuHeader = false;
            string encName;
            var expected = new Subtitle(copy, false);
            switch (format)
            {
                case Ebu:
                    freshEbuHeader = !(BinaryOptions.IsStlHeader(copy.Header) && fo?.EbuStl?.HeaderFromSource != false);
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
            if (format is Ebu && freshEbuHeader && FormatDefaults.Ebu(bytes, fo?.EbuStl, copy) is { } ebuDefaults) warnings.Add(ebuDefaults);
            if (format is Cavena890 && FormatDefaults.Cavena(bytes, fo?.Cavena890, fileName, temp) is { } cavenaDefaults) warnings.Add(cavenaDefaults);
            if (info.Lossy)
                warnings.Add(new("lossy-format", $"{info.Name} can't store everything: characters outside its table are dropped or replaced, and timing may shift."));
            var remedyCtx = format switch
            {
                Ebu => new RemedyContext(BinaryOptions.EbuCharacterTableName(
                    BinaryOptions.IsStlHeader(copy.Header) && copy.Header!.Length >= 14 ? copy.Header[12..14] : header!.CharacterCodeTableNumber)),
                PacUnicode => null,
                Pac p => new RemedyContext(BinaryOptions.PacCodePageName(p.CodePage), lost => PacCovering(lost, p.CodePage, temp)),
                Cavena890 => new RemedyContext(BinaryOptions.CavenaLanguageNames.FirstOrDefault(n => BinaryOptions.CavenaLanguages[n].Id == BinaryOptions.CavenaLanguageFor(copy, fo?.Cavena890))),
                _ => null,
            };
            var verification = Verify(bytes, info, format, expected, frame, encName, temp, language, warnings, remedyCtx);
            return (bytes, encName, verification);
        }
        finally
        {
            g.CurrentFrameRate = savedFps;
        }
    }

    /// <summary>
    /// Re-read the written bytes and compare with what we meant to write (plan §4.3, structured in 1.2.0):
    /// characters the target couldn't encode, cut-off text, lost cues and timing moved to frame boundaries
    /// become a <see cref="Verification"/> plus warnings, never silence. A reader that throws on our own
    /// output gives verdict "unverified" with the reason and a verification-failed warning.
    /// </summary>
    private Verification Verify(byte[] bytes, FormatInfo info, SubtitleFormat writer, Subtitle expected, double frameMs, string encName, JobTemp temp, string? language, List<Warning> warnings, RemedyContext? remedyCtx = null)
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
        catch (Exception ex)
        {
            // Exception type only: messages from readers could quote file content.
            log.LogWarning("verify.readback-failed format={format} exception={exception}", info.Id, ex.GetType().Name);
            var reason = $"The {info.Name} output couldn't be read back by libse's {info.Name} reader ({ex.GetType().Name}).";
            warnings.Add(new("verification-failed", reason + " The file was written but not checked.", null,
                "Check the file in the target software; report it with the requestId if it was written from valid input."));
            return Verifier.Unverified(expected.Paragraphs.Count, reason);
        }
        finally
        {
            (g.CurrentFrameRate, Ebu.OverrideReadFrameRate, ss.CurrentCavena890LanguageIdLine1, ss.CurrentCavena890LanguageIdLine2) = (fps, ovr, l1, l2);
        }
        var (v, w) = Verifier.Compare(expected, back, info, frameMs, encName, language, remedyCtx);
        warnings.AddRange(w);
        return v;
    }

    private const int MaxProbeCodePoints = 1500;

    /// <summary>
    /// v1.2.1: which other PAC code pages keep every given code point (all non-ASCII code points of the output).
    /// Writes the code points (only those, never cue text; 4 per cue so CJK lines stay short) with each code page,
    /// the current one included, and reads them back. Null when the probe can't tell.
    /// </summary>
    private string[]? PacCovering(IReadOnlyCollection<int> lost /* = the code points to keep */, int currentCodePage, JobTemp temp)
    {
        if (lost.Count == 0 || lost.Count > MaxProbeCodePoints) return null;
        var probe = new Subtitle();
        var chunk = new List<string>();
        foreach (var cp in lost.OrderBy(c => c))
        {
            chunk.Add(char.ConvertFromUtf32(cp));
            if (chunk.Count == 4) Flush();
        }
        Flush();
        void Flush()
        {
            if (chunk.Count == 0) return;
            var n = probe.Paragraphs.Count;
            probe.Paragraphs.Add(new Paragraph(string.Concat(chunk), 1000 + n * 2000, 2000 + n * 2000));
            chunk.Clear();
        }
        var g = Configuration.Settings.General;
        var savedFps = g.CurrentFrameRate;
        var keep = new List<string>();
        try
        {
            g.CurrentFrameRate = 25;
            for (var i = 0; i < BinaryOptions.PacCodePages.Length; i++)
            {
                try
                {
                    var bytes = BinaryIO.Save(new Pac { CodePage = i }, new Subtitle(probe, false), "probe.pac", null);
                    using var file = new TempFile(temp, bytes, ".pac");
                    var back = BinaryIO.Load(new Pac { CodePage = i }, bytes, file.Path);
                    var have = string.Concat(back.Paragraphs.Select(p => p.Text)).EnumerateRunes().Select(r => r.Value).ToHashSet();
                    if (lost.All(have.Contains)) keep.Add(BinaryOptions.PacCodePages[i]);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // libse can't write or read these code points with this code page: it doesn't keep them.
                    log.LogDebug("verify.pac-probe codePage={codePage} exception={exception}", i, ex.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning("verify.pac-probe-failed exception={exception}", ex.GetType().Name);
            return null;
        }
        finally
        {
            g.CurrentFrameRate = savedFps;
        }
        return [.. keep];
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

    internal const string FpsProfile = "profile", FpsHeader = "header", FpsInferred = "inferred", FpsAssumed = "assumed", FpsTimeBased = "time-based";

    /// <summary>
    /// Frame-based sources store frames (timecodes with a frame part, frame counts): their times depend on a frame rate.
    /// Binary formats, SCC and libse's frame-based text formats (MicroDVD, ...). SubRip, WebVTT, TTML, ASS store time.
    /// </summary>
    internal static bool IsFrameBasedSource(FormatInfo info, SubtitleFormat f) =>
        info.Kind == FormatKinds.Binary || f.IsFrameBased || f is ScenaristClosedCaptions;

    /// <summary>
    /// The source frame rate and where it came from: profile (source.fps), header (EBU DFC or readFrameRate),
    /// inferred (SCC timecodes), assumed (a frame-based source that states no rate: libse's current rate) or
    /// time-based (no frame rate involved).
    /// </summary>
    internal static (double? Fps, string Source) SourceFrameRate(Loaded l, Profile p)
    {
        if (p.Source?.Fps is > 0 and { } sfps) return (sfps, FpsProfile);
        if (!IsFrameBasedSource(l.Info, l.Format)) return (null, FpsTimeBased);
        if (l.Format is Ebu)
        {
            if (p.FormatOptions?.EbuStl?.ReadFrameRate is > 20 and { } rfr) return (rfr, FpsProfile);
            if (l.Details?.GetValueOrDefault("frameRate") is double hf && hf > 0) return (hf, FpsHeader);
        }
        if (l.Details?.GetValueOrDefault("frameRateSource") as string == FpsInferred && l.Details["frameRate"] is double inf) return (inf, FpsInferred);
        return (null, FpsAssumed);
    }

    private static Dictionary<string, object?>? WithFrameRate(Dictionary<string, object?>? details, double fps, string source)
    {
        if (source == FpsTimeBased) return details;
        var d = details == null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(details);
        var rounded = Math.Round(fps, 3);
        // An EBU header's own frameRate stays as declared; a different read rate (source.fps, readFrameRate) is added beside it.
        if (d.GetValueOrDefault("frameRate") is double declared) { if (Math.Abs(declared - rounded) > 0.001) d["readFrameRate"] = rounded; }
        else d["frameRate"] = rounded;
        d["frameRateSource"] = source;
        return d;
    }

    /// <summary>Reading and writing the same frame-based format at the same (assumed) rate keeps every frame number: nothing to warn about.</summary>
    /// <remarks>EBU STL targets are excluded: their writer uses the header's rate (default 25), not the rate the source was read at.</remarks>
    private bool FrameExactRoundTrip(List<string> targets, Profile p) =>
        p.Target?.Fps == null && targets.All(t => formats.Find(t) is { } ti && formats.Resolve(ti.Id, "/target/format").Format is var tf && IsFrameBasedSource(ti, tf) && tf is not Ebu);

    internal const string FrameRateAssumedCode = "frame-rate-assumed";

    private static Warning FrameRateAssumed(FormatInfo source, double assumed, double? targetFps) =>
        new(FrameRateAssumedCode,
            $"{source.Name} stores times as frames, and the file doesn't say its frame rate; it was read at {assumed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} fps (libse's default)." +
            (targetFps is > 0 ? " target.fps was applied as the output rate only, without rescaling, because the source rate is unknown." : ""),
            null,
            "Set profile.source.fps to the file's frame rate (e.g. 25 for PAL, 29.97 for NTSC); cue times are wrong if the assumption is.",
            new WarningDetails(FrameRate: Math.Round(assumed, 3)));

    /// <summary>
    /// SCC timecodes: a ';' (drop-frame) separator means 29.97 fps; a frame number of 25 or more rules out 23.976/24/25
    /// (frame 24 is still legal at 25 fps) and means NTSC 29.97 non-drop. Otherwise the rate can't be told (null).
    /// </summary>
    internal static (double Fps, string Basis)? InferSccFrameRate(List<string> lines)
    {
        var maxFrame = -1;
        foreach (var line in lines)
        {
            var m = SccTimecode().Match(line.TrimStart());
            if (!m.Success) continue;
            if (m.Groups[1].Value == ";") return (29.97, "drop-frame timecodes (;)");
            maxFrame = Math.Max(maxFrame, int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        return maxFrame >= 25 ? (29.97, $"non-drop timecodes with frame numbers up to {maxFrame}") : null;
    }

    [GeneratedRegex(@"^\d+:\d\d:\d\d([:;,.])(\d\d)\s")]
    private static partial Regex SccTimecode();

    internal const string ProgrammeStartNotApplied = "programme-start-not-applied";

    /// <summary>Source checks that need the format: EBU STL with a non-zero Time Code Start of Programme.</summary>
    private static List<Warning> FormatSourceChecks(Loaded l, byte[] bytes)
    {
        var warnings = new List<Warning>();
        if (l.Format is Ebu && bytes.Length >= 1024)
        {
            string tcp;
            try { tcp = Ebu.ReadHeader(bytes).TimeCodeStartOfProgramme ?? ""; } catch { tcp = ""; }
            if (tcp.Length == 8 && tcp.All(char.IsAsciiDigit) && tcp != "00000000")
            {
                var tc = $"{tcp[..2]}:{tcp[2..4]}:{tcp[4..6]}:{tcp[6..]}";
                warnings.Add(new(ProgrammeStartNotApplied,
                    $"The EBU STL header's Time Code Start of Programme is {tc}. Cue times were left as stored in the file; the programme start was not subtracted.",
                    null,
                    $"If the output should start at 00:00:00, subtract the programme start yourself (timing.offsetMs, e.g. -{(int.Parse(tcp[..2]) * 3600 + int.Parse(tcp[2..4]) * 60 + int.Parse(tcp[4..6])) * 1000} for {tc[..8]}); if the target is timecode-based, keeping it is usually right.",
                    new WarningDetails(Timecode: tc)));
            }
        }
        return warnings;
    }

    /// <summary>
    /// target.fps is the output rate. Times are rescaled (ChangeFrameRate) only from a known source rate: source.fps,
    /// or the rate a frame-based source states or implies (<paramref name="knownSourceFps"/>). Time-based sources and
    /// frame-based sources of unknown rate keep their wall-clock times.
    /// </summary>
    private static void ApplyTiming(Subtitle s, Profile p, double? knownSourceFps)
    {
        var t = p.Timing;
        if (t?.OffsetMs is { } off && off != 0) s.AddTimeToAllParagraphs(TimeSpan.FromMilliseconds(off));
        if (p.Target?.Fps is > 0 and { } tfps && (p.Source?.Fps ?? knownSourceFps) is > 0 and { } from && Math.Abs(from - tfps) > 0.0001)
            s.ChangeFrameRate(from, tfps);
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
