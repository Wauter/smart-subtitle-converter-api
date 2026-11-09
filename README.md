# Smart Subtitle Converter API

**Subtitle Edit's conversion engine as an HTTP service, with checks that catch what a plain conversion would let
slip.**

> **Built on Subtitle Edit.** All format reading and writing is done by `libse`, the library behind
> [Subtitle Edit](https://github.com/SubtitleEdit/subtitleedit) by **Nikolaj Olsson** (MIT). This project is a
> community wrapper, **not an official Subtitle Edit project**.
>
> **Status: alpha.** It works and is tested, but hasn't been through real-world use yet. Expect changes, and
> please [report issues](https://github.com/Wauter/smart-subtitle-converter-api/issues).

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
- **No guessing:** when a file could be read more than one way, it is rejected with the candidates instead of
  converted wrongly: PAC/FPC bytes without a `.pac`/`.fpc` name give `ambiguous-format`, and a PAC code page or
  non-UTF-8 text encoding that can't be told from the file's words gives `ambiguous-encoding`.
- **Source checks:** overlapping cues, cues longer than 30 seconds, zero-length cues and an EBU STL programme start
  are reported with cue numbers; header fields filled with defaults (such as `No Title`) are named
  (`format-defaults-applied`).
- **Presets and profiles:** built-in presets (`netflix-en-vtt`, `pal-to-film`, `clean-srt`, …), your own JSON
  presets in a mounted folder (picked up without a restart), and Subtitle Edit operations such as Fix Common Errors.
- **One job at a time:** libse uses global settings, so one worker runs one job at a time from a bounded queue;
  settings are reset after every job, and a full queue answers `503` with `Retry-After`.
- **Detection:** `/v1/detect` names format, encoding, code page and, for frame-based files, the frame rate used.
- **Storage and jobs:** read from and write to allowlisted folders, S3-compatible buckets or HTTPS URLs, with an
  optional JSON sidecar next to the output. Every conversion gets a job id you can poll.
- **Container defaults:** amd64/arm64, chiseled ASP.NET Core base with no shell, a non-root user, and a
  reference Compose that adds a read-only root filesystem, dropped capabilities and a `127.0.0.1`-only port.
  API keys and rate limits are optional.
- **Agent-friendly:** a JSON endpoint, an OpenAPI contract, a JSON Schema for profiles, and stable error codes with
  hints. The companion [MCP server](https://github.com/Wauter/smart-subtitle-converter-mcp) lets Claude and other AI
  agents use it directly.

## Quick start

With Docker:

```bash
docker run -d --name smart-subtitle-converter -p 127.0.0.1:8080:8080 ghcr.io/wauter/smart-subtitle-converter-api:latest
curl localhost:8080/healthz   # {"status":"ok","version":"…","libse":"…"}
```

Then convert something:

```bash
curl -F file=@movie.srt -F target=webvtt localhost:8080/v1/convert -OJ   # saves movie.vtt
```

Open <http://localhost:8080/docs> for the interactive API reference. Other ways to run it (Docker Compose,
building from source, image tags): [docs/deployment.md](docs/deployment.md).

## A few more examples

```bash
# a built-in preset, with overrides
curl -F file=@movie.srt -F preset=netflix-en-vtt \
     -F 'profile={"rules":{"maxLineLength":37}}' localhost:8080/v1/convert -OJ

# SRT -> EBU STL at 25 fps
curl -F file=@movie.srt -F target=ebustl \
     -F 'profile={"formatOptions":{"ebuStl":{"frameRate":25}}}' localhost:8080/v1/convert -OJ

# what is this file?
curl -F file=@mystery.txt localhost:8080/v1/detect
```

Errors are `application/problem+json` with a stable `code` and a `hint`:

```json
{ "status": 400, "code": "invalid-profile",
  "detail": "The profile has 1 error(s); first at '/target/format': Unknown format 'vtt2'.",
  "hint": "Did you mean 'webvtt'? See GET /v1/formats." }
```

Every error and warning code is explained in [docs/errors.md](docs/errors.md): what happened, why, and what to
do. More examples in [docs/usage.md](docs/usage.md).

## Storage and job tracking

Send one subtitle per request: as an upload or inline content, or as an `input` location on an allowlisted
filesystem, S3-compatible or HTTPS store. An optional `output` location stores the result, and `sidecar: true`
writes `{output}.json` with the verification and warnings next to it (a write without it removes an old sidecar).
Filesystem and S3 outputs are never overwritten unless you send `ifExists: "replace"`. ZIP and batch conversion are
not supported.

Every conversion returns a job id. Long requests continue in the background with `202` and a URL to poll, and
the stored input and output can be re-read through the job while its record lives. With `STORAGE_LIST_ENABLED=true`
(off by default) you can also list the allowed file and S3 folders and read a file by its location. Job history is kept in memory
only, holds metadata (never file bytes), expires, and is cleared on restart: it is not a file archive.

Start with [usage](docs/usage.md#job-metadata), [configuration](docs/configuration.md#jobs-and-storage),
[limits](docs/limits.md), [mounted storage](docs/deployment.md#mounted-subtitle-storage),
[allowlists and network checks](docs/security.md) and [`.env.example`](.env.example).

## Documentation

| Read this | When you want to |
|---|---|
| [docs/usage.md](docs/usage.md) | See every endpoint with curl examples, the JSON endpoint, storage locations, jobs and response headers |
| [docs/formats.md](docs/formats.md) | Work with binary formats: detection, code pages, frame rates, verification |
| [docs/profiles.md](docs/profiles.md) | Use presets, write your own profiles, chain operations |
| [docs/configuration.md](docs/configuration.md) | Change ports, limits, storage allowlists, job history, metrics or health checks |
| [docs/security.md](docs/security.md) | Expose it beyond localhost, enable API keys, see how files are handled |
| [docs/deployment.md](docs/deployment.md) | Pick an image tag, run without Compose, build from source |
| [docs/errors.md](docs/errors.md) | Look up an error or warning code: what happened, why, what to do; verification and remedy fields |
| [docs/development.md](docs/development.md) | Build, test, run Postman/newman, see what CI does |
| [docs/design.md](docs/design.md) · [docs/roadmap.md](docs/roadmap.md) | Understand the design decisions and what comes next |
| [CHANGELOG.md](CHANGELOG.md) · [CONTRIBUTING.md](CONTRIBUTING.md) | See what changed, and the versioning rule |
| [docs/openapi.v1.json](docs/openapi.v1.json) | Generate a client (also served live at `/openapi/v1.json`) |

Using it from an AI agent? Install the
[Smart Subtitle Converter MCP](https://github.com/Wauter/smart-subtitle-converter-mcp) release whose major.minor
version matches this API's.

## Credits and license

All subtitle format support comes from [Subtitle Edit](https://github.com/SubtitleEdit/subtitleedit) and its
`libse` library by Nikolaj Olsson and contributors, used under the MIT license. Thank you.

This wrapper is MIT licensed, see [LICENSE](LICENSE).
