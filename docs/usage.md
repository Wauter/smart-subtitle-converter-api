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

# several targets for ONE input -> JSON outputs (not an archive)
curl -F file=@movie.srt -F target=vtt -F 'profile={"target":{"additional":["ass","ttml"]}}' localhost:4041/v1/convert -OJ
```

Presets, profiles and operations: [profiles.md](profiles.md). Binary formats (EBU STL, PAC, …):
[formats.md](formats.md).

## JSON in/out (for agents and scripts)

```bash
curl localhost:4041/v1/convert/json -H 'content-type: application/json' \
     -d '{"content":"1\n00:00:01,000 --> 00:00:02,000\nHello\n","fileName":"a.srt","target":"vtt"}'
```

Binary content must be base64 (`"contentEncoding":"base64"`); binary outputs always come back base64. Example in
[formats.md](formats.md#examples).

## Detection

```bash
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

# server limits (body caps, wait, I/O, jobs, storage schemes, queue depth, auth required?) before you upload
curl localhost:4041/v1/limits
```

## With API keys enabled

```bash
curl -H "Authorization: Bearer $KEY" -F file=@movie.srt -F target=vtt localhost:4041/v1/convert -OJ
```

How to enable keys: [security.md](security.md#api-keys).

## Response headers

Conversions return `X-Request-Id`, `X-Detected-Format`, `X-Detected-Encoding`, `X-Detected-Code-Page` (binary
input), `X-Output-Kind` (`text`/`binary`), `X-Cue-Count`, `X-Cue-Count-In`, `X-Warnings-Count`, `X-Warnings`,
`X-Profile`, `X-Libse-Version`, `X-Verification-Verdict` (single binary output) and `X-Job-Id`.
Detection/conversion headers are available on a completed response; a 202 carries job/polling headers.

Send your own `X-Request-Id` (`[A-Za-z0-9._-]{1,64}`) to correlate requests; anything else is replaced by a fresh id.

## Errors

Errors are `application/problem+json` with a stable `code`, a `hint` and the `requestId`, which also appears in
the logs:

```json
{ "type": ".../docs/errors.md#invalid-profile", "title": "Invalid profile", "status": 400,
  "code": "invalid-profile", "detail": "The profile has 1 error(s); first at '/target/format': Unknown format 'vtt2'.",
  "hint": "Did you mean 'webvtt'? See GET /v1/formats.", "requestId": "01M52D3QQQP64E77FSF5QXGHJJ",
  "errors": [ { "pointer": "/target/format", "message": "Unknown format 'vtt2'.", "hint": "Did you mean 'webvtt'?" } ] }
```

Every error and warning code, with what happened, why, and what to do: [errors.md](errors.md).

## Job metadata

Every accepted conversion receives `X-Job-Id` (also `id` in JSON). `GET /v1/jobs/{id}` returns status, timestamps and result metadata, without subtitle content. Records live in memory only: restart, expiry or oldest-first eviction returns 404, not a failed status. Defaults: six-hour TTL from creation and 400 records. Running work has a separate cap, so eviction does not allow unlimited work. Metadata is bounded to 128 KiB per result; oversized diagnostics are replaced by a summary. Job ids are unguessable capability tokens: any valid API key knowing an id can read its metadata and stored files; there is no owner/admin role. Requests rejected before admission have no job. Failed admitted jobs also return their id.

## Waiting and polling

`Prefer: wait=N` selects a wait from 0 to 60 seconds (default 30, set by `REQUEST_WAIT_SECONDS`). A fast conversion returns its normal 200; unfinished work returns 202 with `Location: /v1/jobs/{id}`, `X-Job-Id` and `Retry-After: 1`. Poll that URL until status is `succeeded` or `failed`; the polling HTTP status is 200 even for a failed job, whose `error` contains problem details. Destination never selects synchronous versus asynchronous mode. Disconnecting does not cancel accepted work; server shutdown does.

**No replay of file bytes:** jobs retain metadata only. With upload/download and no `output`, only the original synchronous 200 carries content. If it becomes asynchronous, GET cannot recover response-only content: use an explicit output location for work that may exceed the wait. Do not blindly resubmit writes after a lost response; there is no idempotency-key deduplication.

## Filesystem locations and sidecars

```json
{"input":"file:///data/in/show.srt","output":"/data/out/show.pac","target":"pac","sidecar":true}
```

`input` is mutually exclusive with JSON `content` or multipart `file`. `output` is optional; with it the response contains output metadata and verification, not file bytes. An explicit `output` requires exactly one target; `target.additional` remains available for inline results. `detect` accepts `input` too. A path must name a file: no trailing slash, dot segments or symlinks. Only Linux server filesystem storage is supported; directory handles and no-follow opens prevent path-component symlink swaps. Pre-create parent directories. Existing filesystem and S3 outputs are rejected by default; `ifExists:"replace"` explicitly permits replacement.

Set `STORAGE_FILE_ROOTS` to allowlisted absolute roots (OS path separator, or a JSON array); empty disables file URIs. `STORAGE_FILE_ATOMIC=false` writes directly with exclusive creation, removing its own incomplete file after a failed write. With `true`, stage a sibling temporary file, flush it, then rename without replacing an existing destination. Consumers may see a growing file with direct writes; use atomic writes for scanning directories. Network mounts still need suitable rename semantics.

`sidecar:true` writes `{output}.json` with verification, warnings, effective profile and the job id, never converted subtitle content. No output means no sidecar. Subtitle success plus sidecar failure keeps the subtitle and adds `sidecar-write-failed`; receivers must inspect warnings. Without `sidecar:true`, a successful write deletes an existing `{output}.json` (filesystem and S3): it describes a file that is no longer there. If that delete fails the subtitle is kept and `sidecar-delete-failed` is added.

Filesystem outputs are created with mode `0644` (owner read/write, everyone else read; the process umask can narrow it), so MAM and playout accounts on a shared mount can read deliveries.

## HTTPS input and output

```json
{"input":"https://mam.example.com/in/show.stl?signature=...","output":"https://store.example.com/out/show.pac?signature=...","target":"pac","sidecar":true}
```

Set `HTTP_ALLOWED_HOSTS=mam.example.com,store.example.com` (exact hostnames, no wildcards). Input uses GET, output uses PUT of the raw converted bytes, without bearer credentials from this API. Both use the same host/IP policy; Content-Type is not used for detection. Redirects, encoded response bodies and partial GET responses are rejected. No URL-derived second sidecar is attempted for HTTPS output: `sidecar:true` adds `sidecar-skipped` instead. Signed queries are excluded from job metadata.

`IO_TIMEOUT_SECONDS=10` covers each transfer attempt including reading the body. `IO_RETRIES=3` means three extra attempts, with backoff for connection resets, timeouts and HTTP 408/429/500/502/503/504. 403/404, certificate failures and validation rejections are not retried. A timed-out PUT may have reached the receiver: retries target the same URL and object; receiving systems must make repeated PUTs safe. There is no rollback of an external receiver.

## S3-compatible and hybrid locations

```json
{"input":"s3://subs/in/show.stl","output":"/data/out/show.pac","target":"pac","sidecar":true}
```

Inputs and outputs are independent; all enabled schemes can be mixed. S3 requires `S3_ALLOWED_BUCKETS` (comma-separated, empty disables it) and optionally `S3_ALLOWED_PREFIXES` (comma-separated exact key prefixes, e.g. `in/,out/`). A prefix ending in `/` scopes a directory-like namespace; `in` also permits `incoming` by ordinary string-prefix rules. URI keys are percent-decoded once and cannot contain dot segments, backslashes or control characters. Query credentials are not accepted on `s3://` URIs.

`S3_ENDPOINT` selects one server-configured compatible endpoint; unset uses the AWS endpoint for `S3_REGION` (default `us-east-1`). Set `S3_FORCE_PATH_STYLE=true` for MinIO. Credentials come from the SDK's default environment/role chain. Custom endpoints are trusted operator configuration and may be internal; never let untrusted callers configure them. No Azure/GCS support or tests.

Outputs use one object PUT with `If-None-Match: *` by default; existing keys yield 409 `output-exists`. Explicit `ifExists:"replace"` omits the condition and permits replacement; no temporary object, rename or copy. The only delete is a stale `{key}.json` sidecar after a write without `sidecar:true`. Use a unique output key if replacement is unwanted. The subtitle and `{key}.json` sidecar are two independent writes, not a transaction. S3 and HTTPS use the same attempt timeout/retry count; SDK retries are disabled so attempts do not multiply.

## URI and polling examples

After configuring the matching allowlists (see [configuration](configuration.md)):

```bash
# API container paths, not the caller's paths. Parent folders must already exist.
curl localhost:4041/v1/convert/json -H 'content-type: application/json' \
  -H 'Prefer: wait=30' \
  -d '{"input":"file:///data/in/show.srt","output":"/data/out/show.vtt","target":"vtt","sidecar":true}'

# Hybrid input/output; response is a job immediately.
curl -i localhost:4041/v1/convert/json -H 'content-type: application/json' \
  -H 'Prefer: wait=0' \
  -d '{"input":"s3://subs/in/show.stl","output":"/data/out/show.pac","target":"pac","sidecar":true}'
curl localhost:4041/v1/jobs/JOB_ID  # replace JOB_ID with the returned id

# Detection from storage does not create a conversion job.
curl localhost:4041/v1/detect -H 'content-type: application/json' \
  -d '{"input":"s3://subs/in/show.stl"}'
```

The [Postman collection](../postman/subtitleedit-converter-api.postman_collection.json) includes these flows,
HTTPS GET/PUT and sidecars. Storage requests are skipped until their environment variables are supplied;
use fresh filesystem output names for repeated runs. ZIP input is rejected: loop over single-file requests.

## Re-reading job files and overwrite policy

```bash
curl -OJ localhost:4041/v1/jobs/JOB_ID/input
curl -OJ localhost:4041/v1/jobs/JOB_ID/output
curl localhost:4041/v1/convert/json -H 'content-type: application/json' \
  -d '{"input":"s3://subs/in/show.srt","output":"s3://subs/out/show.vtt","target":"vtt","ifExists":"replace","sidecar":true}'
# Multipart has the same field: -F ifExists=replace
```

Job file routes read the **current** file from its recorded URI using current allowlists, size limits and
network checks. They do not run detection/conversion or recover an old copy. Input can be readable while
conversion runs; output is `job-not-ready` until completion, and a failed conversion has no successful stored
output. Upload/inline sources and response-only output have no stored location. Query-bearing HTTPS URLs
are marked non-replayable because the query is never retained. See [job file errors](errors.md#job-file-errors).
Treat an id as a capability token: any valid API key with it can read metadata and stored files.

`ifExists` accepts **error** (default) or **replace**, for JSON and multipart. There is no archive policy or
delete API. Filesystem default creation is exclusive; replacement stages a complete sibling file before rename
even when `STORAGE_FILE_ATOMIC=false`, keeping the old file on staging failure. S3 uses a true conditional PUT
by default; compatible endpoints must support `If-None-Match: *`. Unsupported conditions fail rather than fall
back to a racy check/PUT. Replacement omits that condition. The sidecar follows the same policy, independently.
A conditional retry can find an object written by the prior attempt: inspect a 409 before choosing replacement.

HTTPS PUT follows whatever its signer/receiver allowed. The API does **not** invent an If-None-Match header or
claim HTTPS has the disk/S3 collision guarantee. Choose a distinct signed destination or a signer-enforced
condition when required. No second sidecar URL is derived.
