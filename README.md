# Subtitle Edit Converter API

**Subtitle Edit's conversion engine as an HTTP service, with checks that catch what a plain conversion would let
slip.**

> **Built on Subtitle Edit.** All format reading and writing is done by `libse`, the library behind
> [Subtitle Edit](https://github.com/SubtitleEdit/subtitleedit) by **Nikolaj Olsson** (MIT). This project is a
> community wrapper, **not an official Subtitle Edit project**.
>
> **Status: alpha.** It works and is tested, but hasn't been through real-world use yet. Expect changes, and
> please [report issues](https://github.com/Wauter/subtitleedit-converter-api/issues).

`libse` is the mature, widely used engine behind Subtitle Edit and reads and writes a long list of subtitle formats.
This API runs it in a container and adds what a server needs: a single-worker queue (libse's settings are global),
binary broadcast formats with a read-back verdict on every output, warnings that name a tested fix, and rejection
of ambiguous input instead of a guess.

## What it adds to the engine

- **Text and binary formats:** SRT, WebVTT, ASS, TTML and Subtitle Edit's other text formats, plus EBU STL, PAC,
  FPC, Cavena 890, CapMaker Plus and Cheetah CAP (`GET /v1/formats` has the live list).
- **Verification:** every binary output is read back and reported as `lossless`, `quantised`, `lossy` or
  `unverified`, with cue numbers and code points, never cue text.
- **Warnings you can act on:** lost characters come with a `remedy`; for PAC the API tests each code page and
  lists only the ones that keep every character. It never switches settings on its own.
- **No guessing:** PAC/FPC bytes without a `.pac`/`.fpc` name are rejected with `ambiguous-format` and both
  candidates, instead of being converted as the wrong format.
- **Presets and profiles:** built-in presets (`netflix-en-vtt`, `pal-to-film`, `clean-srt`, …), your own JSON
  presets in a mounted folder (picked up without a restart), and Subtitle Edit operations such as Fix Common Errors.
- **One job at a time:** libse uses global settings, so one worker runs one job at a time from a bounded queue;
  settings are reset after every job, and a full queue answers `503` with `Retry-After`.
- **Detection:** `/v1/detect` names format, encoding and code page.
- **Container defaults:** amd64/arm64, chiseled ASP.NET Core base with no shell, a non-root user, and a
  reference Compose that adds a read-only root filesystem, dropped capabilities and a `127.0.0.1`-only port.
  API keys and rate limits are optional.
- **Agent-friendly:** a JSON endpoint, an OpenAPI contract, a JSON Schema for profiles, and stable error codes with
  hints. The companion [MCP server](https://github.com/Wauter/subtitleedit-converter-mcp) lets Claude and other AI
  agents use it directly.

## Quick start

With Docker:

```bash
docker run -d --name subtitleedit-converter -p 127.0.0.1:4041:8080 ghcr.io/wauter/subtitleedit-converter-api:latest
curl localhost:4041/healthz   # {"status":"ok","version":"…","libse":"…"}
```

Then convert something:

```bash
curl -F file=@movie.srt -F target=webvtt localhost:4041/v1/convert -OJ   # saves movie.vtt
```

Open <http://localhost:4041/docs> for the interactive API reference. Other ways to run it (Docker Compose,
building from source, image tags): [docs/deployment.md](docs/deployment.md).

## A few more examples

```bash
# a built-in preset, with overrides
curl -F file=@movie.srt -F preset=netflix-en-vtt \
     -F 'profile={"rules":{"maxLineLength":37}}' localhost:4041/v1/convert -OJ

# SRT -> EBU STL at 25 fps
curl -F file=@movie.srt -F target=ebustl \
     -F 'profile={"formatOptions":{"ebuStl":{"frameRate":25}}}' localhost:4041/v1/convert -OJ

# what is this file?
curl -F file=@mystery.txt localhost:4041/v1/detect
```

Errors are `application/problem+json` with a stable `code` and a `hint`:

```json
{ "status": 400, "code": "invalid-profile",
  "detail": "The profile has 1 error(s); first at '/target/format': Unknown format 'vtt2'.",
  "hint": "Did you mean 'webvtt'? See GET /v1/formats." }
```

More in [docs/usage.md](docs/usage.md).

## Documentation

| Read this | When you want to |
|---|---|
| [docs/usage.md](docs/usage.md) | See every endpoint with curl examples, the JSON endpoint and response headers |
| [docs/formats.md](docs/formats.md) | Work with binary formats: detection, code pages, frame rates, verification |
| [docs/profiles.md](docs/profiles.md) | Use presets, write your own profiles, chain operations |
| [docs/configuration.md](docs/configuration.md) | Change ports, limits, metrics or health checks |
| [docs/security.md](docs/security.md) | Expose it beyond localhost, enable API keys, see how files are handled |
| [docs/deployment.md](docs/deployment.md) | Pick an image tag, run without Compose, build from source |
| [docs/errors.md](docs/errors.md) | Look up an error or warning code, verification and remedy fields |
| [docs/development.md](docs/development.md) | Build, test, run Postman/newman, see what CI does |
| [docs/design.md](docs/design.md) · [docs/roadmap.md](docs/roadmap.md) | Understand the design decisions and what comes next |
| [CHANGELOG.md](CHANGELOG.md) · [CONTRIBUTING.md](CONTRIBUTING.md) | See what changed, and the versioning rule |
| [docs/openapi.v1.json](docs/openapi.v1.json) | Generate a client (also served live at `/openapi/v1.json`) |

Using it from an AI agent? Install the
[Subtitle Edit Converter MCP](https://github.com/Wauter/subtitleedit-converter-mcp) release whose major.minor
version matches this API's.

## Credits and license

All subtitle format support comes from [Subtitle Edit](https://github.com/SubtitleEdit/subtitleedit) and its
`libse` library by Nikolaj Olsson and contributors, used under the MIT license. Thank you.

This wrapper is MIT licensed, see [LICENSE](LICENSE).

## Storage and job tracking

Send one subtitle per request, as an upload/inline content or an allowlisted filesystem, S3-compatible or
HTTPS input. An optional output URI stores the result; `sidecar:true` requests `{output}.json` metadata.
Conversions return a job id, and long requests continue with 202 and polling. Job history is in memory,
metadata only, not a download cache. ZIP conversion is not supported.

Start with [usage](docs/usage.md#job-metadata), [configuration](docs/configuration.md#jobs-and-storage),
[limits](docs/limits.md), [mount deployment](docs/deployment.md#mounted-subtitle-storage),
[allowlists and network checks](docs/security.md), and [`.env.example`](.env.example).

Stored input/output may be re-read through job-file routes while history lives; this is not a file archive. Filesystem/S3 writes default to collision errors; explicit `ifExists:"replace"` permits replacement. HTTPS follows signer policy. See [job files and overwrite policy](docs/usage.md#re-reading-job-files-and-overwrite-policy).
