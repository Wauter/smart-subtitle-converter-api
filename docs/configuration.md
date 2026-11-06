# Configuration

The API is configured with environment variables. Security-related settings are explained in
[security.md](security.md); how to pass variables with Compose or `docker run`: [deployment.md](deployment.md).

## Ports

The container listens on `8080` internally. The examples and the reference Compose publish it on host port
**4041** (8080 is often taken), bound to `127.0.0.1`, so only your own machine can reach it. To use a different host
port, change only the left-hand number (`127.0.0.1:5050:8080`). To reach it from other machines, see
[security.md](security.md#exposing-it-beyond-localhost).

## Environment variables

| Env | Default | |
|---|---|---|
| `ASPNETCORE_HTTP_PORTS` | `8080` (image) | Listen port inside the container (published on host port 4041 by the examples) |
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

## Queue

libse keeps its settings in global state, so one worker runs one conversion at a time from a bounded queue
(`QUEUE_CAPACITY`). A full queue answers `503 queue-full` with `Retry-After`; a job over
`CONVERSION_TIMEOUT_SECONDS` answers `504 conversion-timeout` and the worker is recycled. Scale out with replicas.
Details: [design.md §2.4](design.md#24-conversion-queue-global-state-hazard).

## Health and limits

- `GET /healthz`: liveness, `{"status":"ok","version":"…","libse":"…"}` (the API version and the libse package
  version).
- `GET /readyz`: self-test, worker health, queue.
- Both stay open when auth is on.
- `GET /v1/limits` reports the effective limits. It's under `/v1`, so it needs a key when auth is on, and it only
  says whether auth is required, never anything about keys.

The image has no shell or curl, so probe these from the orchestrator.
