# Usage

Every endpoint with copy-paste `curl` examples. The examples assume the API on `localhost:8080` (see
[deployment.md](deployment.md)). The interactive reference is at <http://localhost:8080/docs>, the OpenAPI
document at `/openapi/v1.json` (committed as [openapi.v1.json](openapi.v1.json)).

## Converting

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

# several targets for ONE input -> JSON outputs (not an archive)
curl -F file=@movie.srt -F target=vtt -F 'profile={"target":{"additional":["ass","ttml"]}}' localhost:8080/v1/convert -OJ
```

Presets, profiles and operations: [profiles.md](profiles.md). Binary formats (EBU STL, PAC, …):
[formats.md](formats.md).

## JSON in/out (for agents and scripts)

```bash
curl localhost:8080/v1/convert/json -H 'content-type: application/json' \
     -d '{"content":"1\n00:00:01,000 --> 00:00:02,000\nHello\n","fileName":"a.srt","target":"vtt"}'
```

Binary content must be base64 (`"contentEncoding":"base64"`); binary outputs always come back base64. Example in
[formats.md](formats.md#examples).

## Detection

```bash
# what is this file? (text or binary; binary adds codePage/formatDetails and up to 5 candidates)
curl -F file=@mystery.txt localhost:8080/v1/detect
```

## Formats, presets, schema and limits

```bash
curl 'localhost:8080/v1/formats?q=vtt'
curl localhost:8080/v1/profiles                 # built-in + mounted, invalid ones listed with errors
curl localhost:8080/v1/profiles/netflix-en-vtt  # raw + resolved (extends flattened)
curl localhost:8080/v1/profiles/validate -H 'content-type: application/json' -d @my-profile.json
curl localhost:8080/v1/schemas/profile.json     # JSON Schema for editors and agents

# server limits (body caps, wait, I/O, jobs, storage schemes, queue depth, auth required?) before you upload
curl localhost:8080/v1/limits
```

## API keys

A key is required by default (see [Securing the API](security.md#securing-the-api)). The examples on this page
leave the header out for brevity; add it to every `/v1` call:

```bash
curl -H "Authorization: Bearer $KEY" -F file=@movie.srt -F target=vtt localhost:8080/v1/convert -OJ
```

How to enable keys: [security.md](security.md#step-1-the-api-key-file).

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

`input` is mutually exclusive with JSON `content` or multipart `file`. `output` is optional; with it the response contains output metadata and verification, not file bytes. With `output`, `outputs[].fileName` is the name the file was stored under (the last path segment of `output`), not the name the converter would generate from the source. An explicit `output` requires exactly one target; `target.additional` remains available for inline results. `detect` accepts `input` too. A path must name a file: no trailing slash, dot segments, symlinks or control characters (also when percent-encoded, such as `%00`). Hidden files and folders are never read as input ([hidden files](#hidden-files-and-folders)). Only Linux server filesystem storage is supported; directory handles and no-follow opens prevent path-component symlink swaps. Pre-create parent directories. Existing filesystem and S3 outputs are rejected by default; `ifExists:"replace"` explicitly permits replacement.

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

Inputs and outputs are independent; all enabled schemes can be mixed. S3 requires `S3_ALLOWED_BUCKETS` (comma-separated, empty disables it) and optionally `S3_ALLOWED_PREFIXES` (comma-separated exact key prefixes, e.g. `in/,out/`). A prefix ending in `/` scopes a directory-like namespace; `in` also permits `incoming` by ordinary string-prefix rules. URI keys are percent-decoded once and cannot contain dot segments, backslashes or control characters (an encoded `%00` included). Hidden keys (a segment starting with `.`) are not read as input ([hidden files](#hidden-files-and-folders)). Query credentials are not accepted on `s3://` URIs.

`S3_ENDPOINT` selects one server-configured compatible endpoint; unset uses the AWS endpoint for `S3_REGION` (default `us-east-1`). Set `S3_FORCE_PATH_STYLE=true` for MinIO. Credentials come from the SDK's default environment/role chain. Custom endpoints are trusted operator configuration and may be internal; never let untrusted callers configure them. No Azure/GCS support or tests.

Outputs use one object PUT with `If-None-Match: *` by default; existing keys yield 409 `output-exists`. Explicit `ifExists:"replace"` omits the condition and permits replacement; no temporary object, rename or copy. The only delete is a stale `{key}.json` sidecar after a write without `sidecar:true`. Use a unique output key if replacement is unwanted. The subtitle and `{key}.json` sidecar are two independent writes, not a transaction. S3 and HTTPS use the same attempt timeout/retry count; SDK retries are disabled so attempts do not multiply.

## URI and polling examples

After configuring the matching allowlists (see [configuration](configuration.md)):

```bash
# API container paths, not the caller's paths. Parent folders must already exist.
curl localhost:8080/v1/convert/json -H 'content-type: application/json' \
  -H 'Prefer: wait=30' \
  -d '{"input":"file:///data/in/show.srt","output":"/data/out/show.vtt","target":"vtt","sidecar":true}'

# Hybrid input/output; response is a job immediately.
curl -i localhost:8080/v1/convert/json -H 'content-type: application/json' \
  -H 'Prefer: wait=0' \
  -d '{"input":"s3://subs/in/show.stl","output":"/data/out/show.pac","target":"pac","sidecar":true}'
curl localhost:8080/v1/jobs/JOB_ID  # replace JOB_ID with the returned id

# Detection from storage does not create a conversion job.
curl localhost:8080/v1/detect -H 'content-type: application/json' \
  -d '{"input":"s3://subs/in/show.stl"}'
```

The [Postman collection](../postman/smart-subtitle-converter-api.postman_collection.json) includes these flows,
HTTPS GET/PUT and sidecars. Storage requests are skipped until their environment variables are supplied;
use fresh filesystem output names for repeated runs. ZIP input is rejected: loop over single-file requests.

## Browsing storage and reading by location

Off by default. With `STORAGE_LIST_ENABLED=true` the API lists its allowed file and S3 locations and reads files
from them, with no wider scope than conversions: the same roots, buckets, prefixes, symlink, traversal and size
checks. Off, these routes answer `403 storage-list-disabled`; `GET /v1/limits` says which (`storage.list`,
`storage.listSchemes`).

```bash
# the allowlists as folders (no credentials, endpoints or queries); HTTPS hosts are listable:false
curl localhost:8080/v1/storage/locations
# one folder, a page at a time
curl 'localhost:8080/v1/storage/list?location=s3://subs/in/&prefix=show_'
curl 'localhost:8080/v1/storage/list?location=file:///data/in/&cursor=NEXT_FROM_PREVIOUS_PAGE'
# one file, as stored (no conversion)
curl -OJ 'localhost:8080/v1/storage/file?location=s3://subs/in/show_001.srt'
```

A listing has `items` (`name` relative to the folder, `uri`, `type` `file` or `folder`, and `size` and `modified` for
files), `returned`, `truncated` and, when truncated, `next`. Pass `next` as `cursor` with the same `location` and
`prefix` for the following page.

- One level per call. Folders end in `/`; list one by passing its `uri`, or put the path in `prefix`
  (`prefix=season1/ep`).
- `prefix` matches the start of a name. No globs, no time sorting, no offsets and no total count: a page holds at
  most 200 items, in name order (UTF-8 byte order, the order S3 uses).
- Cursors are stateless. Every item that exists for the whole walk appears exactly once; items added or removed
  during the walk may or may not appear. A listing is not a snapshot.
- Listings skip hidden (dot) entries ([hidden files](#hidden-files-and-folders)); filesystem listings also skip symlinks, devices and pipes. A filesystem folder is read in full
  on every page and sorted, so very large folders are slower per page.
- S3 uses `ListObjectsV2` with the delimiter `/`, and the cursor carries its continuation token. Folder marker
  objects (keys ending in `/`) are skipped. Listing needs `s3:ListBucket` ([S3 permissions](deployment.md#s3-permissions)).
  S3-compatible stores that don't list keys in UTF-8 byte order or don't support `ListObjectsV2` continuation
  tokens may page differently.
- When `S3_ALLOWED_PREFIXES` is set, a listing must stay inside one of them: list `s3://subs/in/`, not
  `s3://subs/`. A prefix without a trailing `/` (`out/show`) shows up in the locations as a folder plus `prefix`.
- HTTPS is never listed or read here (`422 storage-not-listable`); use HTTPS URLs as convert `input`/`output`.

### Hidden files and folders

Hidden files and folders are never read, listed, written or used as a conversion source or sidecar, whether listing
is on or off. A
path or object key is hidden when any of its `/`-separated components starts with `.`: a dotfile (`.env`,
`.hidden.srt`), anything inside a dot-named folder at any depth (`.git/config`, `.cache/x/y.srt`), and everything
under an allowed root or `S3_ALLOWED_PREFIXES` entry that is itself hidden or sits inside a hidden folder
(`/srv/.subtitles`, `/home/app/.cache/subs`, `tmp/.cache/`). Dots inside names are fine: `show.en.v2.srt`,
`v1.2/ep.srt` and `a..b.srt` are ordinary files.

- **Where it applies:** `GET /v1/storage/file`, `GET /v1/storage/list` (a hidden folder, or a `prefix` starting
  with `.`), convert and detect `input` and convert `output` (JSON and multipart, including `ifExists:"replace"`
  and the `{output}.json` sidecar), and `GET /v1/jobs/{id}/input|output`. Filesystem
  roots and S3 keys alike. Listings leave hidden entries out, and `GET /v1/storage/locations` leaves out hidden
  roots and prefixes, so their names aren't revealed; the startup log warns about them (`storage.hidden`).
- **How it's checked:** on the decoded location (percent-encoding is decoded once, so `%2Eenv` is `.env`) before any
  filesystem or S3 call, with the same `422 hidden-path` for an existing and a missing hidden name. A location
  outside the allowed folders gets `422 storage-location-rejected` first, so you always see the most basic problem.
  The filesystem never follows symlinks, so a link can't alias a hidden file.
- **HTTPS URLs are not affected** (`/.well-known/…` still works as input or output): those files belong to the
  remote server.
- **Limits:** the rule is name-based. It doesn't look at other hidden markers (Windows hidden attributes, macOS
  `UF_HIDDEN`), and filesystem storage runs on Linux only. Hidden S3 entries are dropped after `ListObjectsV2`
  returns them, so a page can hold fewer than 200 items (even none) while `truncated` is still true.

### Subtitle files only

By file and S3 location, only subtitle files are read, listed or written: the extension (case-insensitive) must be
one that libse reads or writes, such as `.srt`, `.vtt`, `.ass`, `.stl`, `.ttml`, `.xml` or `.txt`. The full list is
`storage.extensions` in `GET /v1/limits`. Listings show only those files, plus folders. A conversion `output` must
use an extension of its target format (`out.vtt` for WebVTT, not `out.srt`); its `{output}.json` sidecar is
allowed. Anything else, including a file without an extension, is `422 extension-not-allowed`. HTTPS URLs are not
checked.

### Big folders and busy servers

A listing stops at 5000 matching entries per folder and answers `422 folder-too-large`; use `prefix` to narrow it.
Listing, reading by location and job file reads go through a small browse queue (4 at a time by default, more
waiting up to 30 seconds). When it is full the answer is `503 browse-busy` with `Retry-After`; just retry.
Conversions have their own queue and are not slowed down by browsing.

`GET /v1/storage/file` returns the current bytes with a `Content-Disposition` file name. It doesn't detect or
convert; send the same location as `input` to `/v1/detect` or `/v1/convert/json` for that.

## Re-reading job files and overwrite policy

```bash
curl -OJ localhost:8080/v1/jobs/JOB_ID/input
curl -OJ localhost:8080/v1/jobs/JOB_ID/output
curl localhost:8080/v1/convert/json -H 'content-type: application/json' \
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
