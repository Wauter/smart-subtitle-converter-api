# Configuration

The API is configured with environment variables. Security-related settings are explained in
[security.md](security.md); how to pass variables with Compose or `docker run`: [deployment.md](deployment.md).

## Ports

The container listens on `8080` internally. The examples and the reference Compose publish it on host port
**8080** as well (`127.0.0.1:8080:8080`), bound to `127.0.0.1`, so only your own machine can reach it. If host
8080 is already taken, change only the left-hand number (for example `127.0.0.1:5050:8080`). To reach it from
other machines, see [security.md](security.md#exposing-it-beyond-localhost).

## Environment variables

| Env | Default | |
|---|---|---|
| `ASPNETCORE_HTTP_PORTS` | `8080` (image) | Listen port inside the container (published on host port 8080 by the examples) |
| `PROFILES_DIR` | `/profiles` | Mounted presets folder ([profiles.md](profiles.md)) |
| `API_KEYS` | unset | Comma-separated bearer keys (`name:key` allowed) |
| `API_KEYS_FILE` | unset | One key per line, `#` comments, `name:key`; reloaded on change |
| `ERRORS_VERBOSE` | `true` | `false` removes stack traces and internals from errors |
| `MAX_UPLOAD_BYTES` | `10485760` | Max decoded file size. Body caps derive from it: multipart `+256 KiB`, JSON `4*ceil(n/3) + PROFILE_MAX_BYTES + 64 KiB` (see `GET /v1/limits`) |
| `PROFILE_MAX_BYTES` | `65536` | Max inline profile size (min 1024) |
| `QUEUE_CAPACITY` | `32` | Waiting conversions before `503 queue-full` |
| `CONVERSION_TIMEOUT_SECONDS` | `30` | Queue-wait budget and run budget (each) |
| `MAX_CUES` / `MAX_LINE_CHARS` | `200000` / `10000` | Input limits |
| `RATE_LIMIT_PER_MINUTE` | off | Per key name, or per client IP when auth is off |
| `CORS_ORIGINS` | off | Comma-separated allow-list |
| `KNOWN_PROXIES` | unset | IPs whose `X-Forwarded-*` headers are trusted |
| `DOCS_ENABLED` | `true` | `/docs` (Scalar) |
| `METRICS_ENABLED` / `METRICS_PORT` | off | Prometheus text at `/metrics`, optionally only on that port |

## Jobs and storage

Copy selected settings from [`.env.example`](../.env.example); pass it with Docker `--env-file`.
Compose's `.env` is interpolation input, not automatically the container environment: add `env_file` or
explicit `environment` entries. Never commit real credentials.

| Env | Default | Behaviour |
|---|---|---|
| `REQUEST_WAIT_SECONDS` | `30` | Caller wait; clamped to 0..60; `Prefer: wait=N` overrides per request |
| `JOB_TTL_SECONDS` | `21600` | Seconds from creation; clamped to 1..86400; restart clears all records |
| `JOB_MAX_ENTRIES` | `400` | Oldest-first history eviction; clamped to 1..10000 |
| `IO_TIMEOUT_SECONDS` | `10` | Whole I/O attempt including body, clamped to 1..300 seconds |
| `IO_RETRIES` | `3` | Extra transient attempts, clamped to 0..10; no retry on 403/404 or validation |
| `STORAGE_FILE_ROOTS` | empty (off) | Absolute Linux roots separated by `:` or a JSON array; mounts use container paths |
| `STORAGE_FILE_ATOMIC` | `false` | Sibling stage/flush/no-replace rename when true; otherwise exclusive direct write |
| `HTTP_ALLOWED_HOSTS` | empty (off) | Exact hostnames, comma-separated, same for HTTPS GET and PUT; no wildcards |
| `S3_ENDPOINT` | unset | Trusted custom endpoint; otherwise AWS regional endpoint |
| `S3_REGION` | `us-east-1` | Signing region |
| `S3_FORCE_PATH_STYLE` | `false` | Set true for MinIO |
| `S3_ALLOWED_BUCKETS` | empty (off) | Comma-separated bucket names |
| `S3_ALLOWED_PREFIXES` | unset | Comma-separated key prefixes, optional within allowed buckets |

Each scheme is off until its allowlist is nonempty. `input` and `output` are independent.
Filesystem writes reject existing files by default and create files with mode `0644` (narrowed by the umask; not configurable); S3 uses conditional create by default (explicit `ifExists:replace` allows replacement); HTTPS follows the receiver's PUT semantics.
S3 uses the default AWS credential chain (environment/shared configuration/task or instance role), never
credentials supplied by the request. A trusted S3 endpoint may be internal; HTTPS request URIs may not resolve
to private addresses. No Azure or GCS adapter is installed.

Active work is capped at `min(JOB_MAX_ENTRIES, QUEUE_CAPACITY + 1)` independently of history eviction.
Records keep metadata only, with a 128 KiB result/error budget. Detailed diagnostics above that budget become
a `metadataTruncated` summary. See [limits](limits.md), [URI usage and polling](usage.md#job-metadata) and
[mount permissions](deployment.md#mounted-subtitle-storage).

## Queue

libse keeps its settings in global state, so one worker runs one conversion at a time from a bounded queue
(`QUEUE_CAPACITY`). A full queue answers `503 queue-full` with `Retry-After`; a job over
`CONVERSION_TIMEOUT_SECONDS` fails with `504 conversion-timeout` and the worker is recycled. Scale out with replicas.
Details: [design.md §2.4](design.md#24-conversion-queue-global-state-hazard).

## Health and limits

- `GET /healthz`: liveness, `{"status":"ok","version":"…","libse":"…"}` (the API version and the libse package
  version).
- `GET /readyz`: self-test, worker health, queue.
- Both stay open when auth is on.
- `GET /v1/limits` reports the effective limits. It's under `/v1`, so it needs a key when auth is on, and it only
  says whether auth is required, never anything about keys.

The image has no shell or curl, so probe these from the orchestrator.

`ifExists` is per request, not an environment variable: error (default) | replace. Replacement uses sibling staging for filesystem writes. S3 default writes are conditional; HTTPS follows signer/receiver policy. [Job-file downloads](usage.md#re-reading-job-files-and-overwrite-policy) re-use current storage settings.
