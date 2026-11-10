# Errors and warnings

The API answers with two kinds of messages:

- An **error** means nothing was converted (or, for storage outputs, nothing new was confirmed written). The
  request was refused or the conversion failed. Each error has a stable `code`.
- A **warning** means the conversion succeeded, but something in the source or the output deserves a look. A
  warning never changes your file by itself; it tells you what happened and which setting would change it.

Every code below has three short parts: **What** happened, **Why** the API did this, and **What to do**. A
**For integrators** line follows with the HTTP status, fields and JSON details; skip it if you only need the
plain explanation.

Setting names are profile fields, written as a path: `source.format` means
`{"source": {"format": "..."}}` in the `profile`. With the MCP server they are the same fields under `profile`.

## Index

### Errors

| Code | Status | In short |
|---|---|---|
| [`invalid-request`](#invalid-request) | 400 | The request itself is incomplete or malformed |
| [`invalid-profile`](#invalid-profile) | 400 | A profile setting is wrong or unknown |
| [`format-option-invalid`](#format-option-invalid) | 400 | A format option value is not allowed |
| [`empty-input`](#empty-input) | 400 | The file is empty |
| [`host-not-allowed`](#host-not-allowed) | 400 | The API was called by a host name it doesn't accept |
| [`unauthorized`](#unauthorized) | 401 | Missing or wrong API key |
| [`origin-not-allowed`](#origin-not-allowed) | 403 | A web page from another site tried to call the API |
| [`not-found`](#not-found) | 404 | No such endpoint |
| [`preset-not-found`](#preset-not-found) | 404 | No preset with that name |
| [`payload-too-large`](#payload-too-large) | 413 | File, request or profile too big |
| [`unsupported-media-type`](#unsupported-media-type) | 415 | Wrong request content type |
| [`format-not-detected`](#format-not-detected) | 422 | The file's format could not be recognised |
| [`format-not-supported`](#format-not-supported) | 422 | Unknown format, image/container format, or ZIP |
| [`format-not-writable`](#format-not-writable) | 422 | The target format can be read but not written |
| [`invalid-binary-input`](#invalid-binary-input) | 422 | The bytes are not a valid file of the binary format |
| [`encoding-error`](#encoding-error) | 422 | Reserved; not currently returned |
| [`unsafe-xml`](#unsafe-xml) | 422 | XML with a DTD or entity declarations |
| [`ambiguous-format`](#ambiguous-format) | 422 | Could be PAC or Unicode PAC (FPC) |
| [`ambiguous-encoding`](#ambiguous-encoding) | 422 | The character set could not be decided |
| [`input-limits-exceeded`](#input-limits-exceeded) | 422 | Too many cues or a line that is far too long |
| [`conversion-failed`](#conversion-failed) | 422 | An operation or the format writer failed |
| [`rate-limited`](#rate-limited) | 429 | Too many requests |
| [`queue-full`](#queue-full) | 503 | The converter is busy |
| [`conversion-engine-stuck`](#conversion-engine-stuck) | 503 | An earlier conversion is stuck; the API is recovering |
| [`browse-busy`](#browse-busy) | 503 | Too many listings and reads at once |
| [`conversion-timeout`](#conversion-timeout) | 504 | The conversion took too long |
| [`internal-error`](#internal-error) | 500 | A bug in the API |
| [`storage-location-rejected`](#storage-location-rejected) | 422 | The input/output location is not allowed |
| [`hidden-path`](#hidden-path) | 422 | The file or folder is hidden (its name starts with `.`) |
| [`extension-not-allowed`](#extension-not-allowed) | 422 | Not a subtitle file extension, or the wrong one for the target |
| [`folder-too-large`](#folder-too-large) | 422 | The folder has too many entries to list |
| [`storage-not-found`](#storage-not-found) | 404 | The storage file or folder does not exist |
| [`storage-io-failed`](#storage-io-failed) | 502 | Reading or writing storage failed |
| [`storage-list-disabled`](#storage-list-disabled) | 403 | Browsing and reading by location are off |
| [`storage-not-listable`](#storage-not-listable) | 422 | HTTPS locations can't be listed or read by location |
| [`storage-cursor-invalid`](#storage-cursor-invalid) | 400 | The listing cursor is malformed or for another folder |
| [`output-exists`](#output-exists) | 409 | The output file already exists |
| [`job-not-found`](#job-not-found) | 404 | Job record unknown or expired |
| [`job-not-ready`](#job-not-ready) | 409 | The job is still running |
| [`job-input-not-stored`](#job-input-not-stored) | 409 | The job's input was an upload, not a stored file |
| [`job-output-not-stored`](#job-output-not-stored) | 409 | The job has no stored output |
| [`job-file-gone`](#job-file-gone) | 404 | The stored file is no longer there |
| [`job-location-not-replayable`](#job-location-not-replayable) | 409 | A signed HTTPS location can't be re-read |

### Warnings

| Code | In short |
|---|---|
| [`characters-replaced`](#characters-replaced) | Some characters can't be stored in the target and were replaced or dropped |
| [`text-truncated`](#text-truncated) | The target cut lines short or dropped lines |
| [`lossy-format`](#lossy-format) | The target couldn't keep everything (cues, times or styling) |
| [`frame-quantised`](#frame-quantised) | Times were rounded to whole frames |
| [`frame-rate-changed`](#frame-rate-changed) | EBU STL stores a different frame rate than intended |
| [`frame-rate-assumed`](#frame-rate-assumed) | The source's frame rate was not known and a default was used |
| [`verification-failed`](#verification-failed) | The written binary file couldn't be checked |
| [`format-defaults-applied`](#format-defaults-applied) | Header fields were filled with defaults you didn't set |
| [`zero-length-cue`](#zero-length-cue) | Source cues with no duration |
| [`overlapping-cues`](#overlapping-cues) | Source cues that overlap in time |
| [`long-cue`](#long-cue) | Source cues longer than 30 seconds |
| [`programme-start-not-applied`](#programme-start-not-applied) | EBU STL programme start was not subtracted |
| [`source-replacement-chars`](#source-replacement-chars) | The source text was already damaged |
| [`encoding-guessed`](#encoding-guessed) | The text's character set was guessed |
| [`encoding-forced`](#encoding-forced) | The input encoding was forced by the request |
| [`language-applied`](#language-applied) | encoding.language selected the text encoding |
| [`encoding-ignored`](#encoding-ignored) | An encoding setting doesn't apply to binary files |
| [`format-option-ignored`](#format-option-ignored) | A format option block doesn't apply to this conversion |
| [`fce-rule-failed`](#fce-rule-failed) | A Fix Common Errors rule failed and was skipped |
| [`op-noop`](#op-noop) | Fix Common Errors changed nothing |
| [`sidecar-write-failed`](#sidecar-write-failed) | The subtitle was written, its JSON sidecar wasn't |
| [`sidecar-delete-failed`](#sidecar-delete-failed) | An old JSON sidecar could not be removed |
| [`sidecar-skipped`](#sidecar-skipped) | No sidecar for HTTPS outputs |

## How errors are returned

_For integrators._ Every error is `application/problem+json` (RFC 7807), the `ApiProblem` schema in the OpenAPI
document. Branch on `code`; codes are stable and never renamed (new ones may be added). `type` links to the code's
section on this page. `type`, `title`, `status`, `code`, `detail` and `hint` are always present, and `requestId`
is present on request-level errors (it matches the `X-Request-Id` header and the logs). `errors[]` holds JSON
pointers into the request with a message and sometimes a hint: profile fields (`/target/format`), query
parameters (`/query/textOnly`) and JSON body fields (`/contentEncoding`). `debug` (exception type, stack trace,
detected encoding, first-line shape, binary probe results) is included only when `ERRORS_VERBOSE=true` (the
default); it never contains file content.

Extension members:

- `phase`: on `conversion-timeout`, `queue` or `running`.
- `candidates`: on `ambiguous-format`, the format ids the bytes match; on `ambiguous-encoding`, the PAC code pages
  or text encodings that remain possible (can be empty).
- `probe`: on `ambiguous-encoding`, which decision failed: `pac-code-page` or `text-encoding`.
- `location`: on `job-file-gone` and `job-location-not-replayable`, the redacted stored location.
- `id`: the job id, when an admitted conversion fails (also in the `X-Job-Id` header).
- `retryAfterSeconds`: on `rate-limited` and `queue-full` (also the `Retry-After` header).

## Errors: request and profile

### <a id="invalid-request"></a>`invalid-request`

**What:** The request is incomplete or malformed, so the API didn't start a conversion.

**Why:** Something required is missing or two fields contradict each other: no file or `content`, no target,
both `preset` and `profile.extends`, both `input` and `content`, base64 that doesn't decode, an unknown
`contentEncoding` or `ifExists` value, an explicit `output` together with `target.additional`, a JSON field of the
wrong type, or a `Prefer: wait` header outside 0 to 60 seconds.

**What to do:** Read `detail` and `hint`; they name the field. Fix it and send the request again.

_For integrators:_ 400. `errors[0].pointer` names the field where possible (`/content`, `/contentEncoding`,
`/ifExists`, `/query/<name>`).

### <a id="invalid-profile"></a>`invalid-profile`

**What:** The profile (or preset override) contains a setting that is wrong or unknown.

**Why:** Profiles are checked against a schema before anything runs. Unknown keys are errors, so a typo never
silently does nothing. An unknown encoding name in `encoding.input` or `encoding.inputFallback` also lands here.

**What to do:** Look at `errors[]`: each entry points at the field and often suggests the right value ("Did you
mean 'webvtt'?"). `POST /v1/profiles/validate` checks a profile without converting.

_For integrators:_ 400. `errors[]` with `pointer`, `message`, `hint`. The JSON Schema is at `/v1/schemas/profile.json`.

### <a id="format-option-invalid"></a>`format-option-invalid`

**What:** A format option has a well-formed but unusable value.

**Why:** The value is valid JSON but not something the format can store: an EBU STL frame rate that conflicts with
`target.fps`, a `target.fps` that EBU STL can't store, an unknown code page or language, an EBU header text field
over 32 bytes, or a Cavena 890 title over 28 ASCII characters.

**What to do:** Use one of the values the hint lists. `GET /v1/formats` shows the options for each format.

_For integrators:_ 400. `errors[]` points at the option, e.g. `/formatOptions/ebuStl/frameRate`.

### <a id="empty-input"></a>`empty-input`

**What:** The subtitle file is empty (0 bytes).

**Why:** There is nothing to convert.

**What to do:** Check that the right file was sent and that the upload or storage copy completed.

_For integrators:_ 400.

### <a id="preset-not-found"></a>`preset-not-found`

**What:** No preset has the name you asked for.

**Why:** Preset names must match exactly. A mounted preset file that is invalid is not usable either.

**What to do:** Pick a name from the hint, or list presets with `GET /v1/profiles` (invalid mounted presets are
listed there with their errors).

_For integrators:_ 404.

## Errors: access and limits

### <a id="unauthorized"></a>`unauthorized`

**What:** The API key is missing or not accepted.

**Why:** The API requires a key for every `/v1` request by default. On first start it can generate one for you
(see [Securing the API](security.md#securing-the-api)).

**What to do:** Send `Authorization: Bearer <key>`. The key is in the API's key file (for example `api_keys.txt`
next to `docker-compose.yml`); the line that doesn't start with `#` is the key. For the MCP server, set
`SUBTITLE_API_KEY_FILE` (or `SUBTITLE_API_KEY`). Ask the operator for a key if you don't have one.

_For integrators:_ 401 with `WWW-Authenticate: Bearer`.

### <a id="host-not-allowed"></a>`host-not-allowed`

**What:** The API was called by a host name (the `Host` header) it doesn't accept.

**Why:** To stop web pages from reaching the API through a trick called DNS rebinding, the API only answers to
names it knows: `localhost`, `127.0.0.1`, `[::1]`, plus the names the operator added to `ALLOWED_HOSTS`.

**What to do:** Call the API by an allowed name. If you reach it by another name or IP (another container, a
server name, a reverse proxy), ask the operator to add that name to `ALLOWED_HOSTS` (comma separated) and restart
the API. The error names the host it saw.

_For integrators:_ 400. Health probes (`/healthz`, `/readyz`, `/metrics`) are exempt. The startup log lists the
accepted hosts (`security.hosts`).

### <a id="origin-not-allowed"></a>`origin-not-allowed`

**What:** A web page from another site tried to call the API, and the API refused.

**Why:** Browsers send an `Origin` header that says which site the page came from. The API only accepts requests
whose origin host and port match the `Host` they were sent to (and that host is allowed), so a random web page you
visit — or one on another port — can't read or write your files.

**What to do:** Call the API from a program (curl, a script, the MCP server). Those don't send `Origin` and are
not affected. A page served from the same host and port the API was called on works too.

_For integrators:_ 403. Requests without an `Origin` header are never affected. The API sends no CORS headers, so a
web page on another site can't call it: a browser front end should not hold an API key. Put a backend in between.

### <a id="not-found"></a>`not-found`

**What:** There is no endpoint at this address.

**Why:** The URL or method is wrong, or the client expects a different API version.

**What to do:** Check the base URL and path against `/docs` or the OpenAPI document.

_For integrators:_ 404. Unknown job ids are `job-not-found`, not this code.

### <a id="payload-too-large"></a>`payload-too-large`

**What:** The file, the request or the profile is larger than this server accepts.

**Why:** Size limits protect the single conversion worker. They cover the request body, the decoded subtitle
(also when read from storage) and the profile.

**What to do:** Split the subtitle, or send a smaller profile (put long replace lists in a mounted preset). An
operator can raise `MAX_UPLOAD_BYTES` or `PROFILE_MAX_BYTES`.

_For integrators:_ 413. Current limits: `GET /v1/limits` (`maxUploadBytes`, `maxRequestBodyBytes`, `maxProfileBytes`).

### <a id="unsupported-media-type"></a>`unsupported-media-type`

**What:** The request body has the wrong content type.

**Why:** Each endpoint expects one kind of body.

**What to do:** Use `multipart/form-data` for `/v1/convert` and `application/json` for `/v1/convert/json`.

_For integrators:_ 415.

### <a id="input-limits-exceeded"></a>`input-limits-exceeded`

**What:** The subtitle has more cues than allowed, or a line that is extremely long.

**Why:** Very long lines usually mean the file isn't really a text subtitle (a binary or minified file read as
text). The cue limit keeps one job from occupying the worker for too long.

**What to do:** For a binary file, set `source.format` to its format. Otherwise split the file. An operator can
raise `MAX_CUES` or `MAX_LINE_CHARS`.

_For integrators:_ 422. Limits in `GET /v1/limits` (`maxCues`, `maxLineChars`).

### <a id="rate-limited"></a>`rate-limited`

**What:** Too many requests in a short time.

**Why:** The server limits requests per minute per API key (or per client address without keys).

**What to do:** Wait for the `Retry-After` interval, then retry.

_For integrators:_ 429, `Retry-After` and `retryAfterSeconds`. Limit: `RATE_LIMIT_PER_MINUTE`.

### <a id="queue-full"></a>`queue-full`

**What:** The converter is busy and can't accept more work right now.

**Why:** Conversions run one at a time from a bounded queue, and the number of active jobs (including storage
transfers) is capped.

**What to do:** Retry after `Retry-After`. If it happens often, ask the operator to add replicas.

_For integrators:_ 503, `Retry-After`. Safe to retry: the request was not admitted.

### <a id="conversion-timeout"></a>`conversion-timeout`

**What:** The conversion did not finish within the time limit.

**Why:** Either it waited in the queue too long (`phase: "queue"`) or the conversion itself was too slow
(`phase: "running"`), for example a huge file or heavy operations.

**What to do:** For `queue`, retry later like `queue-full`. For `running`, split the file or remove expensive
operations; retrying the same request will usually time out again.

_For integrators:_ 504 with `phase`. Budget: `CONVERSION_TIMEOUT_SECONDS`.

### <a id="conversion-engine-stuck"></a>`conversion-engine-stuck`

**What:** An earlier conversion ran past its time limit and is still running, so the API is not starting new
conversions for now.

**Why:** The converter keeps its settings in one shared place, so running a second conversion beside a stuck one
could mix up their settings. The API waits until the stuck one ends. If it never ends, the API restarts itself
after a grace period (`CONVERSION_STUCK_GRACE_SECONDS`, by default twice the time limit).

**What to do:** Retry after `Retry-After`. If it keeps happening with the same file, that file is probably the
cause; keep a copy and send it, with the API's `conversion.stuck` log lines, to the operator.

_For integrators:_ 503 with `Retry-After`; `/readyz` reports not ready meanwhile. The container needs a restart
policy (`restart: unless-stopped`) so the automatic restart brings it back.

### <a id="browse-busy"></a>`browse-busy`

**What:** The API is handling too many folder listings and file reads at the same time.

**Why:** Listings, reads by location and job file reads share a small number of slots (4 by default) and a
bounded waiting line, so a burst of browsing can't slow down the whole server. Conversions use their own queue.

**What to do:** Retry after `Retry-After`. If it happens often, the operator can raise `BROWSE_CONCURRENCY` or
`BROWSE_QUEUE_CAPACITY`.

_For integrators:_ 503 with `Retry-After`. Current values: `GET /v1/limits` (`storage.browse`).

### <a id="internal-error"></a>`internal-error`

**What:** Something unexpected went wrong inside the API.

**Why:** This is a bug, not a problem with your file or request.

**What to do:** Report it with the `requestId` (and, if you can share it, the file).

_For integrators:_ 500.

## Errors: the input file and its format

### <a id="format-not-detected"></a>`format-not-detected`

**What:** The API could not recognise the subtitle format.

**Why:** None of the readers accepted the content. The hint describes what the file looks like (text or binary,
and the shape of its first lines).

**What to do:** Set `source.format` to the file's format (`GET /v1/formats` lists the ids), or check that this
is really a subtitle file. `/v1/detect` shows what the API sees.

_For integrators:_ 422.

### <a id="format-not-supported"></a>`format-not-supported`

**What:** The format can't be used here.

**Why:** The format id is unknown, the format is image-based or a container (Blu-ray sup, VobSub, Matroska, MP4,
...), it needs companion files next to the input, or a ZIP archive was sent. This API converts one text or binary
subtitle per request.

**What to do:** Check the id (the hint suggests a close match), extract subtitles from containers first, and send
archives one file at a time.

_For integrators:_ 422. `errors[]` points at `/source/format` or `/target/format` when a profile field is the cause.

### <a id="format-not-writable"></a>`format-not-writable`

**What:** The target format can be read but not written by this API.

**Why:** Some formats have no reliable writer (for example Avid STL or Ayato).

**What to do:** Choose another target; `GET /v1/formats?writable=true` lists the writable ones.

_For integrators:_ 422, pointer `/target/format`.

### <a id="invalid-binary-input"></a>`invalid-binary-input`

**What:** The file doesn't parse as the binary format it is supposed to be.

**Why:** `source.format` (or the file name) names a binary format, but the bytes are truncated, belong to another
format, or are text. For PAC, this also covers bytes that don't decode as readable text in any PAC code page.

**What to do:** Check the file and the format you named. If you're unsure, remove `source.format` and let detection
decide, or run `/v1/detect`. Binary content in JSON must be sent as base64 with `contentEncoding: "base64"`.

_For integrators:_ 422. `debug` has `format`, `byteLength`, `isMine`.

### <a id="encoding-error"></a>`encoding-error`

**What:** Reserved for input that can't be decoded at all.

**Why:** Text that isn't UTF-8 is probed or decoded with a fallback instead (see
[`ambiguous-encoding`](#ambiguous-encoding) and [`encoding-guessed`](#encoding-guessed)), so this code is not
currently returned.

**What to do:** If you ever see it, set `encoding.input` to the file's encoding.

_For integrators:_ 422. Kept in the code list so clients can handle it.

### <a id="unsafe-xml"></a>`unsafe-xml`

**What:** The XML input contains a DTD or entity declarations.

**Why:** These can be used to read server files or exhaust memory, so they are refused for any input that looks
like XML, even if the format wouldn't need them.

**What to do:** Remove the `<!DOCTYPE ...>` block and send the file again.

_For integrators:_ 422.

### <a id="ambiguous-format"></a>`ambiguous-format`

**What:** The file could be PAC or Unicode PAC (FPC), and the API won't guess which.

**Why:** The two formats share their header. Only the `.pac` or `.fpc` file name tells them apart; without one
(no name, `.bin`, inline content) reading it as the wrong format would garble the text.

**What to do:** Set `source.format` to `pac` or `fpc`, or send the file under its real extension (for
`/v1/detect`: `fileName`).

_For integrators:_ 422. `candidates`: `["pacscreenelectronics", "pacunicodeunipac"]`; `errors[0].pointer`:
`/source/format`.

### <a id="ambiguous-encoding"></a>`ambiguous-encoding`

**What:** The API could not tell which character set (code page or text encoding) the file uses, and won't
guess.

**Why:** The same bytes mean different letters in different character sets. The API decodes the file with each
candidate and keeps a candidate only when the result contains common words of a language that character set
serves. A distinctive-script reading (Arabic, Hebrew, Thai, Greek, Cyrillic, CJK) with dictionary hits drops
Latin code-page rivals (windows-1250/1252/1254/1257 and their ISO siblings) first — those rivals can look like
Czech/Slovak words from mojibake. When exactly one reading remains it is used. This error means either no
reading produced recognisable words, or several genuine readings remain and they give different text. The
`probe` field says which check failed:

- **`probe: "pac-code-page"`, a PAC file.** All 13 PAC code pages are tried when
  `formatOptions.pac.inputCodePage` is unset or `auto`. Very short PAC files without a single common word often
  end up here.
- **`probe: "text-encoding"`, a text file** that has no BOM and isn't valid UTF-8, and still reads as words in
  more than one encoding after the script-priority filter (a genuine clash).


**What to do:**

- PAC: set `formatOptions.pac.inputCodePage` to the code page the file was written with (for example `greek`),
  picked from `candidates` or from the file's language.
- Text: set `encoding.input` to one of the candidate encodings (for `/v1/detect`: `encoding`), or set
  `encoding.language` to the file's language (ISO code such as `ar`, `he`, `th`) so the probe keeps only
  encodings that serve it, or save the file as UTF-8. Agents: show the per-candidate `sample` values, pick
  **only** from the candidate list, tell the user what you chose, then retry.

_For integrators:_ 422. `probe`; `candidates` (PAC: code page name strings, may be empty; text: objects
`{encoding, language, hits, sample}` with a short decode sample that is always present, not verbose-only);
`errors` pointers `/formatOptions/pac/inputCodePage` or `/encoding/input` and `/encoding/language`;
`debug.scores` (verbose only) has the word counts per candidate. How the probes work: [formats.md](formats.md#detection).

### <a id="conversion-failed"></a>`conversion-failed`

**What:** An operation or the target format's writer failed on this file.

**Why:** Something in the content tripped a step: an operation (`debug.operation` names it), a `MultipleReplace`
regular expression that took too long, or the writer itself.

**What to do:** Remove or change the named operation, or try another target to compare. If the input is valid,
report it with the `requestId`.

_For integrators:_ 422. `detail` names the operation or writer.

## Errors: storage locations

These apply when you use `input` or `output` locations (filesystem, S3-compatible or HTTPS) instead of uploading
and downloading. Setup: [usage.md](usage.md#filesystem-locations-and-sidecars).

### <a id="storage-location-rejected"></a>`storage-location-rejected`

**What:** The API is not allowed to use that location.

**Why:** Storage access is limited to what the operator allowlisted, and some paths are refused for safety: a
scheme that isn't enabled, a path outside the allowed roots, buckets, prefixes or hosts, a directory instead of a
file, dot segments, symlinks, control characters in the decoded location (an encoded NUL, `%00`), an HTTPS
redirect, or an HTTPS host that resolves to a private address. Hidden files and folders inside an allowed
location have their own code, [`hidden-path`](#hidden-path).

**What to do:** Name a complete file inside an allowed location. If the location is right, ask the operator to
add it to the allowlist (`STORAGE_FILE_ROOTS`, `S3_ALLOWED_BUCKETS`, `S3_ALLOWED_PREFIXES`, `HTTP_ALLOWED_HOSTS`).

_For integrators:_ 422. `detail` says which rule applied. Enabled schemes: `GET /v1/limits` (`storage`).

### <a id="hidden-path"></a>`hidden-path`

**What:** The file or folder is inside an allowed location, but it is hidden: its name, or the name of a folder
above it, starts with `.` (for example `.env`, `.git/config` or `.cache/show.srt`).

**Why:** Hidden files often hold settings and secrets, so the API never reads, lists or writes them, also not as
an output or sidecar. Names with dots inside them (`show.en.srt`) are fine.

**What to do:** Use a file or folder whose name and parent folders don't start with `.`. A listing `prefix` can't
start with `.` either. HTTPS URLs are not affected by this rule.

_For integrators:_ 422. Checked after the allowlist: a path outside the allowed locations is still
`storage-location-rejected`.

### <a id="extension-not-allowed"></a>`extension-not-allowed`

**What:** The file name doesn't end in a subtitle extension, or, for an output, not in an extension of the
target format.

**Why:** In file and S3 locations the API only lists, reads and writes subtitle files, so other files in the same
folders (configuration, documents) can't be read through it. An output must be named for its format (a WebVTT
output ends in `.vtt`), so files are never written under a misleading name.

**What to do:** Use the file's real subtitle extension (`.srt`, `.vtt`, `.stl`, `.pac`, ...). For an output,
give it the target format's extension or pick the target that matches the name. The full list is in
`GET /v1/limits` under `storage.extensions`. HTTPS URLs and the `{output}.json` sidecar are not affected.

_For integrators:_ 422 with `extension` (and `allowed` for outputs). Also returned by
`GET /v1/jobs/{id}/input|output` when the stored location has no subtitle extension, for example a failed job.

### <a id="folder-too-large"></a>`folder-too-large`

**What:** The folder has too many entries to list.

**Why:** Listing reads the whole folder on every page, so very large folders are refused to keep the API
responsive: at most 5,000 entries that match your prefix (and 100,000 names in total).

**What to do:** Add a `prefix` (the start of the names you want) to list fewer entries, or organise the files into
subfolders.

_For integrators:_ 422 with `limit`. Filesystem folders only; S3 listings are paged by S3 itself.

### <a id="storage-not-found"></a>`storage-not-found`

**What:** The input file or its folder doesn't exist, or an output's allowed root folder doesn't exist.

**Why:** The API can only read files that are there. For filesystem outputs it creates missing sub-folders below an
allowed root (`STORAGE_FILE_ROOTS`), but never the root itself.

**What to do:** Check the path, bucket and key, check that the allowed root folder exists and is mounted, and check
that a signed HTTPS URL hasn't expired.

_For integrators:_ 404.

### <a id="storage-io-failed"></a>`storage-io-failed`

**What:** Reading from or writing to storage failed.

**Why:** The storage was unreachable or refused the transfer: permissions, a full disk, a missing mount, an
HTTPS 403, a TLS problem, or timeouts that used up the retries.

**What to do:** Check permissions, space and availability. For an output, look at the destination before you
retry: a failed or timed-out write may still have arrived.

_For integrators:_ 502. `detail` has the remote status and attempt count, never a signed query. Timeouts, resets and
transient statuses are retried (`IO_RETRIES`); policy rejections and 403/404 are not.

### <a id="storage-list-disabled"></a>`storage-list-disabled`

**What:** You asked to list storage or read a file by its location, and this API has that turned off.

**Why:** Browsing is off by default (`STORAGE_LIST_ENABLED=false`), so the API shows nothing about its allowed
folders and buckets.

**What to do:** Ask the operator to set `STORAGE_LIST_ENABLED=true` and restart the API. Reading a job's stored
files (`GET /v1/jobs/{id}/input` and `/output`) works either way.

_For integrators:_ 403 on every `/v1/storage/*` route while off, checked on each request. `GET /v1/limits` reports
`storage.list: false`.

### <a id="storage-not-listable"></a>`storage-not-listable`

**What:** The location is an HTTPS URL, which can't be listed or read by location.

**Why:** HTTPS has no folders to list; the API only reads HTTPS URLs as convert input and writes them as output.

**What to do:** Browse a file or S3 location from `GET /v1/storage/locations`, or use the HTTPS URL as `input` of a
conversion.

_For integrators:_ 422 from `GET /v1/storage/list` and `/v1/storage/file`. Locations report HTTPS hosts with
`listable: false` and `readable: false`.

### <a id="storage-cursor-invalid"></a>`storage-cursor-invalid`

**What:** The listing `cursor` wasn't returned by this API for this folder and prefix.

**Why:** The cursor was changed, cut short, or came from a listing of another folder or prefix.

**What to do:** Pass `next` from the previous page unchanged, with the same `location` and `prefix`, or start again
without a cursor.

_For integrators:_ 400. Cursors are stateless and opaque; they don't expire, but a folder that changed between pages
is not a snapshot (see [browsing storage](usage.md#browsing-storage-and-reading-by-location)).

### <a id="output-exists"></a>`output-exists`

**What:** The output file already exists, so nothing was overwritten.

**Why:** Filesystem and S3 outputs never overwrite by default. The existing file may come from an earlier job, or
from an earlier attempt of this same request.

**What to do:** Check the existing file first. Then choose a different output name, or send `ifExists: "replace"`
to replace it on purpose.

_For integrators:_ 409. `ifExists` accepts only `error` (default) and `replace`. HTTPS outputs follow the signer's
policy instead.

## <a id="job-file-errors"></a>Job file errors

Every conversion gets a job id. `GET /v1/jobs/{id}/input` and `/output` re-read the stored files of a job. Job
history is kept in memory only, holds metadata (never file bytes), expires after a while and is cleared when the
API restarts. Details: [usage.md](usage.md#re-reading-job-files-and-overwrite-policy).

### <a id="job-not-found"></a>`job-not-found`

**What:** The API has no record of this job.

**Why:** The id is wrong, or the record expired, was evicted to make room, or the API restarted. It does **not**
mean the conversion failed.

**What to do:** Check the output location itself before resubmitting anything that writes.

_For integrators:_ 404.

### <a id="job-not-ready"></a>`job-not-ready`

**What:** The job is still running, so its output isn't there yet.

**Why:** Output files can only be read after the conversion succeeds.

**What to do:** Poll `GET /v1/jobs/{id}` and try again once its status is `succeeded`. The stored input may already
be readable.

_For integrators:_ 409.

### <a id="job-input-not-stored"></a>`job-input-not-stored`

**What:** This job's input was uploaded or sent inline, so there is no stored file to re-read.

**Why:** The API doesn't keep uploaded bytes.

**What to do:** Use your original copy of the file, or convert from an `input` location if you need to re-read
the source through the job later.

_For integrators:_ 409.

### <a id="job-output-not-stored"></a>`job-output-not-stored`

**What:** This job has no stored output to re-read.

**Why:** Usually the conversion had no `output` location: the converted file was returned in the convert response
itself (inline) and was never stored. The API doesn't keep response bytes. Less often, the conversion failed, so
nothing was written. The `detail` says which.

**What to do:** Use the result from the original response; an MCP client receives it as the file attached to the
convert result. To read a result again later, convert with an `output` location (file, S3 or HTTPS) and read it
back from there. For a failed job, look at its error with `GET /v1/jobs/{id}` before retrying.

_For integrators:_ 409.

### <a id="job-file-gone"></a>`job-file-gone`

**What:** The file is no longer at the location the job recorded.

**Why:** It was moved or deleted after the job. Job history points at files; it isn't an archive of them.

**What to do:** Check the storage directly. Don't repeat the write just because of this error.

_For integrators:_ 404 with the redacted `location`.

### <a id="job-location-not-replayable"></a>`job-location-not-replayable`

**What:** The job's HTTPS location can't be re-read.

**Why:** Signed URL queries are removed before the job is recorded, so the signature is gone.

**What to do:** Use a fresh signed URL as `input` on a new request, or read the storage directly.

_For integrators:_ 409 with the redacted `location`.

## <a id="warning-codes"></a>Warnings

Warnings mean the conversion succeeded. Read them before you deliver the file: they name what was lost, defaulted
or suspicious, and how to change it. Settings are never switched automatically.

_For integrators:_ warnings appear in `warnings[]` of the JSON response, in sidecars, in retained job metadata and in
the `X-Warnings` / `X-Warnings-Count` headers. Each warning has `code` and `message`, and may have `count`, `hint`
and `details`. `X-Warnings` carries only `code`, `message` and `count`; `hint` and `details` are in the JSON body
and the sidecar. `details` never contains subtitle text, only positions, code points and settings:

| `details` field | Shape | Used by |
|---|---|---|
| `cueCount`, `cues` | number; 1-based cue numbers (first 20) | most cue warnings |
| `distinctCodePoints`, `codePoints` | number; `[{codePoint: "U+039A", count}]` (first 20) | `characters-replaced` |
| `remedy` | object, see [Remedy](#remedy-characters-replaced) | `characters-replaced` |
| `durations` | `[{cue, durationMs}]` (first 20) | `long-cue` |
| `fields` | `{field: value written}` | `format-defaults-applied` |
| `timecode` | `"HH:MM:SS:FF"` | `programme-start-not-applied` |
| `frameRate` | number | `frame-rate-assumed` |

Cue numbers in source warnings (`zero-length-cue`, `overlapping-cues`, `long-cue`, `source-replacement-chars`) are
positions in the source. In read-back warnings (`characters-replaced`, `text-truncated`, `lossy-format`) they are
positions in the written output, after operations.

## Warnings: what the output could not keep

### <a id="characters-replaced"></a>`characters-replaced`

**What:** Some characters could not be stored in the target's character set and were replaced or dropped.

**Why:** Binary broadcast formats store text in a fixed table (a PAC code page, an EBU STL character table, a
Cavena language). Letters outside that table (Greek in a Latin PAC, for example) can't be written. The API finds
this by reading the written file back.

**What to do:** Change the setting the hint names, for example `formatOptions.pac.codePage: "greek"`, or use a
Unicode target such as `fpc`. The `remedy` lists the values that keep every character; for PAC each one was
actually tested.

_For integrators:_ `count` = characters; `details` = cues, code points and `remedy` (see
[Remedy](#remedy-characters-replaced)). Replacement characters (U+FFFD) already in the source are reported as
`source-replacement-chars` instead.

### <a id="text-truncated"></a>`text-truncated`

**What:** The writer cut some cues short: the end of a long line, or whole extra lines, are missing.

**Why:** Some formats limit line length and line count (Cavena 890 and Cheetah CAP Old, for example).

**What to do:** Split or balance lines before writing (operations `SplitLongLines` / `BalanceLines` with
`rules.maxLineLength` and `rules.maxLines`), or pick another target.

_For integrators:_ `count` = characters lost; `details.cues`.

### <a id="lossy-format"></a>`lossy-format`

**What:** The target couldn't keep everything. The message says what: cues missing when the file is read back,
zero-length cues stored at the wrong time, times moved by more than one frame, or a format that is lossy by
design (Cheetah CAP loses styling).

**Why:** Each format has limits. EBU STL, for example, can't store a cue with no duration; it reads back as
99:59:59,999.

**What to do:** Follow the hint. For zero-length cues: remove them (operations `DeleteFirst` / `DeleteContains`)
or give them a duration (operation `ApplyDurationLimits` with `rules.minDurationMs`). For moved times: check those
cues in the source (overlaps, times past the format's maximum). Or compare with another target.

_For integrators:_ `count` and `details.cues` where cues are involved; also reflected in
`verification.verdict: "lossy"`.

### <a id="frame-quantised"></a>`frame-quantised`

**What:** Times were rounded to whole frames of the target's frame rate, by less than one frame each.

**Why:** Frame-based formats store times as frames, not milliseconds.

**What to do:** Usually nothing; this is expected. If the frame rate is wrong, set `target.fps` (or the format's
own frame-rate option).

_For integrators:_ no `count`; the message gives the largest shift and the rate. `verification.verdict` is
`quantised` when this is the only change.

### <a id="frame-rate-changed"></a>`frame-rate-changed`

**What:** The EBU STL file records a different frame rate than the one intended (for example 30 instead of 29.97).

**Why:** The EBU STL standard only defines codes for 25 and 30 fps, so 29.97 is written as `STL30.01`. The intended
rate is `formatOptions.ebuStl.frameRate`, else `target.fps`, else the source's rate. Timings shift by less than
one frame, without cumulative drift.

**What to do:** Usually accept it, and tell the receiving system the real rate. To change what is written, set
`formatOptions.ebuStl.frameRate`, or `headerFromSource: false` when converting EBU to EBU.

_For integrators:_ EBU STL only; no `count`. The message names the code and both rates.

### <a id="verification-failed"></a>`verification-failed`

**What:** The binary file was written, but it couldn't be read back to check it.

**Why:** The format's reader failed on the file the writer produced.

**What to do:** Open the file in the target software before delivering it. If the input was valid, report it with
the `requestId`.

_For integrators:_ `verification.verdict: "unverified"` with a `reason`.

## Warnings: settings and defaults

### <a id="format-defaults-applied"></a>`format-defaults-applied`

**What:** The output's header contains programme details you didn't provide, such as the title `No Title`, the
country `USA`, a language, or (Cavena 890) a title taken from the file name.

**Why:** EBU STL and Cavena 890 headers have identity fields that are always filled. When the request leaves them
unset, Subtitle Edit's defaults, a detected language or the file name are used. A MAM or playout system may read
these as real programme metadata.

**What to do:** Set the real values in `formatOptions.ebuStl` (`originalProgrammeTitle`, `countryOfOrigin`,
`language`, ...) or `formatOptions.cavena890` (`title`, `language`). The hint lists the fields.

_For integrators:_ `count` = number of fields; `details.fields` maps each field to the value actually written (read
back from the output). Covered: a freshly built EBU STL header's title fields, translator, publisher, country, and
`language` when unset (with `language: "auto"`, only when detection fell back); Cavena 890 `title` and `language`.
Not raised for header fields copied from an EBU STL source (`headerFromSource`).

### <a id="frame-rate-assumed"></a>`frame-rate-assumed`

**What:** The source stores times as frames but doesn't say its frame rate, so a default of 23.976 fps was used to
read it.

**Why:** Formats such as PAC, Cavena 890, Cheetah CAP, CapMaker Plus, MicroDVD, and SCC without telling
timecodes have no frame-rate field. If the assumption is wrong, every time in the output is off.

**What to do:** Set `source.fps` to the file's real frame rate (25 for PAL, 29.97 for NTSC, ...).

_For integrators:_ `details.frameRate`. Not raised when the output is the same kind of frame-based file without
`target.fps` (frame numbers carry over unchanged). `detected.formatDetails.frameRateSource` is `assumed`. When
`target.fps` was set, it was applied as the output rate only, without rescaling.

### <a id="encoding-guessed"></a>`encoding-guessed`

**What:** The text file isn't UTF-8, and its character set had to be guessed.

**Why:** None of the probed encodings turned the text into recognisable words (common with very short or
unusual files), so Subtitle Edit's heuristic picked one. The API does this instead of failing, because most
such files are plain Western text.

**What to do:** Check the converted text. If letters look wrong, set `encoding.input` to the file's encoding, or
save the source as UTF-8.

_For integrators:_ `detected.encodingConfidence` is `heuristic`.

### <a id="encoding-forced"></a>`encoding-forced`

**What:** The request forced the input text encoding.

**Why:** `encoding.input` (or `encoding` on `/v1/detect`) was set, so auto-detection was skipped. A wrong page
still returns 200 with mojibake — forcing always wins.

**What to do:** Confirm the converted text. If letters look wrong, set `encoding.input` to the correct page (or
`encoding.language` on a retry without `encoding.input`).

_For integrators:_ `detected.encodingConfidence` is `forced`.

### <a id="language-applied"></a>`language-applied`

**What:** `encoding.language` selected which text encoding to use.

**Why:** The file wasn't UTF-8; the language filter kept only encodings that serve that language and the probe
then uniquely chose one.

**What to do:** Confirm the converted text. If it is wrong, set `encoding.input` to the exact encoding instead.

_For integrators:_ `detected.encodingConfidence` is `language`.

### <a id="encoding-ignored"></a>`encoding-ignored`

**What:** An encoding setting was ignored because it doesn't apply to a binary file.

**Why:** Binary formats choose their character set through format options, not `encoding.input` or
`encoding.output`. Binary outputs in JSON are always base64, whatever `responseEncoding` says. And a binary source
has no text encoding for `encoding.output: "source"` to reuse, so UTF-8 is written.

**What to do:** Remove the setting, or use the format's own option (`formatOptions.pac.codePage`,
`formatOptions.ebuStl.characterTable`, ...).

_For integrators:_ no `count`.

### <a id="format-option-ignored"></a>`format-option-ignored`

**What:** A `formatOptions` block was ignored.

**Why:** It belongs to a format that is neither the source nor the target of this conversion (for example
`ebuStl` options when writing WebVTT), or, for ASS, only one of `playResX` / `playResY` was set.

**What to do:** Remove the block, or check that the target is what you meant.

_For integrators:_ no `count`.

## Warnings: what the source looks like

These describe the source file. The API reports them and leaves the times and text as they are.

### <a id="zero-length-cue"></a>`zero-length-cue`

**What:** Some cues in the source have no duration (end time equal to or before the start).

**Why:** These are often metadata or placeholder cues. Some targets can't store them: EBU STL reads them back at
99:59:59,999 (also reported as `lossy-format`) and CapMaker Plus drops them.

**What to do:** Check them. Remove them (operations `DeleteFirst` / `DeleteContains`) or give them a duration
(operation `ApplyDurationLimits` with `rules.minDurationMs`).

_For integrators:_ `count`, `details.cues`.

### <a id="overlapping-cues"></a>`overlapping-cues`

**What:** Some cues start before an earlier cue has ended.

**Why:** Overlaps are often timing mistakes, but can be intended (two speakers). Formats and players that show one
cue at a time may hide or cut one of them.

**What to do:** Check the listed cues. Fix the times at the source, or run operation `FixCommonErrors` with the
rule `FixOverlappingDisplayTimes` if they are errors. Leave them if they are intended and the target supports it.

_For integrators:_ `count`, `details.cues` (the later cue of each overlap). Zero-length cues are not counted here.

### <a id="long-cue"></a>`long-cue`

**What:** Some cues stay on screen for more than 30 seconds.

**Why:** That usually means a broken cue: a wrong end time, or several cues merged into one.

**What to do:** Check those cues in the source and correct the end times.

_For integrators:_ `count`, `details.cues`, `details.durations` (`[{cue, durationMs}]`).

### <a id="programme-start-not-applied"></a>`programme-start-not-applied`

**What:** The EBU STL file says the programme starts at a timecode other than zero (often `10:00:00:00`), and the
cue times were kept as stored.

**Why:** Broadcast files often count from the programme's start timecode. The API doesn't subtract it on its own,
because for timecode-based targets keeping it is usually right.

**What to do:** If the output should start at 00:00:00 (for example a WebVTT for the web), subtract the start with
`timing.offsetMs`; the hint gives the exact value (`-36000000` for `10:00:00:00`). Otherwise keep it.

_For integrators:_ `details.timecode` (`HH:MM:SS:FF`); no `count`.

### <a id="source-replacement-chars"></a>`source-replacement-chars`

**What:** The source already contains replacement characters (�): some text was damaged before this conversion.

**Why:** Usually an earlier tool decoded the file with the wrong character set and saved the broken result.

**What to do:** Re-export the source from the original with the right encoding. If this file isn't UTF-8, set
`encoding.input`.

_For integrators:_ `count` = characters; `details.cues`.

## Warnings: operations

### <a id="fce-rule-failed"></a>`fce-rule-failed`

**What:** One Fix Common Errors rule failed on this file and was skipped; the other rules and the conversion went
ahead.

**Why:** A rule hit content it couldn't handle.

**What to do:** Check the output where that rule would have applied, or leave the rule out of `rules`. Report it
if it happens on ordinary files.

_For integrators:_ the message names the rule and the exception type.

### <a id="op-noop"></a>`op-noop`

**What:** The `FixCommonErrors` operation ran but changed nothing.

**Why:** The selected rules found nothing to fix (or none applied to the file's language).

**What to do:** Nothing, unless you expected changes; then check `rules` and `language`.

_For integrators:_ informational.

## Warnings: sidecars

A sidecar is the `{output}.json` file written next to a storage output when you send `sidecar: true`.

### <a id="sidecar-write-failed"></a>`sidecar-write-failed`

**What:** The subtitle was written, but its JSON sidecar wasn't.

**Why:** The sidecar is a separate write: permissions, limits or an existing file can block it while the subtitle
succeeds. There is no rollback of the subtitle.

**What to do:** Use the subtitle; read the warnings from this response instead of the missing sidecar. Fix the
cause (often an existing `{output}.json` without `ifExists: "replace"`) before the next run.

_For integrators:_ the subtitle write is confirmed; only the sidecar failed.

### <a id="sidecar-delete-failed"></a>`sidecar-delete-failed`

**What:** The subtitle was written without a sidecar, but an older `{output}.json` next to it could not be
removed.

**Why:** A write without `sidecar` removes any earlier sidecar (filesystem and S3), because it describes a file
that is no longer there. This time the delete failed, for example because the name is a folder or permissions
block it.

**What to do:** The subtitle is fine. Delete or ignore the old sidecar, or write with `sidecar: true` so it is
replaced with an up-to-date one.

_For integrators:_ HTTPS outputs never delete anything.

### <a id="sidecar-skipped"></a>`sidecar-skipped`

**What:** No sidecar was written for an HTTPS output.

**Why:** A signed HTTPS URL names exactly one object; the API can't derive a second signed URL for the sidecar.

**What to do:** Read the warnings and verification from the response, or use a filesystem or S3 output if you need
a sidecar.

_For integrators:_ the subtitle PUT itself is unaffected.

## Verification (binary outputs)

Every binary output is read back with the same libse reader and compared with the cues that were meant
to be written. The result is `outputs[].verification` in `POST /v1/convert/json`, the
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

`warnings[].details.remedy` (JSON body and requested sidecar only, never `X-Warnings`) gives the caller the facts to
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
## Job and transfer failures

A retained failed job is HTTP 200 with `status: failed` and a problem in `error`; it is not a successful
conversion. Unknown/expired/evicted/restarted jobs return 404 `job-not-found`, meaning gone, not failed.
An admitted conversion that fails synchronously returns its usual problem status plus `X-Job-Id` and `id`;
validation/admission failures have no job. Error `requestId` retains the original POST correlation.
Malformed/duplicate `Prefer: wait` or values outside 0..60 return 400 `invalid-request`.

`storage-io-failed` includes concrete remote status/attempt information without signed URL queries.
Timeouts/reset/transient HTTP statuses may be retried; policy rejections and 403/404 are not.
A failed PUT response does not prove the receiver did not accept the object. Do not blindly replay the
conversion POST after losing a response. Poll a known job id; there is no idempotency-key deduplication.
