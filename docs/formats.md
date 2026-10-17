# Formats

The API reads and writes every text format libse supports (SRT, WebVTT, ASS/SSA, TTML/DFXP, SBV, SMI, …) and
a set of binary broadcast formats. `GET /v1/formats` always has the current list; filter with `q`, `kind` and
`writable`.

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
     localhost:4041/v1/convert -OJ
# EBU STL -> SRT (detected by content; X-Detected-Code-Page: 850)
curl -F file=@movie.stl -F target=srt localhost:4041/v1/convert -OJ
# PAC with an explicit code page (auto-detection is weak for non-Latin scripts)
curl -F file=@movie.pac -F target=srt -F 'profile={"source":{"format":"pac"},"formatOptions":{"pac":{"inputCodePage":"greek"}}}' localhost:4041/v1/convert -OJ
# JSON: binary content must be base64; binary outputs always come back base64
curl localhost:4041/v1/convert/json -H 'content-type: application/json' \
     -d "{\"content\":\"$(base64 -w0 movie.stl)\",\"contentEncoding\":\"base64\",\"target\":\"srt\"}"
```

## Detection

Detection runs binary sniffing first (NUL/control bytes or a binary extension), then tries the binary readers with
Cavena 890 last and a plausibility check, so a text file named `.stl`/`.sub` stays text. When in doubt, set
`profile.source.format`: wrong bytes then give `422 invalid-binary-input`.

**PAC and FPC** share their header. Without a `.pac` / `.fpc` file name, auto-detection rejects such bytes with
`422 ambiguous-format` (listing both candidates) instead of guessing; set `profile.source.format`. Zip entries are
checked the same way, by entry name.

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
