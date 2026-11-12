# Formats

The API reads and writes every text format libse supports (SRT, WebVTT, ASS/SSA, TTML/DFXP, SBV, SMI, …) and
a set of binary broadcast formats. `GET /v1/formats` always has the current list of text and binary formats; filter with `q`, `kind`,
`textOnly` and `writable`.

## Binary broadcast formats

- **Read and write:** **EBU STL** (`ebustl`, `.stl`), **PAC** (`pac`), **FPC** (`fpc`), **Cavena 890**
  (`cavena890`, `.890`), **CapMaker Plus** (`capmakerplus`), **Cheetah CAP** (`cheetahcaption`,
  `cheetahcaptionold`; lossy) and more.
- **Read-only:** Avid STL, Ayato, Spruce STL and others. As a target they give `422 format-not-writable`.
- **Not supported:** image-based and container formats (Blu-ray sup, VobSub, Matroska, MP4, …) are listed with
  `kind` but give `422 format-not-supported`.

`GET /v1/formats?kind=binary&writable=true` has the current list of writable binary formats.

## Examples

```bash
# SRT -> EBU STL (25 fps, CPN 850, language auto-detected), with GSI fields
curl -F file=@movie.srt -F target=ebustl \
     -F 'profile={"formatOptions":{"ebuStl":{"frameRate":25,"originalProgrammeTitle":"My Film","language":"en"}}}' \
     localhost:8080/v1/convert -OJ
# EBU STL -> SRT (detected by content; X-Detected-Code-Page: 850)
curl -F file=@movie.stl -F target=srt localhost:8080/v1/convert -OJ
# PAC with an explicit code page (auto-detection rejects files it can't decide with 422 ambiguous-encoding)
curl -F file=@movie.pac -F target=srt -F 'profile={"source":{"format":"pac"},"formatOptions":{"pac":{"inputCodePage":"greek"}}}' localhost:8080/v1/convert -OJ
# JSON: binary content must be base64; binary outputs always come back base64
curl localhost:8080/v1/convert/json -H 'content-type: application/json' \
     -d "{\"content\":\"$(base64 -w0 movie.stl)\",\"contentEncoding\":\"base64\",\"target\":\"srt\"}"
```

## Detection

Detection runs binary sniffing first (NUL/control bytes or a binary extension), then tries the binary readers with
Cavena 890 last and a plausibility check, so a text file named `.stl`/`.sub` stays text. When in doubt, set
`profile.source.format`: wrong bytes then give `422 invalid-binary-input`.

**PAC and FPC** share their header. Without a `.pac` / `.fpc` file name, auto-detection rejects such bytes with
`422 ambiguous-format` (listing both candidates) instead of guessing; set `profile.source.format`. A `.pac` file
name (or `source.format: pac`) is enough to try the PAC reader, even when libse's PAC check doesn't recognise the
header (seen with some Hebrew and Arabic files).

**PAC input code page.** With `formatOptions.pac.inputCodePage` unset or `auto`, every one of the 13 code pages
decodes the file, and a code page counts only when its text contains words of a language it serves (Greek → `el`,
Czech → `cs`/`sk`, Cyrillic → `ru`/`uk`/`bg`/…, Latin → Western languages, and so on). Letter statistics alone don't
count: Greek PAC read with the Czech page still looks like Latin letters. One answer is used and reported as
`formatDetails.codePageConfidence: "probed"` (with `probedLanguage`); none or several with different text give
`422 ambiguous-encoding` with the remaining `candidates`. An explicit `inputCodePage` is used as is (`"forced"`).
Bytes that don't give plausible text in any code page are `422 invalid-binary-input`. Very short files without a
single common word need `inputCodePage`. The latin and czech code pages store some letters (such as ž, š and é)
identically, so Czech text that uses only those reports `pac-latin` with the same, correct text; text with ř, č or ě
reports `pac-czech`.

**Text encodings.** BOM, then valid UTF-8, then `encoding.inputFallback` if set. Otherwise the same word test runs
over windows-1252/1250/1251/1253/1254/1255/1256/1257/874, ISO-8859-1/2/7/9, GB18030, Big5, Shift_JIS and EUC-KR.
Decodings with U+FFFD or C1 control characters are dropped when a clean one exists; encodings that give the same
text are one answer (windows-1252 first, Windows code pages before their ISO-8859 siblings). One answer:
`encodingConfidence: "probed"`. Several different texts: `422 ambiguous-encoding`. No words at all: libse's
heuristic (`"heuristic"`) plus an `encoding-guessed` warning.

## Frame rates and `target.fps`

Frame-based formats store cue times as frame numbers, so a frame rate is needed to turn them into times. Where the
rate comes from:

- **The request:** `source.fps` for the source, `target.fps` for the output.
- **The file:** EBU STL records its rate in the header (Disk Format Code; `formatOptions.ebuStl.readFrameRate`
  overrides it). SCC timecodes can imply 29.97: drop-frame `;` separators, or frame numbers of 25 and above.
- **Nowhere:** PAC, PAC Unicode (FPC), Cavena 890, Cheetah CAP, CapMaker Plus, Avid STL, MicroDVD and a few other
  frame-based text formats have no frame-rate field, and neither do SCC files whose timecodes don't reveal it.

The output is written at `target.fps`, else at the source's rate (`source.fps`, or the rate the file records or
implies). Set only `source.fps` and the output uses the same rate, which is what you want in most cases. Times are
rescaled from one rate to the other only when `target.fps` differs from a known source rate (for example `25` →
`23.976`). Time-based sources (SRT, WebVTT, TTML, ASS, …) keep their wall-clock times when only `target.fps` is
set: a one-hour WebVTT written as SCC with `target.fps: 29.97` still ends at one hour.

The API doesn't guess a rate. When one is needed and neither the file nor the request gives it, the conversion stops
with [`frame-rate-required`](errors.md#frame-rate-required):

- a source with no recorded rate needs `source.fps` when its frames have to become times: a time-based or SCC
  output, `target.fps`, or millisecond timing settings (`timing.offsetMs`, `adjustDurationMs`, `bridgeGapsMs`,
  `minGapMs`);
- a frame-based output with no rate field (or SCC) needs `target.fps` or `source.fps` when the source is time-based.

No rate is needed to convert between formats that record none (PAC to PAC, PAC to Cavena 890, …) without those
settings: frame numbers carry over unchanged. SCC drop-frame output is always 29.97. iTunes Timed Text output is
written with SMPTE frame time codes and declares their rate, so it needs one like a frame-based target. `GET /v1/formats` marks every
format that stores frames with `frameBased: true`. EBU STL output never needs a rate from the request: its rate is
`formatOptions.ebuStl.frameRate`, else `target.fps`, else the source's rate when it is an EBU rate, else 25.
`detected.formatDetails.frameRate` / `frameRateSource` (`profile`, `header`, `inferred`, or `unknown` with no
`frameRate`) say which rate was used.

**Rounding.** A time exactly halfway between two frames can land on either side: EBU STL rounds it down and PAC
rounds it up, so 3.500 s at 25 fps becomes 3.480 s in EBU STL and 3.520 s in PAC. Both are within one frame
(`frame-quantised`).

**EBU STL programme start.** A non-zero Time Code Start of Programme (often `10:00:00:00`) is reported as
`programme-start-not-applied`; cue times stay as stored. Use `timing.offsetMs` to subtract it.

**Header defaults.** A fresh EBU STL header (SRT/WebVTT/… → STL, or `headerFromSource:false`) and Cavena 890 header
carry identity fields even when the request sets none: libse's `No Title` / `USA`, a detected or fallback language,
Cavena's file-name title. These are reported as `format-defaults-applied` with the values written; set the matching
`formatOptions` for deliveries.

## Format options

Per-format options live in `profile.formatOptions.{ebuStl,pac,cavena890,cheetah}` (see `/v1/schemas/profile.json`).
Bad values give `400 format-option-invalid`; a block that doesn't apply to the source or target gives a
`format-option-ignored` warning.

## Verification and lossy writes

Lossy writes are never silent. Every binary output is read back and reported as `verification`
(`lossless` / `quantised` / `lossy` / `unverified`, with cue numbers and code points), plus the warnings
`characters-replaced` (with a `remedy`: the setting to change and, for PAC, the code pages tested to keep every
character), `text-truncated`, `frame-quantised` and `lossy-format`. Nothing is switched automatically.

A round trip proves the file is self-consistent, not that the playout system accepts it. Field reference:
[errors.md: Verification](errors.md#verification-binary-outputs) and
[Remedy](errors.md#remedy-characters-replaced).

## EBU STL frame rates

The GSI Disk Format Code is the only place the rate is stored, and EBU Tech 3264 only defines `STL25.01` and
`STL30.01`, so 29.97 fps is written as `STL30.01` and read back as 30. Whenever the written code implies a
different rate than the requested (`frameRate`/`target.fps`) or source rate, the response carries a
`frame-rate-changed` warning. EBU→EBU reuses the source GSI by default; set `formatOptions.ebuStl.frameRate` or
`headerFromSource:false` to change that.

## Temporary files

Binary files are handled in a private `0700` temp dir with random names, deleted after every job, including
timeouts and worker restarts, and swept at startup. See [security.md](security.md).
