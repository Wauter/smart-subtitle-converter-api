# subtitleedit-api

HTTP service for converting subtitle files between text formats. It wraps
[Subtitle Edit](https://github.com/SubtitleEdit/subtitleedit)'s `libse` library
(pinned NuGet package, currently **5.1.0**) in an ASP.NET Core (.NET 10) API and ships as a
multi-arch (amd64/arm64) container.

- Design: [docs/design.md](docs/design.md) (section 13 lists where v1 differs from the design)
- Error codes: [docs/errors.md](docs/errors.md)
- Roadmap: [docs/roadmap.md](docs/roadmap.md)
- OpenAPI (committed contract): [docs/openapi.v1.json](docs/openapi.v1.json), live at `/openapi/v1.json`, interactive docs at `/docs`
- MCP server (planned): [Wauter/subtitleedit-mcp](https://github.com/Wauter/subtitleedit-mcp)

## Quickstart

```bash
# from source (needs the .NET 10 SDK)
dotnet run --project src/SubtitleEditApi        # listens on http://localhost:5000 (ASPNETCORE_HTTP_PORTS to change)

# or with Docker
docker build -t subtitleedit-api .
docker run --rm -p 8080:8080 -v "$PWD/profiles:/profiles:ro" subtitleedit-api
# or: docker compose up -d   (see docker-compose.yml; image ghcr.io/wauter/subtitleedit-api once a v* tag is published)
```

Open <http://localhost:8080/docs> for the interactive API reference.

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

# zip in -> zip out (one manifest entry per file; a bad file doesn't fail the batch unless failFast=true)
curl -F file=@season1.zip -F target=vtt localhost:8080/v1/convert -OJ
curl -F file=@season1.zip -F target=vtt -F failFast=true localhost:8080/v1/convert -OJ

# JSON in/out (for agents and scripts)
curl localhost:8080/v1/convert/json -H 'content-type: application/json' \
     -d '{"content":"1\n00:00:01,000 --> 00:00:02,000\nHello\n","fileName":"a.srt","target":"vtt"}'

# what is this file?
curl -F file=@mystery.txt localhost:8080/v1/detect

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
`X-Cue-Count`, `X-Cue-Count-In`, `X-Warnings-Count`, `X-Warnings`, `X-Profile`, `X-Libse-Version`
(zip input adds `X-Entries-Ok` / `X-Entries-Failed`).

Errors are `application/problem+json` with a stable `code`, a `hint` and the `requestId`, which
also appears in the logs:

```json
{ "type": ".../docs/errors.md#invalid-profile", "title": "Invalid profile", "status": 400,
  "code": "invalid-profile", "detail": "The profile has 1 error(s); first at '/target/format': Unknown format 'vtt2'.",
  "hint": "Did you mean 'webvtt'? See GET /v1/formats.", "requestId": "01M52D3QQQP64E77FSF5QXGHJJ",
  "errors": [ { "pointer": "/target/format", "message": "Unknown format 'vtt2'.", "hint": "Did you mean 'webvtt'?" } ] }
```

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
inline profile, multi-output zip, zip in/out, JSON in/out) and the error cases (unsupported format,
bad profile, unknown preset, wrong media type, missing/wrong API key). Each request has test scripts
for status codes and key headers (`X-Request-Id`, `X-Detected-*`, `X-Cue-Count`, `X-Profile`,
`X-Entries-*`, problem+json `code`).

- Import `postman/subtitleedit-api.postman_collection.json` and `postman/subtitleedit-api.postman_environment.json`.
- Environment: `baseUrl` (default `http://localhost:8080`), `apiKey` (leave empty for an open server;
  the auth-failure tests are then skipped).
- File parts point at repo files (`tests/smoke/*`, `postman/fixtures/batch.zip`) by relative path: in
  Postman set *Settings → General → Working directory* to your checkout.

```bash
npx newman run postman/subtitleedit-api.postman_collection.json -e postman/subtitleedit-api.postman_environment.json \
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
| `MAX_UPLOAD_BYTES` | `10485760` | Request body limit |
| `QUEUE_CAPACITY` | `32` | Waiting conversions before `503 queue-full` |
| `CONVERSION_TIMEOUT_SECONDS` | `30` | Queue-wait budget and run budget (each) |
| `ZIP_MAX_ENTRIES` / `ZIP_MAX_UNPACKED_BYTES` | `100` / `52428800` | Zip guards (also max ratio 100:1) |
| `MAX_CUES` / `MAX_LINE_CHARS` | `200000` / `10000` | Input limits |
| `RATE_LIMIT_PER_MINUTE` | off | Per key name, or per client IP when auth is off |
| `CORS_ORIGINS` | off | Comma-separated allow-list |
| `KNOWN_PROXIES` | unset | IPs whose `X-Forwarded-*` headers are trusted |
| `DOCS_ENABLED` | `true` | `/docs` (Scalar) |
| `METRICS_ENABLED` / `METRICS_PORT` | off | Prometheus text at `/metrics`, optionally only on that port |

Health: `GET /healthz` (liveness), `GET /readyz` (self-test, worker health, queue). Both stay open
when auth is on. The image has no shell or curl, so probe them from the orchestrator.

## Development

```bash
dotnet test                         # tests for our layer only (libse itself is trusted upstream)
UPDATE_OPENAPI=1 dotnet test        # after an intended API change: refresh docs/openapi.v1.json
scripts/smoke.sh subtitleedit-api   # Docker smoke test against a built image
docker buildx build --platform linux/arm64 -o type=oci,dest=img.tar . && scripts/image-size.py img.tar slim linux/arm64
```

Releases: push a `vX.Y.Z` tag. CI builds `linux/amd64` + `linux/arm64`, runs the smoke test and newman on
both, checks the size budget, scans with Trivy, and pushes `ghcr.io/wauter/subtitleedit-api:{X.Y.Z,X.Y,latest,sha-…}`.

## License

MIT, see [LICENSE](LICENSE). Built on `libse` from Subtitle Edit by Nikolaj Olsson (MIT).
