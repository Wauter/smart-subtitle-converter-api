# Subtitle Edit Converter API — Design Doc

Subtitle Edit Converter API is a community wrapper around Subtitle Edit's `libse`. It is not an official Subtitle Edit project.

| | |
|---|---|
| Owner | Wauter De Bruyne |
| Status | v1 implemented (see §13 for where the code differs from this design) |
| Date | 2026-10-16 |
| Upstream | [SubtitleEdit/subtitleedit](https://github.com/SubtitleEdit/subtitleedit) (MIT), reviewed at `main` @ `f9a41e0` |
| Repos | `subtitleedit-converter-api` (service), `subtitleedit-converter-mcp` (MCP server); formerly `subtitleedit-api` / `subtitleedit-mcp`, see §13 row 12 |

## 0. Repo findings that shape this design

These come from reading `/workspace/subtitleedit` (`main` @ `f9a41e0`, 2026-10-16). Check them again at each pin bump.

| Finding | Evidence | Consequence |
|---|---|---|
| Only `libse` is packable. It ships on NuGet as `libse`; target frameworks and dependency pins are in the project file. | `src/libse/LibSE.csproj` (`IsPackable=true`) | Pin it as a NuGet package. |
| `libuilogic` is **not packable**. | `src/libuilogic/LibUiLogic.csproj` (`IsPackable=false`) | Nothing from libuilogic is available on NuGet. |
| `seconv` is an `Exe`. Its pipeline (`SubtitleConverter`, `ConversionOptions`, `LibSEIntegration.ApplyOperations`, `OperationOrderParser`) is `internal`, and `InternalsVisibleTo` names only `SeConvTests`. | `src/seconv/SeConv.csproj`, `Core/*.cs` | It can't be consumed as a library, whether by NuGet or by project reference. |
| The seconv pipeline works on file paths and globs (`Patterns`, `OutputFolder`, rotating file names, `AtomicFileWriter`), not on streams. | `ConversionOptions` | It doesn't fit a request/response model without refactoring. |
| Most operations are thin calls into **libse** public APIs (`RemoveTextForHI`, `MergeShortLinesUtils`, `SplitLongLinesHelper`, `FixCasing`, `FixDurationLimits`, `TimeCodesBeautifier`, `Utilities.AutoBreakLine`, the FCE rules under `libse/Forms/FixCommonErrors`, …). | `LibSEIntegration.cs` around lines 760–930 | The pipeline can be rebuilt on libse alone. |
| Three things need libuilogic: the `FixCommonOcrErrors` pass (OCR-fix engine plus Hunspell), `customtext` output (`CustomTextFormatter`), and loaders for non-registered or load-only formats (`SubtitleLoading/*`). OCR, translate and image export need it too, but those are out of scope for V1. | `FixCommonErrorsRunner.cs`, `LibSEIntegration.cs:414` | V1 leaves these features out. |
| Operations read **process-global** settings (`Configuration.Settings.General.SubtitleLineMaximumLength`, and so on). seconv touches this about 30 times. | `LibSEIntegration.cs` | Concurrent requests with different settings would race. See §2.4. |
| The format registry `SubtitleFormat.AllSubtitleFormats` is built from a private static list and has no public registration hook. | `libse/SubtitleFormats/SubtitleFormat.cs` | Our plugin formats need our own registry, plus an upstream PR. |
| libse has 432 files under `SubtitleFormats/` and about 400 `: SubtitleFormat` subclasses. Text-based and binary formats are told apart by `IsTextBased`. | `rg` | `/v1/formats` reports this flag. |
| NuGet publishing is a manual `workflow_dispatch` with an explicit version. The local clone is shallow and has no tags. | `.github/workflows/publish-nuget.yml` | Pin to a NuGet version. If a submodule is used, pin it to a SHA, because tags may lag. |

## 1. Goals / non-goals

**Goals (V1)**
- One subtitle conversion over HTTP: synchronous result or asynchronous job metadata, with optional URI storage.
- Run libse's operation pipeline under the same names and semantics as `seconv`, so presets carry over between the CLI and the API.
- Named, file-based presets ("profiles"). They can be built in or mounted from a folder, and can be inspected for troubleshooting.
- A small, stateless container (100–150 MB) built for amd64 and arm64.
- A machine-friendly contract: generated OpenAPI, RFC 7807 errors, and a JSON endpoint for agents.

**Non-goals (V1)**
- Image or bitmap formats (Blu-ray sup, VobSub, DVB-sub, image export) and OCR.
- Container extraction (MKV, MP4, TS, MXF).
- Auto-translate, `FixCommonOcrErrors` (Hunspell), and `customtext` output.
- Async jobs, persistence of uploads or results, multi-tenancy, SSO, and server-side saved profiles. V1 has optional API-key auth only (§6.1).
- Testing libse's format logic. We trust upstream and test only our own layer (§9).
- Batch globbing (this is not a file-system tool).
- Trimming or AOT.

## 2. Architecture

```
            ┌───────── subtitleedit-converter-api (ASP.NET Core, .NET 10) ─────────┐
 HTTP ──►   │ Endpoints (Minimal API) ─► ProfileResolver ─► ConversionEngine       │
            │      │                         │                 │                   │
            │  OpenAPI/Scalar           Presets (built-in  ┌───┴────────────┐      │
            │  ProblemDetails            + /profiles mount)│ FormatRegistry │      │
            │                                              │ libse + plugin │      │
            │                                              └───┬────────────┘      │
            │             SubtitleEditConverterApi.Pipeline ───┤ (ops adapter)     │
            └──────────────────────────────────────────────────┼───────────────────┘
                                                   NuGet: libse (pinned in project)
                                                   NuGet: SubtitleEditConverterApi.Formats (own plugin)
```

### 2.1 Dependency strategy: NuGet (recommended)

The brief asked for "reuse seconv's pipeline in-process, no source copy". With the current repo that isn't fully possible (see §0): seconv's pipeline is internal, file-based, and depends on libuilogic, which isn't packaged. Options:

| Option | Pros | Cons |
|---|---|---|
| **A. NuGet `libse` only, plus a thin pipeline adapter (recommended for V1)** | Reproducible, small, and Renovate-friendly. No upstream source in our tree. | The adapter (about 300 LOC) re-calls the same libse APIs seconv calls. That is an adapter over public APIs, not copied source. Covers about 95% of text ops. No `FixCommonOcrErrors` and no `customtext`. |
| B. Git submodule pinned to a SHA, with `ProjectReference` to libuilogic and seconv | Full parity, including OCR fix and custom text. | seconv internals are still out of reach, so we'd need `InternalsVisibleTo` (an upstream change) or reflection (no). It also pulls in Spectre, CommunityToolkit.Mvvm and Hunspell, makes builds heavier, and makes bumps manual. |
| C. Upstream PR: extract `seconv/Core` into a packable library (`libseconv`) with a stream-based public API, and make libuilogic packable | Real reuse with no adapter. The CLI and the API share one code path. | Depends on the maintainer accepting it, with uncertain timing. |

**Decision:** ship V1 on **A**, and open the upstream PR for **C** at the same time. Once a `libseconv` package exists, replace the adapter with it. Keep **B** as a fallback only if a V1.x feature hard-requires libuilogic, such as OCR-fix. Rules for A:
- The adapter's operation IDs, ordering rules and defaults mirror `OperationOrderParser` and `LibSEIntegration.ApplyOperations`, including GUI-matching constants such as the 100 ms merge-same-text threshold.
- The adapter is kept small and reviewed against the upstream switch on every libse bump (§8). We don't run a seconv parity job: format and operation correctness belongs to upstream.

### 2.2 Components
- **Endpoints**: Minimal APIs, with `Microsoft.AspNetCore.OpenApi` (`AddOpenApi`/`MapOpenApi`, which serves `/openapi/v1.json`) and `Scalar.AspNetCore` (`MapScalarApiReference("/docs")`).
- **FormatRegistry**: the union of `SubtitleFormat.AllSubtitleFormats` and our plugin formats. It owns ID normalisation (seconv-style aliases `srt`, `vtt`, `ass`, `ebu`, …, taken from `LibSEIntegration.cs:1318ff`) and detection. Detection uses `IsMine(lines, fileName)` in registry order, with plugin formats ordered first only when they are explicitly marked `Priority`.
- **Plugin formats**: assembly `SubtitleEditConverterApi.Formats`. Classes subclass `SubtitleFormat` and implement `Extension`, `Name`, `IsMine`, `ToText` and `LoadSubtitle`. They are discovered by reflection at startup, not dynamically from disk, so the set is fixed at build time. Upstream PR: add `SubtitleFormat.RegisterFormat(SubtitleFormat)`. Formats of general value are proposed upstream.
- **ProfileResolver**: merges profile layers (built-in → mounted folder → request overrides) and validates them against the JSON Schema (§5).
- **ConversionEngine**: decode, load, apply operations, write, encode. All in memory, with no temp files.
- **Upstream fixes**: bugs found in libse are fixed by PR upstream first. Until the fix ships, we may carry a workaround in our adapter, guarded by a test that turns red once the fix lands.

### 2.3 Request flow
1. Read the upload into memory (size-capped, §4.8).
2. Pick the input encoding: BOM, then UTF-8 validation, then `profile.encoding.inputFallback` or libse `DetectAnsiEncoding`. Register `CodePagesEncodingProvider` at startup.
3. Detect the format, or use `profile.source.format`. Unknown input → `422 format-not-detected`.
4. Apply the profile settings, then operations (§5) in canonical or explicit order.
5. Write with the target format's `ToText`, then encode (default UTF-8 with BOM, matching seconv).
6. A multi-output result (for example `outputs: [srt, vtt]`) is returned as JSON.

### 2.4 Conversion queue (global-state hazard)
libse operations read `Configuration.Settings`, a static, mutable object. Two conversions can therefore never run at the same time in one process. V1 uses an explicit queue:

- A **bounded FIFO queue** (`System.Threading.Channels.BoundedChannel`, capacity `QUEUE_CAPACITY`, default 32) feeds a **single worker**.
- The worker runs one job at a time: **apply settings → load → ops → write → restore defaults**. Settings are restored in a `finally`, so even a failed job leaves the baseline clean.
- **Queue full**: the request is rejected immediately with `503 queue-full` and `Retry-After`. The value is estimated from queue depth × the rolling average job time, rounded up, with a minimum of 1 second.
- **Per-conversion timeout** (`CONVERSION_TIMEOUT_SECONDS`, default 30): time spent waiting in the queue counts toward the request deadline, and time spent running has its own budget. A job that runs too long returns `504 conversion-timeout`. libse calls can't be cancelled cooperatively, so after a timeout the worker is marked dirty and recycled: the old thread is abandoned, settings are reset and a new worker starts. If timeouts repeat (3 in 5 minutes), `/readyz` fails so the orchestrator restarts the pod.
- A client disconnect cancels a job that is still queued. A job that is already running finishes and its result is discarded.
- Scale out with replicas. Metrics: `queue_depth`, `queue_wait_seconds`, `conversion_seconds`, `rejected_total{reason}`.
- Upstream PR: pass settings explicitly instead of through the global, which would allow N workers.

## 3. Docker

- **Base:** `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` plus an ICU/tzdata overlay built by `docker/icu-slim.sh` (see §13 row 3). libse does culture-sensitive casing and language detection, so we don't run in invariant globalization mode (`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false`; plain chiseled defaults to `true`). The previous `-chiseled-extra` base stays available with `--build-arg RUNTIME_BASE=base-extra`. Runs as a non-root user (`app`, UID 1654). There is no shell.
- **Build:** `mcr.microsoft.com/dotnet/sdk:10.0`, multi-stage, `dotnet publish -c Release --no-self-contained -r linux-$TARGETARCH`. `$BUILDPLATFORM` cross-builds the managed code, so QEMU isn't needed for compilation.
- **Natives:** libse references SkiaSharp. Add `SkiaSharp.NativeAssets.Linux.NoDependencies` (no fontconfig dependency, which suits chiseled images) and pin it to the matching package version. This avoids the `DllNotFoundException` in seconv's csproj comment (#11872) and the ABI mismatch (#11864/#11931). Text conversion rarely loads Skia, but the startup self-test behind `/readyz` exercises it once.
- **Size:** measured, not estimated: amd64 158 MiB uncompressed / 68 MiB compressed, arm64 165 / 64 MiB (§13 row 3). CI computes exact sizes from an OCI export on every run, writes them to the job summary and fails at measured + ~15% per arch.
- **Multi-arch:** GitHub Actions `docker/setup-qemu-action` + `docker/setup-buildx-action` + `docker/build-push-action`, `platforms: linux/amd64,linux/arm64`, pushed to GHCR. Tags are `:X.Y.Z`, `:X.Y`, `:latest` and `:sha-<short>`. Each image also gets an SBOM and provenance attestation.
- **Runtime config:** `PROFILES_DIR=/profiles` (read-only mount), `MAX_UPLOAD_BYTES`, `QUEUE_CAPACITY`, `CONVERSION_TIMEOUT_SECONDS`, `API_KEYS_FILE`, `ERRORS_VERBOSE`, `ASPNETCORE_HTTP_PORTS=8080` (full list in §6–§7). Probes: liveness `/healthz`, readiness `/readyz`. The chiseled image has no curl, so the orchestrator probes it.
- **Trimming/AOT:** deferred. libse uses reflection over format types, and `System.Text.Json` would need source generation. Revisit after V1 once we have size and cold-start data.

## 4. API contract (v1)

OpenAPI is generated natively at `GET /openapi/v1.json`, and Scalar UI is served at `GET /docs`. All bodies are UTF-8 JSON unless noted. Every response carries `X-Request-Id` and `X-Libse-Version`.

### 4.1 `POST /v1/convert` (multipart/form-data)
| Part | Type | Notes |
|---|---|---|
| `file` | binary, required | Source subtitle. |
| `preset` | string, optional | Preset name (built-in or mounted). |
| `profile` | JSON, optional | A full profile, or overrides merged on top of `preset`. |
| `target` | string, optional | Shorthand for `profile.target.format`. |

At least one of `preset`, `profile.target.format` or `target` is required.

**200**: the converted file, with `Content-Type` from the target format (`application/x-subrip`, `text/vtt`, `text/x-ssa`, `application/ttml+xml`, …, falling back to `text/plain; charset=…`) and `Content-Disposition: attachment; filename="<stem>.<ext>"` (RFC 6266 `filename*` for non-ASCII). Multiple targets (`target.additional`) return JSON. ZIP inputs are rejected. Response headers are listed in §7.3.

```bash
curl -F file=@movie.srt -F preset=netflix-en-vtt localhost:8080/v1/convert -OJ
curl -F file=@movie.srt -F target=webvtt -F 'profile={"timing":{"offsetMs":-1500}}' localhost:8080/v1/convert -OJ
```

### 4.2 `POST /v1/convert/json` (for agents)
```json
// request
{ "content": "1\n00:00:01,000 --> ...", "contentEncoding": "text",   // or "base64"
  "fileName": "movie.srt", "preset": "clean-srt", "profile": { "target": { "format": "webvtt" } } }
// 200
{ "outputs": [ { "fileName": "movie.vtt", "format": "WebVTT", "mediaType": "text/vtt",
                 "content": "WEBVTT\n\n...", "contentEncoding": "text" } ],
  "detected": { "format": "SubRip", "encoding": "utf-8", "language": "en" },
  "stats": { "paragraphsIn": 812, "paragraphsOut": 798, "elapsedMs": 41 },
  "warnings": [ { "code": "op-noop", "message": "SplitLongLines changed nothing" } ],
  "profile": { "...effective merged profile..." : "" } }
```
Output content comes back as `text` when the target encoding is a Unicode encoding, and as `base64` otherwise. A client can force base64 with `"responseEncoding": "base64"`.

### 4.3 `GET /v1/formats`
`?q=` filter, `?textOnly=true` (default `true` in V1). Each item:
`{ "id": "webvtt", "name": "WebVTT", "aliases": ["vtt"], "extension": ".vtt", "alternateExtensions": [], "textBased": true, "frameBased": false, "styleSupport": true, "positionSupport": true, "canRead": true, "canWrite": true, "source": "libse" | "plugin" }`

### 4.4 `POST /v1/detect`
The body is multipart `file` or JSON `{content, contentEncoding, fileName}`. Response: `{ "format": "SubRip", "id": "srt", "encoding": "windows-1252", "encodingConfidence": "bom|utf8-valid|heuristic", "language": "en", "paragraphs": 812, "candidates": [ "SubRip", "..." ] }`. If nothing matches → `422 format-not-detected`, with hints.

### 4.5 `GET /v1/profiles`, `GET /v1/profiles/{name}`
The list includes valid **and** invalid presets, so operators can see why a preset fails to load:
```json
[ { "name": "netflix-en-vtt", "origin": "builtin", "path": "builtin://netflix-en-vtt.json",
    "valid": true, "description": "...", "target": "webvtt" },
  { "name": "broadcast-stl", "origin": "mounted", "path": "/profiles/broadcast-stl.json",
    "valid": false, "loadedAt": "2026-10-16T12:00:00Z",
    "errors": [ { "pointer": "/operations/2/id", "message": "Unknown operation 'SplitLong'", "hint": "Did you mean 'SplitLongLines'?" } ] } ]
```
`{name}` returns the raw and the resolved (`extends`-flattened) profile, plus its errors. A mounted preset overrides a built-in preset of the same name, and the response says so with `"shadows": "builtin://..."`. The folder is re-scanned on `GET` when its mtime changes, so no restart is needed.

### 4.6 `POST /v1/profiles/validate`
The body is a profile JSON. Response `200 { "valid": bool, "errors": [...], "warnings": [...], "resolved": {...} }`. It never returns 4xx for an invalid profile, only for unparseable JSON.

### 4.7 `GET /healthz`, `GET /readyz`, `GET /metrics`
- `/healthz` (liveness): the process is up. `200 {"status":"ok","version":"…","libse":"…"}` (`version` is the csproj `<Version>`, see CHANGELOG.md). No dependencies are checked.
- `/readyz` (readiness): the format registry is loaded, a built-in self-test conversion (embedded SRT → VTT) passed at startup, the worker is healthy and the queue is not full. Returns `503` with the failing check named. Invalid mounted presets do **not** fail readiness; their count is reported (`"profiles":{"valid":7,"invalid":1}`).
- `/metrics`: Prometheus, off unless `METRICS_ENABLED=true`. It can be served on a separate port (`METRICS_PORT`) so it stays off the public listener.
- These endpoints are never behind auth and are excluded from rate limiting.

### 4.8 Error codes
Error body format: §7.1. The codes below are stable. New codes may be added, and existing ones are never renamed. This is the original draft; the current catalogue, with what each code means and what to do, is [errors.md](errors.md).

| Status | code | Hint example |
|---|---|---|
| 400 | `invalid-request` | "Provide `preset`, `target`, or `profile.target.format`." |
| 400 | `invalid-profile` | Points at `/timing/fps`: "fps must be > 0; common values 23.976, 25, 29.97." |
| 400 | `empty-input` | "The uploaded file is 0 bytes." |
| 401 | `unauthorized` | "Send `Authorization: Bearer <key>`." |
| 404 | `preset-not-found` | "Known presets: … See GET /v1/profiles (mounted presets with errors are listed there)." |
| 413 | `payload-too-large` | "Max upload is 10 MB (MAX_UPLOAD_BYTES)." |
| 415 | `unsupported-media-type` | "Use multipart/form-data or POST /v1/convert/json." |
| 422 | `format-not-detected` | "Set `profile.source.format`; see GET /v1/formats." Includes the first lines' shape, for example "looks like XML root `<tt>` but no TTML namespace." |
| 422 | `format-not-supported` | "'bluraysup' is image-based; V1 supports text formats only." |
| 422 | `encoding-error` | "Input is not valid UTF-8; set `encoding.input` (e.g. `windows-1252`)." |
| 422 | `unsafe-xml` | "DTDs are not allowed." |
| 422 | `conversion-failed` | Names the operation ID that threw, and the cue where possible. |
| 429 | `rate-limited` | `Retry-After`. |
| 503 | `queue-full` | `Retry-After`. |
| 504 | `conversion-timeout` | "Conversion exceeded 30 s; split the file or simplify operations." |

## 5. Profile JSON schema (draft)

Fields map 1:1 to seconv options and to the `dump-settings` schema (`docs/reference/command-line.md`), so a seconv user can translate a profile directly. The schema is published at `/v1/schemas/profile.json` (JSON Schema 2020-12) and is also embedded in OpenAPI.

```jsonc
{
  "$schema": "https://<host>/v1/schemas/profile.json",
  "name": "string",                       // presets only; [a-z0-9-]+
  "description": "string",
  "extends": "preset-name",               // single inheritance, depth ≤ 3
  "source": {
    "format": "auto",                     // or id/alias; seconv: format detection
    "fps": 25                             // seconv --fps (frame-based inputs)
  },
  "target": {
    "format": "webvtt",                   // required after merge; seconv <format>
    "fps": 23.976,                        // output rate; rescales only from source.fps or a rate the file states
    "fileName": "{stem}.{ext}",           // tokens: stem, ext, format
    "additional": ["subrip"]              // extra outputs → JSON
  },
  "encoding": {
    "output": "utf-8",                    // seconv --encoding: utf-8 | utf-8-no-bom | <name|codepage> | source
    "inputFallback": "windows-1252"       // seconv --input-encoding-fallback
  },
  "timing": {
    "offsetMs": 0,                        // seconv --offset (signed)
    "changeSpeedPercent": 100,            // --change-speed (>0)
    "adjustDurationMs": 0,                // --adjust-duration
    "bridgeGapsMs": null,                 // --bridge-gaps
    "minGapMs": null,                     // --apply-min-gap
    "renumberFrom": 1                     // --renumber
  },
  "rules": {                              // → Configuration.Settings.General (dump-settings "general")
    "maxLineLength": 43,                  // subtitleLineMaximumLength
    "maxLines": 2,                        // maxNumberOfLines
    "minDurationMs": 1000,                // subtitleMinimumDisplayMilliseconds
    "maxDurationMs": 8000,                // subtitleMaximumDisplayMilliseconds
    "minGapMs": 24,                       // minimumMillisecondsBetweenLines
    "maxCps": 25, "optimalCps": 15,       // subtitleMaximum/OptimalCharactersPerSeconds
    "maxWpm": 400,
    "mergeLinesShorterThan": 33,
    "dialogStyle": "DashBothLinesWithSpace",
    "continuationStyle": "None",
    "mergeShortLines": { "maxGapMs": 250, "onlyContinuous": true },   // "tools"
    "removeTextForHI": { "betweenBrackets": true, "betweenParentheses": true,
                         "beforeColon": true, "beforeColonOnlyIfUppercase": true,
                         "interjections": false /* … full dump-settings section */ }
  },
  "operations": [                         // executed in array order (cf. seconv operation order)
    { "id": "RemoveTextForHI" },
    { "id": "FixCommonErrors", "rules": ["all", "-FixDanishLetterI"], "language": "auto" },
    { "id": "RemoveFormatting", "rules": ["all"] },
    { "id": "MergeSameTexts" }, { "id": "MergeSameTimeCodes" }, { "id": "MergeShortLines" },
    { "id": "SplitLongLines" }, { "id": "BalanceLines" }, { "id": "RedoCasing" },
    { "id": "RemoveLineBreaks" }, { "id": "ApplyDurationLimits" }, { "id": "BeautifyTimeCodes" },
    { "id": "ConvertColorsToDialog" }, { "id": "FixRtlViaUnicodeChars" },
    { "id": "RemoveUnicodeControlChars" }, { "id": "ReverseRtlStartEnd" },
    { "id": "DeleteFirst", "count": 1 }, { "id": "DeleteLast", "count": 1 },
    { "id": "DeleteContains", "text": "subtitles by" },
    { "id": "MultipleReplace", "rules": [ { "find": "…", "replace": "…", "mode": "text|caseSensitive|regex" } ] }
  ],
  "formatOptions": {                      // only applied when target matches
    "assa":      { "playResX": 1920, "playResY": 1080, "styleBlock": "[V4+ Styles]…" },  // --resolution, --assa-style-file (inline)
    "pac":       { "codePage": "Latin", "secondaryCodePage": null },                      // --pac-codepage
    "ebuStl":    { "headerBase64": null },                                                // --ebu-header-file (inline)
    "plainText": { "merge": false, "unbreak": false, "blankLine": true }                  // --plaintext-*
  }
}
```

Validation notes:
- Merge semantics: objects deep-merge; arrays (`operations`, `additional`) replace wholesale; `null` clears an inherited value.
- Unknown keys are **errors**, not warnings. seconv makes the same choice for unknown CLI flags, but its `--settings` JSON only warns on unknown keys.
- File-path options from seconv (`--assa-style-file`, `--ebu-header-file`, `--multiple-replace`) become inline content. Those operation-specific files stay inline; source/destination storage paths use separate allowlisted `input`/`output` fields.
- Not in V1 (rejected with a hint): `FixCommonErrors` rule `FixCommonOcrErrors` (needs Hunspell or libuilogic), `customtext`, OCR, `translate`, image styling, and track selection.
- `FixMisreadQuotes` is opt-in, as in seconv (`all` does not include it).
- The `rules.*` defaults are the libse defaults (`seconv dump-settings`). The test suite asserts they match the pinned libse version.

### 5.1 Example presets (built in under `presets/`)

```json
{ "name": "srt-to-vtt", "description": "Plain format change, UTF-8 no BOM",
  "target": { "format": "webvtt" }, "encoding": { "output": "utf-8-no-bom" } }
```
```json
{ "name": "clean-srt", "description": "Common cleanup pass (seconv README example)",
  "target": { "format": "subrip" },
  "operations": [ { "id": "RemoveTextForHI" }, { "id": "FixCommonErrors" },
                  { "id": "MergeSameTexts" }, { "id": "SplitLongLines" } ] }
```
```json
{ "name": "netflix-en-vtt", "extends": "clean-srt", "description": "Approximation of Netflix EN timed-text style guide",
  "target": { "format": "webvtt" }, "encoding": { "output": "utf-8-no-bom" },
  "rules": { "maxLineLength": 42, "maxLines": 2, "maxCps": 20, "minDurationMs": 833, "maxDurationMs": 7000, "minGapMs": 83 },
  "operations": [ { "id": "FixCommonErrors" }, { "id": "SplitLongLines" }, { "id": "BalanceLines" },
                  { "id": "ApplyDurationLimits" } ],
  "timing": { "minGapMs": 83 } }
```
```json
{ "name": "pal-to-film", "description": "25 fps source → 23.976 fps target",
  "source": { "fps": 25 }, "target": { "format": "subrip", "fps": 23.976 } }
```
```json
{ "name": "ebu-stl-broadcast", "target": { "format": "ebustl", "fps": 25 },
  "rules": { "maxLineLength": 37, "maxLines": 2 },
  "operations": [ { "id": "RemoveFormatting" }, { "id": "SplitLongLines" }, { "id": "ApplyDurationLimits" } ] }
```

## 6. Security and auth

### 6.1 Authentication (optional API keys)
- **Off by default**: if no keys are configured, the API is open. That suits local and sidecar use, and startup logs a `WARN auth.disabled` line.
- **Bearer keys**: `Authorization: Bearer <key>`. Keys come from `API_KEYS` (comma-separated) and/or `API_KEYS_FILE` (a mounted file, for example a Kubernetes Secret, with one key per line, `#` comments, and an optional `name:key` form so logs and metrics carry a key **name**, never the key itself).
- **Rotation**: the file is watched and reloaded without a restart. Overlapping old and new keys allows zero-downtime rotation. Keys are compared in constant time against SHA-256 hashes held in memory.
- **Scope**: when auth is enabled, every `/v1/*` endpoint requires a key. `/healthz`, `/readyz`, `/metrics`, `/openapi/v1.json` and `/docs` stay open (the docs can be disabled with `DOCS_ENABLED=false`).
- **TLS** is terminated by a reverse proxy or ingress. The service listens on plain HTTP on 8080 and honours `X-Forwarded-*` only from `KNOWN_PROXIES`.
- **SSO, OIDC, tenants and per-user quotas** belong in an API gateway in front of the service and are out of scope here.
- **MCP** (§10) passes a key through: `SUBTITLE_API_KEY` from its own config, or, in HTTP mode, the bearer token of the incoming MCP request forwarded unchanged.

### 6.2 Input and resource safeguards
| Control | Default | Env |
|---|---|---|
| Max upload (request body) | 10 MB | `MAX_UPLOAD_BYTES` |
| Per-conversion timeout | 30 s | `CONVERSION_TIMEOUT_SECONDS` |
| Queue capacity | 32 | `QUEUE_CAPACITY` |
| Per-key rate limit (optional, fixed window via `Microsoft.AspNetCore.RateLimiting`, partitioned by key name, or by client IP when auth is off) | off | `RATE_LIMIT_PER_MINUTE` |
| Max cues / line length | 200 k / 10 k chars | `MAX_CUES`, `MAX_LINE_CHARS` |
| Profile JSON size | 64 KB | n/a |
| `MultipleReplace` regex | 100 ms match timeout, `RegexOptions.NonBacktracking` where possible | n/a |

- **XML**: everything is parsed with `DtdProcessing = Prohibit` and `XmlResolver = null`, so external entities are never fetched. A DTD returns `422 unsafe-xml`. libse creates its own `XmlDocument` instances in format classes, so the service **pre-screens** every XML-looking input with an `XmlReader` with DTDs prohibited and no resolver before handing it to libse. The same parser settings are proposed for libse’s own readers.
- **Storage**: uploads/results are bounded in memory; binary operations use private temporary files. Optional allowlisted filesystem/S3/HTTPS input and output are described in [usage](usage.md#filesystem-locations-and-sidecars). Retained jobs contain metadata only.
- **CORS**: off by default. `CORS_ORIGINS` enables an explicit allow-list; wildcards are not supported when auth is on.
- **Headers**: `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'` on API responses (relaxed only for `/docs`), and `Cache-Control: no-store` on conversion responses.
- **Container**: non-root, read-only root filesystem, no shell (chiseled image), all capabilities dropped.

## 7. Transparency and observability

### 7.1 Errors (RFC 7807)
Every error is `application/problem+json`:
```json
{ "type": "https://<docs>/errors/format-not-detected", "title": "Format not detected", "status": 422,
  "code": "format-not-detected",
  "detail": "No text format recognised the input.",
  "hint": "Set profile.source.format, or check GET /v1/formats.",
  "requestId": "01JA9…",
  "errors": [ { "pointer": "/source/format", "message": "…" } ],
  "debug": {                                   // verbose only
    "detected": { "encoding": "windows-1252", "encodingConfidence": "heuristic", "firstLine": "1", "lineCount": 2410 },
    "location": { "line": 1873, "cue": 412, "snippetShape": "00:12:01,000 -> 00:12:03,500" },
    "operation": "SplitLongLines",
    "exception": { "type": "System.FormatException", "message": "…", "stackTrace": "…" }
  } }
```
- `code`, `title`, `detail`, `hint` and `requestId` are **always** present. `code` is the stable contract, and clients should branch on it, not on `title`.
- `ERRORS_VERBOSE` defaults to `true`, which suits self-hosting and troubleshooting. With `ERRORS_VERBOSE=false`, `debug.exception`, stack traces, file-system paths (such as a mounted preset path in error responses) and `snippetShape` are removed. The code, message, hint, request ID and JSON-pointer `errors[]` stay.
- `snippetShape` shows structure, not content: letters are replaced with `x` and digits and punctuation kept, so a failing cue can be located without echoing subtitle text.

### 7.2 Request ID and logs
- `X-Request-Id` is on **every** response, errors included. An incoming `X-Request-Id` (≤ 64 chars, `[A-Za-z0-9._-]`) is honoured; otherwise the service generates a ULID. It is also the trace ID when OpenTelemetry is enabled.
- Logs are structured JSON (`Microsoft.Extensions.Logging` JSON console). Each record carries `requestId`, `route`, `status`, `durationMs`, `queueWaitMs`, `bytesIn` and `bytesOut`, plus `format`, `encoding`, `cues`, `preset`, `operations[]` and `keyName`.
- Subtitle content, uploaded file names and keys are **never logged**. File names are logged only as a hash and extension.
- Security events are **always** logged at `Warning` or higher, whatever the log level: auth failures (with key-name hint or prefix hash, never the key), rate-limit hits, XML-guard rejections, oversized bodies, timeouts and key-file reloads.

### 7.3 Response headers on conversions
`X-Request-Id`, `X-Detected-Format`, `X-Detected-Encoding`, `X-Cue-Count` (output), `X-Cue-Count-In`, `X-Warnings-Count`, `X-Warnings` (compact JSON, truncated to 4 KB; the complete list is in the JSON endpoint), `X-Libse-Version`, `X-Profile` (resolved preset name). CORS `Access-Control-Expose-Headers` lists them when CORS is enabled.

### 7.4 Versioning
- Path versioning `/v1`. Package compatibility changes, including removed features while alpha, are recorded in the CHANGELOG.
- Removing or renaming anything, or changing semantics, requires `/v2`, which runs alongside v1 for at least one minor release cycle and is announced with `Deprecation`/`Sunset` headers.
- The OpenAPI document is committed. CI rejects breaking diffs (§9).

## 8. Upgrade process (upstream formats and fixes)

1. **Bump**: Renovate (preferred over Dependabot, because it groups `libse` with the pinned `SkiaSharp.NativeAssets.Linux.NoDependencies`) opens a PR for the new `libse` version.
2. **CI on the bump PR**: unit tests, API edge-case tests, the smoke corpus, the OpenAPI diff and Docker smoke on both architectures (§9). Format correctness is upstream's responsibility, so we don't re-test it.
3. **Review checklist**: read the upstream release notes, and diff `LibSEIntegration.ApplyOperations` and `OperationOrderParser` between the old and new upstream SHA to keep the adapter (§2.1) aligned. A job also prints the `/v1/formats` diff (added or removed formats) into the PR description, for information only; it doesn't gate the merge.
4. **Release**: semver. A libse patch bump releases as a patch, new formats as a minor, and breaking contract changes as a major (expected only for `/v2`). A tag triggers the multi-arch build, the smoke test on both architectures, the GHCR push and GitHub release notes, which include the libse version and the format diff.
5. **Upstream loop**: bugs we hit are reported or fixed upstream with a minimal sample file. Any temporary workaround in our adapter carries a `// UPSTREAM: #NNNN` marker and is removed once that version ships.

## 9. Testing

Scope: **our layer only**. libse's parsing, writing and operation logic is trusted upstream code and is not re-tested here.

| Area | What | Tooling |
|---|---|---|
| Unit: profiles | JSON parsing, schema errors with JSON pointers and hints, `extends` merge (objects deep-merge, arrays replace, `null` clears), cycles and depth limit, request-override precedence, built-in presets load and validate, mounted folder (valid, invalid, shadowing a built-in, hot reload on change, unreadable file, non-JSON file), alias normalisation | xUnit |
| API edge cases | ZIP input rejection, multiple-target JSON, encodings (UTF-8 with or without BOM, UTF-16 LE/BE, windows-1252 fallback, `utf-8-no-bom` output), empty body, garbage bytes, oversized body (413), unknown or unsupported format (422 with hint), unknown preset (404), wrong media type (415), `Content-Disposition` with non-ASCII names, response headers present | xUnit + `WebApplicationFactory` |
| Light hostile input | XXE and DTD payload, billion laughs, ZIP rejection, a pathological `MultipleReplace` regex, very long line. Each must give the documented 4xx within the time budget, and nothing may be written to disk. | xUnit, about 10 fixed fixtures |
| Smoke corpus | About 15–20 real sample files (SRT, VTT, ASS/SSA, TTML/DFXP, SBV, SAMI, EBU STL, PAC, MicroDVD, plain text, a few non-UTF-8). Each converts through 2–3 presets without error and produces a non-empty, re-detectable output with the expected cue count. Output is checked for sanity, not compared byte for byte. Wauter will supply the real files. Until then, placeholders come from libse's test resources. | xUnit theory over `tests/smoke/` |
| Auth | Open mode when no keys are set, 401 with a wrong or missing key, both keys accepted during rotation (file reload), health and docs left open | xUnit |
| Concurrency / queue | N parallel requests with conflicting `rules` give the same outputs as serial runs (proves the single worker and settings restore). Full queue → `503` + `Retry-After`. Timeout → `504` and the worker recovers. A cancelled queued request never runs. | xUnit + `WebApplicationFactory`, fake slow operation |
| OpenAPI contract | The generated `/openapi/v1.json` is diffed against the committed `openapi.v1.json` (oasdiff, breaking changes fail CI), and example responses are validated against the schema | oasdiff |
| Docker smoke | A buildx image per architecture (amd64 natively, arm64 under QEMU). It boots, reaches `/readyz`, converts 3 corpus files, checks auth on and off, runs the Postman collection with newman, reports exact compressed/uncompressed sizes for the shipped image and the `-chiseled-extra` variant, and enforces the per-arch size budget | GitHub Actions matrix |

**CI gates (required for merge):** build with warnings as errors, all of the above, `dotnet list package --vulnerable`, and a Trivy image scan (high or critical findings fail). There are no nightly jobs.

## 10. MCP server: `subtitleedit-converter-mcp`

- **Stack**: TypeScript, Node (see package engines), official SDK `@modelcontextprotocol/sdk`, `zod` schemas. It is a thin client over the HTTP API with no conversion logic of its own.
- **Transports**: stdio (`npx subtitleedit-converter-mcp`) and Streamable HTTP (`subtitleedit-converter-mcp --http :3000`, endpoint `/mcp`, stateless mode).
- **Config**: `SUBTITLE_API_URL` (required; also `--api-url`), `SUBTITLE_API_KEY` (optional bearer key, sent as `Authorization: Bearer`), `SUBTITLE_MCP_MAX_BYTES`, `SUBTITLE_MCP_TIMEOUT_MS`. In HTTP mode, an incoming `Authorization` header is passed through to the API unchanged and takes precedence over the configured key. The MCP server stores no keys and logs none. The tool descriptions are regenerated from the API's OpenAPI at build time so they don't drift.

| Tool | Maps to | Input → output |
|---|---|---|
| `list_formats` | `GET /v1/formats` | `{query?}` → compact list (id, name, ext, read/write) |
| `list_presets` | `GET /v1/profiles` | `{}` → name, description, target, valid, and errors |
| `get_profile` | `GET /v1/profiles/{name}` | `{name}` → resolved profile JSON |
| `detect_format` | `POST /v1/detect` | `{content, fileName?, base64?}` → format, encoding, language |
| `convert_subtitle` | `POST /v1/convert/json` | `{content, fileName?, preset?, profile?, target?}` → text content, plus warnings. Large outputs are returned as an embedded resource. |
| `validate_profile` | `POST /v1/profiles/validate` | `{profile}` → valid, errors with hints |

Errors are surfaced as `isError: true` with the ProblemDetails `code`, `hint` and `requestId`, so the agent can correct itself. The server is published to npm and GHCR. Tests use a mocked API with `msw` and a contract test against a real container in CI.

## 11. Roadmap

| Phase | Scope | Exit criteria |
|---|---|---|
| **Text API** | §4 endpoints, option A adapter, queue, optional API keys, safeguards, built-in presets, multi-arch image | All CI gates green; smoke corpus passes with the real sample files |
| **Presets and runtime controls** | Settings-hash fast path (no lock), `MultipleReplace` import of SE `.template`/`.csv`, OpenTelemetry traces, more smoke samples, the upstream PRs (format registration, explicit settings, `libseconv`) | p95 under 50 ms for 1 k cues |
| **MCP server** | `subtitleedit-converter-mcp`, first release (§10) | Works in Claude Desktop, Cursor and an HTTP client; contract tests green |
| **MCP follow-ups** | In `subtitleedit-converter-mcp` (no separate roadmap there yet): a full `analyze_subtitle` tool (report-only diagnostics using the profile's own thresholds, with Fix Common Errors in preview mode; needs an API endpoint first); hosted hardening (`--hosted`, which refuses to start today) | Each item ships with tests and is documented in the MCP's `docs/tools.md` |
| **Agent guidance (AGENTS.md)** | A short `AGENTS.md` in both repos for coding agents: build, test and lint commands; the version-bump and CHANGELOG rule (the MCP's major.minor follows the API); keeping error codes in sync (ErrorCodes, docs/errors.md, OpenAPI, Postman); contract tests against the pinned API image; no cue text, file content or tokens in logs and warnings; no tags, releases or publishing without the owner's go-ahead | A coding agent can build, test and open a correct PR in either repo from AGENTS.md alone |
| **Public release and Docker Hub** | Contact the Subtitle Edit author (Nikolaj Olsson) before going public, to agree on naming and make it clear this is a community wrapper. Then make the repos and the GHCR package public, and publish the same multi-arch image to Docker Hub as well as GHCR from one build, and the MCP's npm package and image (its `PUBLISH_ENABLED` gate) (Hub account and namespace, access token as a GitHub Actions secret, Hub login plus a second image name in the publish job). README pull instructions for both registries, mentioning Docker Hub's anonymous pull rate limits. | Upstream author contacted; same digest on GHCR and Docker Hub for each release |
| **OCR (separate image tag)** | `:X.Y-ocr` adds Tesseract and traineddata (about +100–300 MB, depending on languages), plus image inputs (sup, VobSub). AI engines (PaddleOCR, llama.cpp vision) go in `:X.Y-ocr-ai` (GBs, possibly GPU), or are delegated to an external endpoint (Ollama or `llama-server` URL). This needs libuilogic, so either option B or C is a prerequisite. Uses the existing conversion job flow. Persisted history and cached result replay are not implemented. | The text-only image stays unchanged in size |
| **Container extraction** | MKV/MKS, MP4, TS teletext and MXF text tracks via libse `ContainerFormats`, with `GET /v1/inspect` listing tracks and `trackNumbers`/`forcedOnly` in the profile. Large uploads move to async jobs or streaming (multi-GB media → presigned URL or chunked upload) | Upload limits are configurable per endpoint |
| **Saved server profiles** | Server-side CRUD `/v1/profiles` (DB-backed) with per-key ownership, plus an audit log. Mounted and built-in presets stay read-only. SSO and tenants still come from a gateway. | Read-only presets are unaffected |

## 12. Open questions
- Will upstream accept a `libseconv` package and a registration hook? Ask the maintainer (Nikolaj Olsson) before investing in option B.
- Which licence applies to our own repos? MIT is the easy choice, and upstream's MIT notice must be retained in image labels and the NOTICE file.
- Should we support `FixCommonOcrErrors` (English Hunspell bundle) in V1.x via option B, or keep it for the OCR phase?

## 13. V1 implementation notes and deviations

Recorded while building v1 (2026-10-16). Where these conflict with earlier sections, this section wins.

| # | Area | Design said | V1 does | Why |
|---|---|---|---|---|
| 1 | libse pin (§0, §2) | Published NuGet package | Pin lives in the project file; the package version is stamped for health responses. | Bump after upstream publishes. |
| 2 | Operations adapter (§2.1) | Mirror published seconv/libse APIs | Only published APIs are used; unsupported rules are rejected. | Re-align on package updates. |
| 3 | Image size (§3) | 100–150 MB, CI fails above 170 MB | Plain `-noble-chiseled` + trimmed ICU overlay. amd64 **158.2 MiB** uncompressed / **68.2 MiB** compressed, arm64 **165.2 / 64.4 MiB** (was 172.1 / 74.8 and 179.2 / 71.1 on `-chiseled-extra`). Budgets: amd64 78 / 182 MiB, arm64 74 / 190 MiB (compressed / uncompressed). | See row 11. The rest is the ASP.NET runtime base (~124 MB) and our 17 MB app layer; going lower needs a trimmed self-contained publish on `runtime-deps` (libse's reflection over format types makes trimming risky), tracked in the roadmap. |
| 11 | ICU in the image (§3) | `-chiseled-extra` (ICU + tzdata) | `docker/icu-slim.sh` (build host, no QEMU) copies `libicuuc`/`libicui18n` 74.2 byte-for-byte from the matching `-chiseled-extra`, extracts the ICU data blob from its `libicudata`, drops what .NET never reads (charset converters, transliteration, RBNF, unit names, character names, confusables, CJK/Thai/Lao/Khmer/Burmese word dictionaries; 815 of 4083 items, 30.8 → 17.7 MB), re-links it with the target binutils under the same soname/symbol, and copies tzdata without the `right/` leap-second duplicates. All locales, collation, casing, normalization, display names and time zones stay. Overlay 24 MB vs 38 MB in `-extra`. | Leanest option that keeps globalization fully working. Rejected: **copying ICU + tzdata as-is** onto plain chiseled (≈1.5 MB saving, same data); **app-local ICU** (`Microsoft.ICU.ICU4C.Runtime` + `System.Globalization.AppLocalIcu`) ships the full ~30 MB data file per RID (no saving, a second ICU version to track); **invariant mode** breaks culture lookups outright (`CultureNotFoundException` for `tr-TR` etc.; verified). Guard: a startup `GlobalizationCheck` (Turkish/Greek casing, sv/de/ja collation, culture-aware search, number/date formats, display names, normalization, IDN, IANA time zones + Windows id mapping, code pages, libse language detection fr/de/tr) gates `/readyz`, so a bad trim, a missing library or invariant mode fails the smoke test. It already caught the plain-chiseled invariant default. The script fails the build if `icu-devtools` and the base image's ICU major differ. |
| 12 | Naming (header, §10) | Repos `subtitleedit-api` / `subtitleedit-mcp`, namespace `SubtitleEditApi`, title "Subtitle Edit API" | Renamed 2026-10-17 to `subtitleedit-converter-api` / `subtitleedit-converter-mcp`, namespace and assemblies `SubtitleEditConverterApi`, image `ghcr.io/<owner>/subtitleedit-converter-api`, title "Subtitle Edit Converter API". `/v1/...` paths and error codes are unchanged. | Make clear this is a community wrapper around libse, not an official Subtitle Edit product. GitHub redirects the old repo URLs. Nothing had been published under the old names (no tags, no images). |
| 4 | Formats (§1, §5.1) | Text formats; `ebu-stl-broadcast` preset | The first version was text only. Since the binary-formats milestone (rows 18–32), binary broadcast formats are read and written. Image-based and container formats still give `422 format-not-supported`; read-only binary targets give `422 format-not-writable`. The `ebu-stl-broadcast` preset still isn't shipped; `srt-to-ass` and `to-ttml` were added. | Images and containers need OCR or extraction (roadmap). |
| 5 | `formatOptions` (§5) | `assa`, `pac`, `ebuStl`, `plainText` | `assa`, `ebuStl`, `pac`, `cavena890`, `cheetah`; no `plainText` | `plainText` needs libuilogic. Unknown keys are rejected with a hint. |
| 8 | XML guard (§6.2) | Pre-screen XML-looking input | Any input starting with `<` that contains a DTD/`<!ENTITY` is rejected as `unsafe-xml`, even if libse wouldn't parse it as XML (e.g. a SAMI file with `<!DOCTYPE html>`). | Conservative; relax if real files hit it. |
| 9 | Plugin formats (§2.2) | `SubtitleEditConverterApi.Formats` assembly | Not created (no format needed yet). `FormatInfo.source` is in the contract (`"libse"`) so `"plugin"` can be added later. | YAGNI. |
| 10 | OpenAPI contract (§9) | `oasdiff` breaking-change gate | xUnit snapshot: generated `/openapi/v1.json` must equal committed `docs/openapi.v1.json` (`UPDATE_OPENAPI=1 dotnet test` to refresh). | Stricter (any drift fails) and needs no extra tool. |
| 11 | Request bodies in OpenAPI | n/a | Described with an operation transformer, not `.Accepts()` | `.Accepts()` is also a routing constraint: a wrong Content-Type would 404 instead of our `415`. |
| 12 | Metrics (§4.7) | Prometheus, histograms | Hand-written Prometheus text: queue depth/capacity, completed jobs, sums of run/wait seconds, `rejected_total{reason}` | No extra dependency; histograms/OpenTelemetry are v1.x. |
| 13 | API key reload (§6.1) | File watcher | `API_KEYS_FILE` mtime checked on each authenticated request | Works with Kubernetes Secret symlink swaps, where watchers are unreliable. Setting `API_KEYS_FILE` turns auth on even if the file is empty or missing (fail closed). |
| 14 | Queue (§2.4) | Single worker, recycle on timeout | As designed. Retry-After = (queued + 1) × max(rolling average, running job's elapsed). Known limit: after a timeout the abandoned thread keeps running until libse returns, so it can overlap the new worker for that long; the abandoned worker no longer resets settings. `/readyz` also fails while the queue is full. | libse calls can't be cancelled. |
| 15 | Logs (§7.2) | JSON logs | JSON console logs, one `request …` record per request with `requestId` scope, `route`, `status`, `durationMs`, `queueWaitMs`, `bytesIn/Out`, `format`, `encoding`, `cues`, `preset`, `operations`, `keyName`, `file` (hash + extension), `code`. No OpenTelemetry yet. | v1.x per roadmap. |
| 16 | MultipleReplace | `rules: [{find, replace, mode}]` | Same; at most 500 rules; regex uses `NonBacktracking` with a 100 ms timeout, falling back to the backtracking engine (still 100 ms) for patterns NonBacktracking can't run (lookarounds, backreferences). | |
| 17 | Validate endpoint (§4.6) | Never 4xx for an invalid profile | Same; an unknown `extends` comes back as `valid:false` with the error. A profile without `target.format` is valid with a warning (it may be a base preset). | |
| 18 | Binary detection (milestone §3) | IsMine in registry order, Cavena last, cues > 0 | Same, plus a **plausibility check** in auto mode (≥ 90 % of decoded text is letters, digits or common punctuation), because libse's Cavena 890 IsMine accepts random `.890` garbage. Detection writes the upload once to a private temp file and renames it to each candidate's extension (most binary IsMine checks look at the extension). `/v1/detect` returns up to 5 candidates. | Stricter than the contract; an explicit `source.format` skips the check. |
| 19 | Binary sniff (milestone §3) | NUL/control bytes or binary extension | Same; a BOM always means text, and a text file with a binary-looking extension (MicroDVD `.sub`, SRT named `.stl`) falls back to text detection when no binary reader takes it. | |
| 20 | `detected.codePage` (milestone §3) | string or absent | EBU: the GSI CPN (`"850"`); PAC: `"pac-<name>"`; others absent. `formatDetails`: EBU `{characterTable, frameRate, displayStandard, languageCode}`, PAC `{codePageConfidence: probed|forced, probedLanguage}`; frame-based sources also `{frameRate, frameRateSource}`, **Cavena `{languageId, characterSet}`** (not in the contract; added so the detected script is visible). | |
| 21 | Output encoding names | not specified | `cpNNN` (EBU), `pac-<name>`, `pac-unicode`, `cavena-<script>`, `cheetah-ascii`, `cheetah-latin`, `capmaker-latin` | Invented here; reported in `outputs[].encoding`. |
| 22 | PAC write code page | `formatOptions.pac.codePage`, default latin | Option, else **the source PAC's code page** (PAC→PAC keeps Greek Greek), else latin. | Avoids silently re-encoding. |
| 23 | EBU→EBU header | Reuse the source GSI unless `headerFromSource:false` | Same; explicit options are applied on top. libse re-reads `subtitle.Header` when it's a GSI block, so the source DFC wins: a 29.97 source re-serialises as `STL30.01` (libse's `FrameRateFromSaveDialog` isn't kept in the GSI). **This is a `frame-rate-changed` warning** whenever the written DFC implies (libse's DFC getter: STL23/24/25/29/30/35/48/50/60, anything else 30; STL23 taken as 23.976) a rate more than 0.01 fps away from the intended one: `frameRate`, else `target.fps`, else the source's (EBU: `readFrameRate` or its DFC; others: `source.fps`). Covers a 29.97 EBU source rewritten with defaults (read with `readFrameRate: 29.97`), explicit `frameRate`/`target.fps` 29.97 (always `STL30.01`) and `headerFromSource:false` dropping a 30 fps source to the default `STL25.01`. EBU Tech 3264 itself only defines `STL25.01` and `STL30.01`, so 29.97 has no code of its own. TTI times are `HH:MM:SS:FF`, so the mismatch reinterprets the frame part of each time (under one frame); there is no cumulative drift inside libse. | EBU limitation (no 29.97 code) plus libse not storing the save-dialog rate; warned, not fixed. |
| 24 | EBU language | ISO 639-1 or EBU hex, `auto` default | Same; the ISO→EBU map is libse's `AutoDetectLanguageCode` table (sq … tr, `nb`/`no` → `1E`), unknown or undetected → `09` (English). | |
| 25 | Re-read verification (milestone §4.3; structured verification) | Re-read and compare | The output is re-read with the same reader and compared with the cues meant to be written (tags and whitespace normalised). Result: `verification` per binary output (verdict, timing, replaced code points, truncated/missing/zero-length cues; cue numbers and code points only, never text) and the warnings `characters-replaced` (with `details`, a remedy `hint` and `details.remedy`: option, allowed values, tested candidates for PAC, `suggested` only when exactly one fits), `text-truncated`, `frame-quantised`, `lossy-format`. Differing cue counts are matched by start time. A reader that throws on our own output gives `unverified` + `verification-failed` (earlier versions stayed silent). | Client feedback 2026-10-17: users wrote their own round-trip scripts. |
| 26 | Profile schema | `codePage` name or number | `StrOrIntSpec`: rendered as `oneOf: [{enum}, {integer, min, max}]` in `/v1/schemas/profile.json`. | |
| 27 | Body caps (milestone §3.1) | multipart `MAX_UPLOAD+256 KiB`, JSON `4*ceil(n/3)+PROFILE_MAX+64 KiB` | Same. Kestrel's global cap is the larger of the two; a middleware sets the per-request cap from the route and content type. | TestServer doesn't enforce Kestrel limits, so the JSON cap was verified against the real host. |
| 28 | OpenAPI problems (MCP answers A1/A2) | n/a | One `ApiProblem` schema (with the `code` enum) on every error response; Optional `bearer` security on `/v1`, 401/429 on every `/v1` operation, 503/504 on queued ones, 400 on GETs with parameters, documented headers, `X-Request-Id` request header. A contract test keeps `ErrorCodes`, `errors.md` and the enum in sync. | |
| 29 | Binding errors (A5) | n/a | `ThrowOnBadRequest`: wrong JSON types → `400 invalid-request` with a pointer to the field (`/contentEncoding`), bad query values → pointer `/query/<name>`, wrong Content-Type on `/v1/convert/json` → `415` (was a 404 because the JSON body makes routing skip the endpoint). | |
| 31 | Timeout phase (A11) | n/a | `504 conversion-timeout` has `phase: "queue"` (never started, retryable) or `"running"`. | |
| 32 | Temp files (milestone §4.2) | Private dir, delete after each job | `se-bin-<pid>-*` (0700) under the system temp dir, a `job-<guid>` subdir per job, files `<guid><ext>` (0600) with an allowlisted extension. Deleted in the worker's `finally`, on cancel, on queue-wait timeout and on running timeout (then old job dirs are swept after the worker restarts); at startup, dirs of dead processes are removed. | An abandoned libse call may still hold its file until it returns; the sweep catches it. |
| 34 | PAC / FPC ambiguity (Wauter, 2026-10-17) | Pick PAC | PAC and PAC Unicode share their header. Auto-detection that finds bytes valid (and plausible) as both, with no `.pac` / `.fpc` name to decide, fails with `422 ambiguous-format` and `candidates`. An explicit `source.format` or the right extension decides. | Ambiguity is rejected, never guessed. |
| 35 | Input character set (Wauter, 2026-11-06) | libse guesses (PAC: Latin; text: letter statistics) | PAC `inputCodePage` auto and non-UTF-8 text are probed: every candidate decodes the bytes and counts only if its text has dictionary words of a language that code page serves (libse's word lists plus a few short lists for Greek, Hebrew, Arabic, Cyrillic, CJK and Thai). A candidate needs at least max(1, units/30) hits and more than a third of the best score; identical texts are one answer. One answer is used (`probed`); different answers give `422 ambiguous-encoding` with `candidates`. PAC with no answer is `422` too; text with no answer falls back to libse's heuristic with `encoding-guessed`, so short Latin files keep working. | Wrong-script text is worse than a rejected file; letter statistics recognise mojibake as Latin. |
| 36 | `target.fps` semantics (Wauter, 2026-11-06) | Rescale from libse's current rate | `target.fps` is the output rate. Times are rescaled only from `source.fps` or a rate the file states or implies (EBU DFC, SCC drop-frame or high frame numbers → 29.97). Time-based sources keep wall-clock times; frame-based sources without a rate are read at 23.976 with `frame-rate-assumed`. | A WebVTT→SCC at 29.97 must still end at the same time. |
| 37 | Header defaults and sidecars (Wauter, 2026-11-06) | Silent | Identity fields a binary writer fills without being asked are reported as `format-defaults-applied` from the bytes written. An output written without `sidecar` deletes a stale `{output}.json`; filesystem outputs are `0644`. | A MAM must not ingest `No Title`/`USA` or an old sidecar as real metadata. |

## Storage and job contract

Current URI, wait, metadata-only job and sidecar behaviour is in [usage](usage.md#job-metadata); effective configuration is in [configuration](configuration.md#jobs-and-storage). Single-file calls replace all archive and folder batch processing.
