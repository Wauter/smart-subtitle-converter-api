# Error codes

Every error is `application/problem+json` (RFC 7807), the `ApiProblem` schema in the OpenAPI document.
Branch on `code`; it never changes within v1. `type`, `title`, `status`, `code`, `detail` and `hint`
are always present, and `requestId` is present on request-level errors (it matches the `X-Request-Id`
header). `errors[]` holds JSON pointers into the request: profile fields (`/target/format`), query
parameters (`/query/textOnly`), JSON body fields (`/failFast`) and zip entries (`/file/entries/<i>` on
multipart, `/content/entries/<i>` on `POST /v1/convert/json`). `<i>` counts the *processed* entries in
zip order. Skipped entries (nested zips, macOS metadata) are not counted. `debug` (exception, stack
trace, detected encoding, first-line shape, binary probe results) is included only when
`ERRORS_VERBOSE=true` (the default). It never contains file content.

Extension members:

- `entry`: the zip entry name, on per-entry errors in the manifest and on `failFast=true` errors.
- `phase`: on `conversion-timeout`, either `queue` (the job never started; retry like a 503) or
  `running` (the conversion itself was too slow; split the file or simplify operations).
- `candidates`: on `ambiguous-format`, the format ids the bytes match (`pacscreenelectronics`,
  `pacunicodeunipac`).
- `groups`: on `ambiguous-batch-names`, an array of arrays of zip entry paths that clash. Output-path
  clashes also carry `outputs`, the clashing output paths. Here `errors[].pointer` is the **entry path**
  (e.g. `s1/ep1.srt`), not a JSON pointer.

Errors inside a zip manifest (`entries[].error`) are the same `ApiProblem` without `requestId`. The
request's `X-Request-Id` covers all entries.

| Status | code | Meaning / what to do |
|---|---|---|
| 400 | <a id="invalid-request"></a>`invalid-request` | Missing `file`/`content`/target, bad base64, both `preset` and `profile.extends`, a JSON field of the wrong type (pointer to the field), an invalid query value (pointer `/query/<name>`). |
| 400 | <a id="invalid-profile"></a>`invalid-profile` | The profile is invalid; see `errors[]` (pointer + hint). Unknown keys are errors. |
| 400 | <a id="format-option-invalid"></a>`format-option-invalid` | A `formatOptions` value is well-formed but not allowed: an EBU frame rate that conflicts with `target.fps`, an unknown code page or language, a GSI text field over 32 bytes, a Cavena title over 28 ASCII characters. |
| 400 | <a id="empty-input"></a>`empty-input` | The file (or zip) is empty, or the zip holds only skipped entries. |
| 401 | <a id="unauthorized"></a>`unauthorized` | Send `Authorization: Bearer <key>`. |
| 404 | <a id="not-found"></a>`not-found` | No such endpoint. |
| 404 | <a id="preset-not-found"></a>`preset-not-found` | Unknown preset; the hint lists known ones. Invalid mounted presets are listed by `GET /v1/profiles`. |
| 413 | <a id="payload-too-large"></a>`payload-too-large` | Body over the cap (`GET /v1/limits` → `maxRequestBodyBytes`), decoded content over `MAX_UPLOAD_BYTES`, or profile over `PROFILE_MAX_BYTES`. |
| 413 | <a id="zip-limits-exceeded"></a>`zip-limits-exceeded` | Too many entries, too large unpacked, or a compression ratio over `ZIP_MAX_RATIO`. |
| 415 | <a id="unsupported-media-type"></a>`unsupported-media-type` | Use `multipart/form-data` for `/v1/convert`, `application/json` for `/v1/convert/json`. |
| 422 | <a id="format-not-detected"></a>`format-not-detected` | No format matched; set `profile.source.format`. The hint describes what the input looks like (text or binary). |
| 422 | <a id="format-not-supported"></a>`format-not-supported` | Image-based or container formats (Blu-ray sup, VobSub, Matroska, ...), or a format this API can't read. |
| 422 | <a id="format-not-writable"></a>`format-not-writable` | The target format is read-only here (e.g. Avid STL, Ayato). `GET /v1/formats?writable=true` lists writable ones. |
| 422 | <a id="invalid-binary-input"></a>`invalid-binary-input` | `source.format` names a binary format but the bytes don't parse as it (truncated, wrong format), or text content was sent for a binary source format. `debug` has `format`, `byteLength`, `isMine`. |
| 422 | <a id="encoding-error"></a>`encoding-error` | Reserved: the input could not be decoded; set `encoding.input`. |
| 422 | <a id="unsafe-xml"></a>`unsafe-xml` | DTD / entity declarations are not allowed. |
| 422 | <a id="ambiguous-batch-names"></a>`ambiguous-batch-names` | Two or more zip entries share a folder and stem (the file name without its last extension, case-insensitive: `ep1.srt` + `EP1.vtt`). Their outputs would collide. Checked before any conversion, whatever `failFast` says. The same code is used when final output paths still collide after conversion (a `target.fileName` template without `{stem}`, `manifest.json`). Names are never renamed or overwritten. Rename the entries or use folders. |
| 422 | <a id="ambiguous-format"></a>`ambiguous-format` | Auto-detection found bytes that are valid as both PAC (Screen Electronics) and PAC Unicode (FPC): the two formats share their header, so only a `.pac` / `.fpc` file name decides. Without one (no file name, `.bin`, inline content) the input is rejected instead of guessed. `candidates` lists both ids; `errors[0].pointer` is `/source/format`. Set `profile.source.format` to `pac` or `fpc`, or send the file under its real extension (for `/v1/detect`: `fileName`). Zip entries are checked the same way, by entry name. |
| 422 | <a id="unsafe-zip"></a>`unsafe-zip` | A zip entry name has a `..` path segment, an absolute path, a drive letter or a backslash. (`a..b.srt` is fine.) |
| 422 | <a id="input-limits-exceeded"></a>`input-limits-exceeded` | More than `MAX_CUES` cues or a line over `MAX_LINE_CHARS`. |
| 422 | <a id="conversion-failed"></a>`conversion-failed` | An operation or writer threw; `debug.operation` names it. |
| 429 | <a id="rate-limited"></a>`rate-limited` | Over `RATE_LIMIT_PER_MINUTE`; see `Retry-After`. |
| 503 | <a id="queue-full"></a>`queue-full` | Conversion queue full; retry after `Retry-After` seconds or scale out. |
| 504 | <a id="conversion-timeout"></a>`conversion-timeout` | Over `CONVERSION_TIMEOUT_SECONDS`; see `phase`. |
| 500 | <a id="internal-error"></a>`internal-error` | A bug. Report it with the `requestId`. |

## Warning codes

Warnings are not errors: the conversion succeeded. They appear in `warnings[]`, the manifest and
`X-Warnings`/`X-Warnings-Count`. Each warning has `code` and `message`, and may have `count`, `hint` (what
to do) and `details` (`cueCount`, `cues` (1-based cue numbers, at most 20 listed),
`distinctCodePoints`, `codePoints` (`[{codePoint: "U+039A", count}]`, at most 20)). `X-Warnings` carries only
`code`, `message` and `count`; `hint` and `details` are in the JSON body and the manifest. Details never
contain subtitle text, only positions and code points.

Cue numbers in source warnings (`zero-length-cue`, `source-replacement-chars`) are positions in the
source; in read-back warnings (`characters-replaced`, `text-truncated`, `lossy-format`) they are positions
in the written output (after operations).

| code | Meaning |
|---|---|
| `characters-replaced` | Read back from a binary output, characters the target's character table can't hold were replaced or dropped. `count` = characters; `details` = cues, code points and `remedy`, the facts needed to choose a fix (see [Remedy](#remedy-characters-replaced)); `hint` says the same in words, with code points, never text. Nothing is switched automatically. U+FFFD already in the source is not counted here (see `source-replacement-chars`). |
| `text-truncated` | The binary writer cut cues short (Cavena 890 and Cheetah CAP Old cut long lines and drop extra lines). `count` = characters lost; `details.cues`. |
| `zero-length-cue` | Source cues with zero or negative duration (often metadata cues). `details.cues`. EBU STL can't store them (they read back as 99:59:59,999, reported as `lossy-format` with the cue numbers) and CapMaker Plus drops them. |
| `source-replacement-chars` | The source already contains U+FFFD replacement characters (damaged by an earlier wrong decoding). `count` = characters; `details.cues`. |
| `verification-failed` | The written binary output couldn't be read back; the file was written but not checked (`verification.verdict` = `unverified` with `reason`). |
| `frame-quantised` | Times were rounded to the target frame rate (at most one frame). |
| `frame-rate-changed` | EBU STL only: the written Disk Format Code implies a different frame rate (by more than 0.01 fps) than the intended one: the requested rate (`formatOptions.ebuStl.frameRate`, else `target.fps`) or, when none was requested, the source's (the rate an EBU source was read at, i.e. `readFrameRate` or its own code; else `source.fps`). Typical case: a 29.97 fps source or request written as `STL30.01`, because EBU Tech 3264 only defines `STL25.01` and `STL30.01`. The message names the code, both rates and the workaround (`formatOptions.ebuStl.frameRate`, or `headerFromSource:false`). No `count`. |
| `lossy-format` | The target format can't keep everything: Cheetah styling, cues missing on read-back (`details.cues`, matched by start time), zero-length cues the target can't store, or timing moved by more than one frame (`details.cues`). |
| `encoding-ignored` | `encoding.output` / `responseEncoding=text` doesn't apply to binary output (always base64 in JSON). |
| `format-option-ignored` | A `formatOptions` block doesn't apply to this source or target. |
| `zip-nested-skipped` | A nested zip was skipped (also listed in the manifest as `status: "skipped"`). |

## Verification (binary outputs)

Every binary output is read back with the same libse reader and compared with the cues that were meant
to be written. The result is `outputs[].verification` in `POST /v1/convert/json`, `entries[].verification`
(keyed by output path) in zip manifests, `outputs[].verification` in the multi-output manifest, and the
`X-Verification-Verdict` header for a single binary output from `POST /v1/convert`.

Greek SRT written as PAC with the default latin code page (code points shortened):

```json
{
  "verdict": "lossy",
  "cuesWritten": 3, "cuesReadBack": 3,
  "timing": { "maxShiftMs": 13, "meanShiftMs": 4.3, "frameMs": 41.7, "cuesOverOneFrame": 0 },
  "replacedChars": { "total": 50, "cueCount": 3, "cues": [1, 2, 3], "distinctCodePoints": 27,
                     "codePoints": [{ "codePoint": "U+03B1", "count": 5 }, { "codePoint": "U+03B9", "count": 4 }] }
}
```

With `formatOptions.pac.codePage: "greek"` the same input is `"verdict": "quantised"`.

- `verdict`: `lossless` (identical after normalising tags and whitespace, times within 1.5 ms),
  `quantised` (only times moved, each by at most one frame), `lossy` (characters replaced, text cut,
  cues missing, or a time moved by more than a frame) or `unverified` (read-back failed; `reason`).
- `timing`: largest and mean shift of start/end over the matched cues, the frame length used, and how
  many cues moved more than one frame. When the cue counts differ, cues are matched by start time.
- `replacedChars`, `truncatedCues`, `missingCues`: exact totals, first 20 cue numbers / code points.
- `zeroLengthCues`: zero-length cues the target couldn't store (EBU STL reads them back as 99:59:59,999).
  They are kept out of `timing` and make the verdict `lossy`; the `lossy-format` warning names them.
- Absent members mean "nothing found" (e.g. no `replacedChars` = no characters lost).

A round trip proves the file is self-consistent through libse's own reader; it does not prove that the
target playout or editing software accepts it.

## Remedy (`characters-replaced`)

`warnings[].details.remedy` (JSON body and manifests only, never `X-Warnings`) gives the caller the facts to
choose a fix instead of the API guessing one:

| field | meaning |
|---|---|
| `scripts` | Scripts of the lost code points (`Greek`, `Cyrillic`, `Latin extended`, `CJK`, `Japanese kana`, `Hangul`, `Hebrew`, `Arabic`, `Thai`, `other`). |
| `detectedLanguage` | libse's language detection for the source (a hint for the caller, not used to decide). |
| `currentEncoding` | What was written, e.g. `pac-latin`, `cp850`, `cavena-latin`. |
| `option` | The setting that controls it: `formatOptions.pac.codePage`, `formatOptions.ebuStl.characterTable` or `formatOptions.cavena890.language` (null for formats with a fixed table). |
| `currentValue` | The value of `option` in effect (explicit or default). |
| `allowedValues` | Every valid value of `option`. |
| `candidates` | Values that keep the characters; absent when none does. |
| `candidatesTested` | `true` for PAC: every non-ASCII code point of the output was written with each code page and read back, and only code pages that keep all of them are listed. `false`: the candidates follow from the script (EBU STL, Cavena 890). |
| `suggested` | Set only when exactly one candidate exists (e.g. Greek → `greek`); null when several fit (the caller picks, e.g. by language) or none. |
| `unicodeAlternative` | `fpc` (Unicode PAC), which keeps any character. |
| `note` | Known limits: libse's Cavena 890 writer garbles Greek/Cyrillic/Arabic whatever the language; Cavena's Latin languages share one table; PAC characters that survive alone in the current code page but are lost next to certain neighbours (libse's combining), where no code page helps. |

Greek SRT → PAC (default code page), hint and remedy (`allowedValues` shortened):

```json
"hint": "Lost characters are Greek (U+03B1, U+03B9, U+03B5, U+03BC, U+03BF, …) (detected source language: el); set formatOptions.pac.codePage to \"greek\" (the only value that keeps every character of this text, tested; current: latin) or use target fpc (Unicode PAC). Never switched automatically.",
"remedy": { "scripts": ["Greek"], "detectedLanguage": "el", "currentEncoding": "pac-latin",
            "option": "formatOptions.pac.codePage", "currentValue": "latin", "allowedValues": ["latin", "greek", "czech", "…"],
            "suggested": "greek", "candidates": ["greek"], "candidatesTested": true, "unicodeAlternative": "fpc" }
```

Traditional Chinese → PAC: `candidates` = `chinese-traditional`, `chinese-simplified`, `korean`, `japanese` (all
tested), `suggested` null. Polish → PAC: lossless with the default latin code page, so no warning.
