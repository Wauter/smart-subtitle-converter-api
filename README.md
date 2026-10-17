# Subtitle Edit Converter API

`subtitleedit-converter-api` is a community wrapper around Subtitle Edit's `libse`, not an official
Subtitle Edit project: an HTTP service for converting subtitle files between text formats and binary
broadcast formats (EBU STL, PAC, Cavena 890, ...). It wraps
[Subtitle Edit](https://github.com/SubtitleEdit/subtitleedit)'s `libse` library
(pinned NuGet package, currently **5.1.0**) in an ASP.NET Core (.NET 10) API and ships as a
multi-arch (amd64/arm64) container.

- Design: [docs/design.md](docs/design.md) (section 13 lists where v1 differs from the design)
- Error codes: [docs/errors.md](docs/errors.md)
- Roadmap: [docs/roadmap.md](docs/roadmap.md)
- Changelog: [CHANGELOG.md](CHANGELOG.md) (current version **1.1.0**, reported by `/healthz`); versioning rule: [CONTRIBUTING.md](CONTRIBUTING.md)
- OpenAPI (committed contract): [docs/openapi.v1.json](docs/openapi.v1.json), live at `/openapi/v1.json`, interactive docs at `/docs`
- MCP server (planned): [Wauter/subtitleedit-converter-mcp](https://github.com/Wauter/subtitleedit-converter-mcp)

## Quickstart

```bash
# from source (needs the .NET 10 SDK)
dotnet run --project src/SubtitleEditConverterApi        # listens on http://localhost:5000 (ASPNETCORE_HTTP_PORTS to change)

# or with Docker
docker build -t subtitleedit-converter-api .
docker run --rm -p 8080:8080 -v "$PWD/profiles:/profiles:ro" subtitleedit-converter-api
# or with Compose (see docker-compose.yml: read-only rootfs, tmpfs /tmp, ./profiles mounted)
docker compose up --build -d          # build locally from this checkout
# ...or pull the private :edge image that CI pushes from main (token needs read:packages)
echo "$GHCR_TOKEN" | docker login ghcr.io -u <github-user> --password-stdin
docker compose up -d                  # pulls ghcr.io/wauter/subtitleedit-converter-api:edge
```

Open <http://localhost:8080/docs> for the interactive API reference.

### Pulling the `:edge` image

Every green push to `main` publishes `ghcr.io/wauter/subtitleedit-converter-api:edge` and
`:sha-<short commit>` (linux/amd64 + linux/arm64, with SBOM and provenance). The package is
**private**: create a GitHub personal access token (classic) with only the `read:packages` scope, then

```bash
echo "$GHCR_TOKEN" | docker login ghcr.io -u <github-user> --password-stdin
docker pull ghcr.io/wauter/subtitleedit-converter-api:edge
```

`:edge` moves with `main`; `:latest` only moves on releases. Pin `:sha-…` (or a version once releases
exist) for anything you don't want to change under you.

The image is plain `aspnet:10.0-noble-chiseled` plus a trimmed ICU/tzdata overlay (full globalization,
about 7 MiB smaller compressed than `-chiseled-extra`; see design §13). Sizes are measured on every CI
run (job summary). `--build-arg RUNTIME_BASE=base-extra` builds on the stock `-chiseled-extra` base instead.

## Examples

```bash
# SRT -> WebVTT; -OJ saves using the server's file name (movie.vtt)
curl -F file=@movie.srt -F target=webvtt localhost:8080/v1/convert -OJ

# a built-in preset
curl -F file=@movie.srt -F preset=netflix-en-vtt localhost:8080/v1/convert -OJ

# preset + overrides (objects merge, arrays replace, null clears)
curl -F file=@movie.srt -F preset=netflix-en-vtt \
     -F 'profile={"rules":{"maxLineLength":37},"timing":{"offsetMs":-1500}}' localhost:8080/v1/convert -OJ

# TTML -> SRT, and SRT -> ASS
curl -F file=@movie.ttml -F target=srt localhost:8080/v1/convert -OJ
curl -F file=@movie.srt  -F preset=srt-to-ass localhost:8080/v1/convert -OJ

# several outputs at once -> zip with manifest.json
curl -F file=@movie.srt -F target=vtt -F 'profile={"target":{"additional":["ass","ttml"]}}' localhost:8080/v1/convert -OJ

# zip in -> zip out (one manifest entry per file; a bad file doesn't fail the batch unless failFast=true).
# Entries with the same folder + name (ep1.srt and EP1.vtt) are rejected up front: 422 ambiguous-batch-names.
curl -F file=@season1.zip -F target=vtt localhost:8080/v1/convert -OJ
curl -F file=@season1.zip -F target=vtt -F failFast=true localhost:8080/v1/convert -OJ

# JSON in/out (for agents and scripts)
curl localhost:8080/v1/convert/json -H 'content-type: application/json' \
     -d '{"content":"1\n00:00:01,000 --> 00:00:02,000\nHello\n","fileName":"a.srt","target":"vtt"}'

# what is this file? (text or binary; binary adds codePage/formatDetails and up to 5 candidates)
curl -F file=@mystery.txt localhost:8080/v1/detect

# server limits (body caps, zip guards, queue depth, auth required?) before you upload
curl localhost:8080/v1/limits

# formats, presets, schema
curl 'localhost:8080/v1/formats?q=vtt'
curl localhost:8080/v1/profiles                 # built-in + mounted, invalid ones listed with errors
curl localhost:8080/v1/profiles/netflix-en-vtt  # raw + resolved (extends flattened)
curl localhost:8080/v1/profiles/validate -H 'content-type: application/json' -d @my-profile.json
curl localhost:8080/v1/schemas/profile.json     # JSON Schema for editors and agents

# with API keys enabled
curl -H "Authorization: Bearer $KEY" -F file=@movie.srt -F target=vtt localhost:8080/v1/convert -OJ
```

Response headers on conversions: `X-Request-Id`, `X-Detected-Format`, `X-Detected-Encoding`,
`X-Detected-Code-Page` (binary input), `X-Output-Kind` (`text`/`binary`), `X-Cue-Count`, `X-Cue-Count-In`,
`X-Warnings-Count`, `X-Warnings`, `X-Profile`, `X-Libse-Version` (zip input adds `X-Entries-Ok` /
`X-Entries-Failed` / `X-Entries-Skipped`). Send your own `X-Request-Id` (`[A-Za-z0-9._-]{1,64}`) to
correlate requests; anything else is replaced by a fresh id.

Errors are `application/problem+json` with a stable `code`, a `hint` and the `requestId`, which
also appears in the logs:

```json
{ "type": ".../docs/errors.md#invalid-profile", "title": "Invalid profile", "status": 400,
  "code": "invalid-profile", "detail": "The profile has 1 error(s); first at '/target/format': Unknown format 'vtt2'.",
  "hint": "Did you mean 'webvtt'? See GET /v1/formats.", "requestId": "01M52D3QQQP64E77FSF5QXGHJJ",
  "errors": [ { "pointer": "/target/format", "message": "Unknown format 'vtt2'.", "hint": "Did you mean 'webvtt'?" } ] }
```

## Binary broadcast formats

Read and write: **EBU STL** (`ebustl`, `.stl`), **PAC** (`pac`), **FPC** (`fpc`), **Cavena 890**
(`cavena890`, `.890`), **CapMaker Plus** (`capmakerplus`), **Cheetah CAP** (`cheetahcaption`,
`cheetahcaptionold`; lossy) and more. Read-only: Avid STL, Ayato, Spruce STL and others (as a target
they give `422 format-not-writable`). Image-based and container formats (Blu-ray sup, VobSub,
Matroska, MP4, ...) are listed with `kind` but give `422 format-not-supported`.
`GET /v1/formats?kind=binary&writable=true` has the current list.

```bash
# SRT -> EBU STL (25 fps, CPN 850, language auto-detected), with GSI fields
curl -F file=@movie.srt -F target=ebustl \
     -F 'profile={"formatOptions":{"ebuStl":{"frameRate":25,"originalProgrammeTitle":"My Film","language":"en"}}}' \
     localhost:8080/v1/convert -OJ
# EBU STL -> SRT (detected by content; X-Detected-Code-Page: 850)
curl -F file=@movie.stl -F target=srt localhost:8080/v1/convert -OJ
# PAC with an explicit code page (auto-detection is weak for non-Latin scripts)
curl -F file=@movie.pac -F target=srt -F 'profile={"source":{"format":"pac"},"formatOptions":{"pac":{"inputCodePage":"greek"}}}' localhost:8080/v1/convert -OJ
# JSON: binary content must be base64; binary outputs always come back base64
curl localhost:8080/v1/convert/json -H 'content-type: application/json' \
     -d "{\"content\":\"$(base64 -w0 movie.stl)\",\"contentEncoding\":\"base64\",\"target\":\"srt\"}"
```

- Detection runs binary sniffing first (NUL/control bytes or a binary extension), then tries the
  binary readers with Cavena 890 last and a plausibility check, so a text file named `.stl`/`.sub`
  stays text. When in doubt, set `profile.source.format`: wrong bytes then give `422 invalid-binary-input`.
- Per-format options live in `profile.formatOptions.{ebuStl,pac,cavena890,cheetah}` (see
  `/v1/schemas/profile.json`); bad values give `400 format-option-invalid`, a block that doesn't apply
  to the source/target gives a `format-option-ignored` warning.
- Lossy writes are never silent: `characters-replaced` (with a count), `frame-quantised`, `lossy-format`.
- EBU STL frame rates: the GSI Disk Format Code is the only place the rate is stored, and EBU Tech 3264
  only defines `STL25.01` and `STL30.01`, so 29.97 fps is written as `STL30.01` and read back as 30.
  Whenever the written code implies a different rate than the requested (`frameRate`/`target.fps`) or
  source rate, the response carries a `frame-rate-changed` warning (EBU→EBU reuses the source GSI by
  default; set `formatOptions.ebuStl.frameRate` or `headerFromSource:false` to change that).
- Binary files are handled in a private `0700` temp dir with random names, deleted after every job,
  including timeouts and worker restarts, and swept at startup.

## Presets

Built in: `srt-to-vtt`, `clean-srt`, `netflix-en-vtt`, `pal-to-film`, `srt-to-ass`, `to-ttml`.
Add your own as `*.json` files in the folder mounted at `PROFILES_DIR` (default `/profiles`); the
file name is the preset name (`[a-z0-9-]+`). The folder is re-read when it changes, no restart.
A mounted preset with the same name as a built-in one replaces it (`shadows` in `GET /v1/profiles`).
See [profiles/example-house-style.json](profiles/example-house-style.json) and the schema at
`/v1/schemas/profile.json`.

Operations (run in array order; names and behaviour follow `seconv`): `RemoveTextForHI`,
`FixCommonErrors` (`rules`, `language`), `RemoveFormatting` (`rules`), `MergeSameTexts`,
`MergeSameTimeCodes`, `MergeShortLines`, `SplitLongLines`, `BalanceLines`, `RedoCasing`,
`RemoveLineBreaks`, `ApplyDurationLimits`, `BeautifyTimeCodes`, `ConvertColorsToDialog`,
`FixRtlViaUnicodeChars`, `RemoveUnicodeControlChars`, `ReverseRtlStartEnd`, `DeleteFirst`/`DeleteLast`
(`count`), `DeleteContains` (`text`), `MultipleReplace` (`rules: [{find, replace, mode: text|caseSensitive|regex}]`).

## Postman / newman

[`postman/`](postman) has a v2.1 collection covering every endpoint with the examples above
(health, formats, detect, profiles list/get/validate, schema, convert with target, preset, overrides,
inline profile, multi-output zip, zip in/out, JSON in/out, limits, binary formats: SRT→STL, STL→SRT,
PAC with a code page, a mixed text/binary zip) and the error cases (unsupported format, read-only
target, truncated STL, bad profile, unknown preset, wrong media type, missing/wrong API key). Each request has test scripts
for status codes and key headers (`X-Request-Id`, `X-Detected-*`, `X-Cue-Count`, `X-Profile`,
`X-Entries-*`, problem+json `code`).

- Import `postman/subtitleedit-converter-api.postman_collection.json` and `postman/subtitleedit-converter-api.postman_environment.json`.
- Environment: `baseUrl` (default `http://localhost:8080`), `apiKey` (leave empty for an open server;
  the auth-failure tests are then skipped).
- File parts point at repo files (`tests/smoke/*`, `postman/fixtures/*`) by relative path: in
  Postman set *Settings → General → Working directory* to your checkout.

```bash
npx newman run postman/subtitleedit-converter-api.postman_collection.json -e postman/subtitleedit-converter-api.postman_environment.json \
    --working-dir . --env-var baseUrl=http://localhost:8080 --env-var apiKey=$KEY
```

CI runs it against the built container on amd64 and arm64.

## Configuration

| Env | Default | |
|---|---|---|
| `ASPNETCORE_HTTP_PORTS` | `8080` (image) | Listen port |
| `PROFILES_DIR` | `/profiles` | Mounted presets folder |
| `API_KEYS` | unset | Comma-separated bearer keys (`name:key` allowed) |
| `API_KEYS_FILE` | unset | One key per line, `#` comments, `name:key`; reloaded on change |
| `ERRORS_VERBOSE` | `true` | `false` removes stack traces and internals from errors |
| `MAX_UPLOAD_BYTES` | `10485760` | Max decoded file size. Body caps derive from it: multipart `+256 KiB`, JSON `4*ceil(n/3) + PROFILE_MAX_BYTES + 64 KiB` (see `GET /v1/limits`) |
| `PROFILE_MAX_BYTES` | `65536` | Max inline profile size (min 1024) |
| `QUEUE_CAPACITY` | `32` | Waiting conversions before `503 queue-full` |
| `CONVERSION_TIMEOUT_SECONDS` | `30` | Queue-wait budget and run budget (each) |
| `ZIP_MAX_ENTRIES` / `ZIP_MAX_UNPACKED_BYTES` / `ZIP_MAX_RATIO` | `100` / `52428800` / `100` | Zip guards |
| `MAX_CUES` / `MAX_LINE_CHARS` | `200000` / `10000` | Input limits |
| `RATE_LIMIT_PER_MINUTE` | off | Per key name, or per client IP when auth is off |
| `CORS_ORIGINS` | off | Comma-separated allow-list |
| `KNOWN_PROXIES` | unset | IPs whose `X-Forwarded-*` headers are trusted |
| `DOCS_ENABLED` | `true` | `/docs` (Scalar) |
| `METRICS_ENABLED` / `METRICS_PORT` | off | Prometheus text at `/metrics`, optionally only on that port |

Health: `GET /healthz` (liveness; `{"status":"ok","version":"1.1.0","libse":"5.1.0"}`), `GET /readyz` (self-test, worker health, queue). Both stay open
when auth is on. `GET /v1/limits` reports the effective limits (it's under `/v1`, so it needs a key
when auth is on, and it only says whether auth is required, never anything about keys). The image has no shell or curl, so probe them from the orchestrator.

## Development

```bash
dotnet test                         # tests for our layer only (libse itself is trusted upstream)
UPDATE_OPENAPI=1 dotnet test        # after an intended API change: refresh docs/openapi.v1.json
scripts/smoke.sh subtitleedit-converter-api   # Docker smoke test against a built image
docker buildx build --platform linux/arm64 -o type=oci,dest=img.tar . && scripts/image-size.py img.tar slim linux/arm64
```

Every API change that ships bumps `<Version>` in the csproj (semver: minor for additive, patch for fixes) with a CHANGELOG entry; see [CONTRIBUTING.md](CONTRIBUTING.md). A PR-only `version-check` job enforces it for `src/` changes.

CI on every PR and push: build with warnings as errors, tests, vulnerable-package check, `docker compose config`,
then `linux/amd64` + `linux/arm64` images with the smoke test and newman on both, the size budget and Trivy.
When all of that is green:

- push to `main` → `ghcr.io/wauter/subtitleedit-converter-api:{edge,sha-…}` (`latest` doesn't move);
- `vX.Y.Z` tag → `:{X.Y.Z,X.Y,latest,sha-…}` plus a GitHub Release.

Both are multi-arch with SBOM and provenance. PRs never push. The package stays private.

## License

MIT, see [LICENSE](LICENSE). Built on `libse` from Subtitle Edit by Nikolaj Olsson (MIT).
