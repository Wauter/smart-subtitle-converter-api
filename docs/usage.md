# Usage

Every endpoint with copy-paste `curl` examples. The examples assume the API on `localhost:4041` (see
[deployment.md](deployment.md)). The interactive reference is at <http://localhost:4041/docs>, the OpenAPI
document at `/openapi/v1.json` (committed as [openapi.v1.json](openapi.v1.json)).

## Converting

```bash
# SRT -> WebVTT; -OJ saves using the server's file name (movie.vtt)
curl -F file=@movie.srt -F target=webvtt localhost:4041/v1/convert -OJ

# a built-in preset
curl -F file=@movie.srt -F preset=netflix-en-vtt localhost:4041/v1/convert -OJ

# preset + overrides (objects merge, arrays replace, null clears)
curl -F file=@movie.srt -F preset=netflix-en-vtt \
     -F 'profile={"rules":{"maxLineLength":37},"timing":{"offsetMs":-1500}}' localhost:4041/v1/convert -OJ

# TTML -> SRT, and SRT -> ASS
curl -F file=@movie.ttml -F target=srt localhost:4041/v1/convert -OJ
curl -F file=@movie.srt  -F preset=srt-to-ass localhost:4041/v1/convert -OJ

# what is this file? (text or binary; binary adds codePage/formatDetails and up to 5 candidates)
curl -F file=@mystery.txt localhost:4041/v1/detect
```

## Formats, presets, schema and limits

```bash
curl 'localhost:4041/v1/formats?q=vtt'
curl localhost:4041/v1/profiles                 # built-in + mounted, invalid ones listed with errors
curl localhost:4041/v1/profiles/netflix-en-vtt  # raw + resolved (extends flattened)
curl localhost:4041/v1/profiles/validate -H 'content-type: application/json' -d @my-profile.json
curl localhost:4041/v1/schemas/profile.json     # JSON Schema for editors and agents


## Job metadata

Every accepted conversion receives `X-Job-Id` (also `id` in JSON). `GET /v1/jobs/{id}` returns status, timestamps and result metadata, without subtitle content. Records live in memory only: restart, expiry or oldest-first eviction returns 404, not a failed status. Defaults: six-hour TTL and 400 records. Running work has a separate cap, so eviction does not allow unlimited work. Metadata is bounded to 128 KiB per result; oversized diagnostics are replaced by a summary. Jobs belong to the authenticated key name.

## Waiting and polling

`Prefer: wait=N` selects a wait from 0 to 60 seconds (default 30, set by `REQUEST_WAIT_SECONDS`). A fast conversion returns its normal 200; unfinished work returns 202 with `Location: /v1/jobs/{id}`, `X-Job-Id` and `Retry-After: 1`. Poll that URL until status is `succeeded` or `failed`; the polling HTTP status is 200 even for a failed job, whose `error` contains problem details. Destination never selects synchronous versus asynchronous mode. Disconnecting does not cancel accepted work; server shutdown does.

**No replay of file bytes:** jobs retain metadata only. With upload/download and no `output`, only the original synchronous 200 carries content. If it becomes asynchronous, GET cannot recover that content: use an explicit output location for work that may exceed the wait. Do not blindly resubmit writes after a lost response; there is no idempotency-key deduplication.

## Filesystem locations and sidecars

```json
{"input":"file:///data/in/show.srt","output":"/data/out/show.pac","target":"pac","sidecar":true}
```

`input` is mutually exclusive with JSON `content` or multipart `file`. `output` is optional; with it the response contains output metadata and verification, not file bytes. An explicit `output` requires exactly one target; `target.additional` remains available for inline results. `detect` accepts `input` too. A path must name a file: no trailing slash, dot segments or symlinks. Only Linux server filesystem storage is supported; directory handles and no-follow opens prevent path-component symlink swaps. Pre-create parent directories. Existing output files are rejected, not overwritten.

Set `STORAGE_FILE_ROOTS` to allowlisted absolute roots (OS path separator, or a JSON array); empty disables file URIs. `STORAGE_FILE_ATOMIC=false` writes directly with exclusive creation, removing its own incomplete file after a failed write. With `true`, stage a sibling temporary file, flush it, then rename without replacing an existing destination. Consumers may see a growing file with direct writes; use atomic writes for scanning directories. Network mounts still need suitable rename semantics.

`sidecar:true` writes `{output}.json` with verification, warnings, effective profile and the job id, never converted subtitle content. No output means no sidecar. Subtitle success plus sidecar failure keeps the subtitle and adds `sidecar-write-failed`; receivers must inspect warnings.
