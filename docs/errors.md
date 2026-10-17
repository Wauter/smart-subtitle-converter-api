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
| 422 | <a id="unsafe-zip"></a>`unsafe-zip` | A zip entry name has a `..` path segment, an absolute path, a drive letter or a backslash. (`a..b.srt` is fine.) |
| 422 | <a id="input-limits-exceeded"></a>`input-limits-exceeded` | More than `MAX_CUES` cues or a line over `MAX_LINE_CHARS`. |
| 422 | <a id="conversion-failed"></a>`conversion-failed` | An operation or writer threw; `debug.operation` names it. |
| 429 | <a id="rate-limited"></a>`rate-limited` | Over `RATE_LIMIT_PER_MINUTE`; see `Retry-After`. |
| 503 | <a id="queue-full"></a>`queue-full` | Conversion queue full; retry after `Retry-After` seconds or scale out. |
| 504 | <a id="conversion-timeout"></a>`conversion-timeout` | Over `CONVERSION_TIMEOUT_SECONDS`; see `phase`. |
| 500 | <a id="internal-error"></a>`internal-error` | A bug. Report it with the `requestId`. |

## Warning codes

Warnings are not errors: the conversion succeeded. They appear in `warnings[]`, the manifest and
`X-Warnings`/`X-Warnings-Count`. The binary-format ones:

| code | Meaning |
|---|---|
| `characters-replaced` | Characters the target code page can't represent were replaced (`count`). |
| `frame-quantised` | Times were rounded to the target frame rate (at most one frame). |
| `lossy-format` | The target format can't keep everything (Cheetah styling, cue count changed, larger timing drift). |
| `encoding-ignored` | `encoding.output` / `responseEncoding=text` doesn't apply to binary output (always base64 in JSON). |
| `format-option-ignored` | A `formatOptions` block doesn't apply to this source or target. |
| `zip-nested-skipped` | A nested zip was skipped (also listed in the manifest as `status: "skipped"`). |
