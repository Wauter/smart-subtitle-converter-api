# Changelog

All notable API changes. Versions follow [semver](https://semver.org/); the version is the
`<Version>` in `src/SmartSubtitleConverterApi/SmartSubtitleConverterApi.csproj` and is what
`GET /healthz` (`version`) and the OpenAPI `info.version` report. See [CONTRIBUTING.md](CONTRIBUTING.md)
for the bump rule. Tagged releases publish multi-arch images to GHCR
(`ghcr.io/wauter/smart-subtitle-converter-api`); every green push to `main` also publishes `:edge`.

## Unreleased

- Fixed: filesystem storage failed on arm64 Linux (for example Docker on Apple silicon) with `502 storage-io-failed`
  "Invalid argument" for every listing, read and write. The folder-open and no-follow flags now use the right values
  for the CPU architecture, so symlinks are refused on arm64 too. Supported: `linux/amd64` and `linux/arm64`; any
  other architecture with `STORAGE_FILE_ROOTS` set stops startup with an `Invalid configuration` message. The Docker
  smoke test on both architectures now lists, reads and writes filesystem storage and checks symlink refusal.
- Filesystem outputs and sidecars create missing sub-folders below an allowed root, one level at a time on the opened
  parent folder (mode 0755, narrowed by the umask), never through a symlink, never a hidden (`.`) folder and never
  the root itself. Reads of a missing file or folder stay `404 storage-not-found`.
- New, off by default: browse storage and read files by location with `STORAGE_LIST_ENABLED=true`.
  `GET /v1/storage/locations` lists the allowed file roots, S3 buckets and prefixes (HTTPS hosts as not listable);
  `GET /v1/storage/list?location=&prefix=&cursor=` lists one folder, up to 200 items a page in name order, with a
  stateless `next` cursor; `GET /v1/storage/file?location=` returns one file's current bytes. Same allowlists and
  checks as conversions. Off, the routes answer `403 storage-list-disabled`. See
  [browsing storage](docs/usage.md#browsing-storage-and-reading-by-location).
- New error codes `storage-list-disabled` (403), `storage-not-listable` (422) and `storage-cursor-invalid` (400).
- `GET /v1/limits` adds `storage.list` and `storage.listSchemes`; the startup log says whether listing is on.
- Listing S3 uses `ListObjectsV2` and needs `s3:ListBucket` ([S3 permissions](docs/deployment.md#s3-permissions)).
- **Breaking:** hidden files and folders are never read, listed, written, replaced or used as a conversion source,
  output or sidecar, with listing on or off: any path or S3 key component starting with `.` (a dotfile, anything
  inside a dot-named folder, or anything under a hidden allowed root or prefix) is `422 hidden-path` on
  `GET /v1/storage/file`, `GET /v1/storage/list` (also a `prefix` starting with `.`), convert and detect `input`,
  convert `output` and job file reads. A location outside the allowed folders still gets
  `422 storage-location-rejected` first. Checked on the decoded location, before any storage call. Hidden roots and
  prefixes are left out of `GET /v1/storage/locations`. Dotted names (`show.en.srt`) are unaffected. HTTPS URLs are
  not checked. See [hidden files](docs/usage.md#hidden-files-and-folders).
- **Breaking:** an API key is required by default. The image sets `API_KEYS_FILE=/api_keys.txt`; mount an empty,
  writable file there and a strong key is generated into it on first start (in place, mode 0600, never logged).
  A missing file, a folder, an unreadable file or a read-only empty file stops startup with exit code 78 and an
  explanation. `API_AUTH_DISABLED=true` opts out, with a loud startup warning. See
  [Securing the API](docs/security.md#securing-the-api).
- **Breaking:** `ALLOWED_HOSTS`: the API answers only to `localhost`, `127.0.0.1`, `[::1]` and the listed host names
  (`*` for any, with a warning); other `Host` headers get `400 host-not-allowed`. A browser `Origin` header, when
  present, must match the request's own host and port (scheme ignored; missing ports default to 80 or 443) and that
  host must be allowed: `403 origin-not-allowed`. Health probes are exempt.
- **Breaking:** CORS support is removed. `CORS_ORIGINS` is ignored (with a startup warning) and the API never sends
  CORS headers: a browser front end should not hold an API key, so it calls its own backend instead.
- **Breaking:** only subtitle files are read, listed or written by file and S3 location: the extension must be one
  libse reads or writes (`GET /v1/limits` `storage.extensions`), and an output must use an extension of its target
  format (the `{output}.json` sidecar is exempt). Otherwise `422 extension-not-allowed`. Listings show only those
  files. Job file reads follow the same rule.
- **Breaking:** `ERRORS_VERBOSE` defaults to `false`: error responses no longer carry stack traces or internals;
  they are logged as `error.details requestId=...` instead.
- A folder listing stops at 5000 matching entries: `422 folder-too-large` (narrow it with `prefix`).
- Browse queue: listing, reading by location and job file reads run at most `BROWSE_CONCURRENCY` (4) at a time,
  `BROWSE_QUEUE_CAPACITY` (32) wait up to `BROWSE_WAIT_SECONDS` (30), then `503 browse-busy` with `Retry-After`.
  Separate from the conversion queue. Reported in `GET /v1/limits` `storage.browse`.
- After a conversion timeout no other conversion starts beside the stuck one: `503 conversion-engine-stuck` with
  `Retry-After` and `/readyz` not ready, until it returns. Still running after `CONVERSION_STUCK_GRACE_SECONDS`
  (default twice the timeout), the process logs why and exits with code 70 so the restart policy starts a clean one.
  New metric `subtitleedit_conversion_engine_stuck`.
- XML is screened after stripping leading byte-order marks, zero-width characters and whitespace; any DOCTYPE or
  ENTITY is `422 unsafe-xml`, and XML parsing prohibits DTDs, uses no resolver and limits entity expansion.
- Reads by file location open a path handle and check it is a regular file before opening it, so a FIFO or device
  named like a subtitle can't hang a request. Writes, replaces, sidecars and deletes never follow symlinks.
- `GET /v1/storage/file` and job file reads send an `ETag` (file: size, mtime, ctime, inode; S3: object ETag) and
  answer `If-None-Match` with `304 Not Modified` without reading the content: a cheap change check for caches.
- New error codes: `hidden-path`, `extension-not-allowed`, `folder-too-large` (422), `host-not-allowed` (400),
  `origin-not-allowed` (403), `browse-busy`, `conversion-engine-stuck` (503).
- Fix: a percent-encoded control character in a file or S3 location (for example `file:///data/a.srt%00.txt`) is
  `422 storage-location-rejected` before any filesystem call, instead of `500 internal-error`.

## 1.3.1 — Unreleased

- Job and storage errors have their own problem `title` instead of the generic "Internal error": `job-not-found`, `job-input-not-stored`, `job-output-not-stored`, `job-not-ready`, `job-file-gone`, `job-location-not-replayable`, `storage-location-rejected`, `storage-not-found`, `storage-io-failed` and `output-exists`. Codes and status codes are unchanged.
- `job-output-not-stored` explains the usual cause: a conversion without an `output` location returns its result in the response and stores nothing, so to read a result later, convert with an `output` location. A failed conversion gets its own detail and hint. `job-input-not-stored` points to the original file or an `input` location. See [errors](docs/errors.md#job-output-not-stored).

- Storage outputs report the stored file's name: with `output`, `outputs[].fileName` (in the response, job metadata and sidecar) is now the last path segment of the output location (for example `test2-145152.srt` for `s3://bucket/qa/test2-145152.srt`) instead of the name generated from the source (`show.srt`). Inline results are unchanged.

- Rename the product to **Smart Subtitle Converter** and update repository, container image, assembly and project identifiers. Versions and existing release tags are unchanged.

- Add a GitHub security policy (`SECURITY.md`) pointing at [docs/security.md](docs/security.md).
- Document that GHCR package visibility is a one-time UI setting (Packages REST API cannot change it).
- Default published host port is **8080** again (`127.0.0.1:8080:8080` in Compose and the docs). The container still
  listens on 8080; change only the host side if 8080 is taken. Keep the `127.0.0.1` bind by default. No API contract
  change.
- Startup storage diagnostics (warning-only; `/readyz` unchanged, nothing written, HTTPS hosts not contacted): after
  start the API logs every storage setting as given and as used (`storage.config var=... value=... effective=...`),
  warns about malformed values with the variable, the value, the problem and a fix (`storage.config warning=<code>`:
  quotes/whitespace/CRLF, s3:// or paths in bucket names, invalid bucket names, missing or unknown `S3_REGION`,
  endpoints without scheme or with a path, clamped timeouts, comma-separated file roots, incomplete or mismatched AWS
  keys and session tokens, missing credential files), reports the credential source the AWS SDK resolved
  (`storage.s3 credentials ok source=env|profile|web-identity|container-role|instance-role`), runs one `HeadBucket`
  per allowlisted bucket (with the bucket's real region on a 301), checks each `STORAGE_FILE_ROOTS` entry, and ends
  with `storage.check done ok=N warnings=M`. AWS key ids, secret keys and session tokens are only printed masked
  (`AKI***MPLE`).
- Malformed numeric or boolean settings (for example `IO_TIMEOUT_SECONDS=10s`, `S3_FORCE_PATH_STYLE=yes`) and an
  invalid `STORAGE_FILE_ROOTS` JSON array still stop startup, now with a message naming the variable, the value and
  the fix.
- **Console log format changed:** the default is now plain text, one line per entry
  (`2026-11-09T13:40:51.531Z info  Storage.StorageCheck  storage.s3 bucket=subs ok`). Set `LOG_FORMAT=json` to
  restore the previous JSON console output. An invalid `LOG_FORMAT` falls back to text with a
  `logging.config warning=invalid-log-format` line.
- `docker-compose.yml` loads `.env` into the container with `env_file` (`required: false`). Compose's `.env` alone
  only feeds `${...}` interpolation, so settings such as `S3_ALLOWED_BUCKETS` in `.env` never reached the container.
  The startup log always prints the allowlists, as `(unset — S3 off)` and similar when empty.
- Docs: [S3 permissions](docs/deployment.md#s3-permissions) lists each S3 call, its IAM action and resource, what
  fails without it, per input/output access pattern, plus SSE-KMS, a minimal policy and MinIO notes.

## 1.3.0 — Unreleased

Broadcast-tester follow-ups:

- PAC input code page `auto` now probes all 13 code pages and keeps one only when it yields words of a language that page serves (`codePageConfidence: "probed"`, `probedLanguage`). No match or several different texts: new `422 ambiguous-encoding` with `candidates` and `probe: "pac-code-page"` instead of a silent Latin read. An explicit `inputCodePage` is `"forced"`. A `.pac` name or `source.format: pac` loads PAC even when libse's header check fails. Word-poor PAC files need `inputCodePage`.
- Non-UTF-8 text is probed over the common Windows, ISO-8859 and CJK encodings the same way (`encodingConfidence: "probed"`); conflicting readings give `422 ambiguous-encoding` (`probe: "text-encoding"`); with no recognisable words libse's heuristic is used with a new `encoding-guessed` warning. `encoding.input` still forces.
- Text encoding probe: distinctive-script matches (Arabic, Hebrew, Thai, Greek, Cyrillic, CJK) with dictionary hits drop Latin code-page rivals before deciding, so legacy Arabic/Hebrew/Thai SRTs no longer 422 against a windows-1250 false rival. Genuine clashes still return `422 ambiguous-encoding` with per-candidate `{encoding, language, hits, sample}` (samples always present). New optional `encoding.language` filters the probe; successful force/language retries warn `encoding-forced` / `language-applied` (`encodingConfidence` `forced` / `language`). The no-dictionary-hits `encoding-guessed` path is unchanged.
- Breaking (timing): `target.fps` no longer rescales time-based sources (SRT, WebVTT, ...). Rescaling uses `source.fps` or a rate the file states/implies (EBU header, SCC timecodes); `formatDetails.frameRate`/`frameRateSource` report it. New `frame-rate-assumed` warning when a frame-based source is read at libse's default.
- SCC with drop-frame (`;`) timecodes, or frame numbers of 25 and above, is read at 29.97.
- New source warnings `overlapping-cues`, `long-cue` (over 30 s, with durations) and `programme-start-not-applied` (EBU STL with a non-zero TCP; times unchanged).
- New `format-defaults-applied` warning lists EBU STL / Cavena 890 identity fields filled in although the request didn't set them (libse's `No Title`, `USA`, fallback language, Cavena file-name title).
- Writing an output without `sidecar` deletes a stale `{output}.json` (filesystem, S3); failure keeps the subtitle and adds `sidecar-delete-failed`.
- Filesystem outputs are created `0644` (was `0600`).
- Docs: every error and warning code in `docs/errors.md` now explains what happened, why, and what to do, with the technical details for integrators after it; the README covers storage, jobs and the new checks in the main flow.

- Add input/output job-file routes that re-read storage without retaining bytes; any valid key knowing an unguessable id can read job metadata/files. Distinguish not-ready, not-found, unstored, gone and non-replayable locations, with redacted locations where applicable.
- Add request-level ifExists error | replace for filesystem/S3. S3 default PUT is conditional; explicit filesystem replacement stages before rename. HTTPS PUT retains signer/receiver semantics. Report the documented maximum wait through limits.

- Complete storage/job configuration, usage, mount/UID, policy, error and limits documentation; add a commented `.env.example` and opt-in Postman URI examples.
- Report effective caller wait, I/O retries/timeouts, enabled schemes and job caps/TTL in `/v1/limits`; retain original request correlation and job id on admitted conversion failures.

- Add S3-compatible storage through AWSSDK.S3, configured endpoint/path-style, bucket/prefix allowlists and independent hybrid source/destination schemes.

- Add allowlisted HTTPS GET/PUT with connection-time IP checks, capped streaming, separate I/O budgets and transient retries. Skip HTTPS sidecars with a warning.

- Add allowlisted Linux filesystem inputs/outputs, optional sibling staging and per-request metadata sidecars. Existing files and symlinks are rejected.

- Wait up to the configured default (30 seconds), honour Prefer: wait=0..60 and return 202 with polling headers when work continues.

- Every conversion has a job id. Add bounded in-memory metadata retention and GET /v1/jobs/{id}; no subtitle bytes are retained.

- Breaking: remove ZIP batch input/output and failFast. Submit one subtitle per request. Multiple targets from one subtitle return JSON rather than ZIP.
- Paired with MCP 1.3.0.

## 1.2.1 (unreleased)

`characters-replaced` remedies state facts instead of guessing (approved by Wauter).

- `warnings[].details.remedy` (JSON body and manifests only): `scripts`, `detectedLanguage`, `currentEncoding`,
  `option`, `currentValue`, `allowedValues`, `candidates`, `candidatesTested`, `suggested`, `unicodeAlternative`,
  `note`. See [docs/errors.md](docs/errors.md#remedy-characters-replaced).
- PAC candidates are tested: every non-ASCII code point of the output (code points only) is written with each code
  page and read back; only code pages that keep all of them are listed. `suggested` is set only when exactly one
  value fits. 1.2.0 guessed from the script and detected language (e.g. `czech` for any Latin extended text, which
  doesn't help in libse 5.1.0: latin, czech and portuguese keep the same Latin extended characters).
- When several values fit (Latin extended, CJK, …) the hint lists them; when none does, it says so and points to
  target `fpc`. Hints list code points, never text. Never switched automatically.
- Notes for libse limits: Cavena 890 garbles Greek/Cyrillic/Arabic whatever the language (kept from 1.2.0); Cavena's
  Latin languages share one table; PAC characters lost only next to certain neighbours (e.g. `ť` before `o`) can't
  be fixed by a code page.

## 1.2.0 (unreleased)

Conversion verification and diagnostics (client feedback 2026-10-17, approved by Wauter).

- **Structured read-back verification** for every binary output: `outputs[].verification` (JSON),
  `entries[].verification` keyed by output path (zip manifests), `X-Verification-Verdict` (single binary
  output, multipart). `verdict` is `lossless` | `quantised` | `lossy` | `unverified`, with `cuesWritten`,
  `cuesReadBack`, `timing` (`maxShiftMs`, `meanShiftMs`, `frameMs`, `cuesOverOneFrame`), `replacedChars`
  (cue numbers and code points, first 20, exact totals), `truncatedCues`, `missingCues`, `zeroLengthCues`.
  When cue counts differ, cues are matched by start time (1.1 skipped every check). A read-back that throws
  now gives `unverified` with a `reason` and a `verification-failed` warning (1.1 stayed silent). Positions
  and code points only, never cue text, in responses and logs. See [docs/errors.md](docs/errors.md#verification-binary-outputs).
- **Warnings carry `hint` and `details`** (JSON body and manifest only; `X-Warnings` keeps code, message,
  count). `characters-replaced` names the script, the cues and the code points, and suggests the remedy
  for the target (`formatOptions.pac.codePage`, `formatOptions.ebuStl.characterTable`,
  `formatOptions.cavena890.language`, or target `fpc`); nothing is switched automatically.
- New warning codes: `text-truncated` (Cavena 890 / Cheetah CAP Old cut lines; no longer counted as
  replaced characters), `zero-length-cue`, `source-replacement-chars` (U+FFFD already in the source; no
  longer counted as an encoding loss), `verification-failed`. A zero-length cue written to EBU STL now
  gets a `lossy-format` warning naming the cue and the 99:59:59,999 read-back, instead of "Timing changed
  by up to 359999999 ms".
- `POST /v1/detect` reports `bom` and `lineEndings` (crlf | lf | cr | mixed | none) for text input, and
  `warnings` (`zero-length-cue`, `source-replacement-chars`).
- **Behaviour change: `422 ambiguous-format`.** PAC and PAC Unicode (FPC) share their header bytes. When
  auto-detection finds bytes valid as both and no `.pac` / `.fpc` file name decides, the input is now
  rejected (`candidates: ["pacscreenelectronics", "pacunicodeunipac"]`, hint to set
  `profile.source.format`). 1.1 silently picked PAC, so FPC files without their extension were reported as
  PAC. Correct extensions and an explicit `source.format` work as before; zip entries are checked by entry
  name. PAC files that only parse as PAC (e.g. the Greek PAC fixture) are unaffected.
- Known libse 5.1.0 limits now visible through verification: Cavena 890 doesn't round-trip Greek,
  Cyrillic or Arabic (garbled, or its reader throws on its own Greek output); Hebrew and Chinese do.

## 1.1.1 (unreleased)

- Default host port is now **4041** instead of 8080 (8080 is often in use): `docker-compose.yml` publishes
  `127.0.0.1:4041:8080`, bound to localhost only by default (README "Ports" explains how to expose it on other
  interfaces). README, design examples and the Postman collection/environment `baseUrl` use
  `http://localhost:4041`. The container still listens on 8080 internally (`ASPNETCORE_HTTP_PORTS`, `EXPOSE`,
  healthcheck unchanged). No API contract change.

## 1.1.0 (unreleased)

Binary broadcast formats milestone (PR #4) plus the `frame-rate-changed` warning.

- Read and write EBU STL, PAC (incl. Unicode PAC), Cavena 890, CapMaker Plus and Cheetah CAP
  (`formatOptions.ebuStl`, `.pac`, `.cavena890`, `.cheetah`); binary detection with `codePage` and
  `formatDetails`; binary content as base64 in `/v1/convert/json`.
- `GET /v1/limits`: effective body caps, zip guards, queue depth and whether auth is required.
- MCP review fixes A1–A11 (OpenAPI problem schema and security, binding errors, zip entry pointers,
  `504 conversion-timeout` `phase`, ...).
- `422 ambiguous-batch-names`: zip entries sharing folder + stem, or colliding output names, are
  rejected up front instead of renamed.
- New warning `frame-rate-changed` (EBU STL): the written Disk Format Code implies a different frame
  rate than the requested or source rate, e.g. 29.97 fps written as `STL30.01`
  ([docs/errors.md](docs/errors.md#warning-codes), design §13 row 23).
- `/healthz` and OpenAPI `info.version` now report `1.1.0`; OpenAPI `info.version` follows the csproj.

## 1.0.0 (unreleased)

First text-to-text version: `/v1/convert`, `/v1/convert/json`, `/v1/detect`, formats, presets,
profiles and schema endpoints, FIFO conversion queue, optional API keys.
