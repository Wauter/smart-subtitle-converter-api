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
| `LOG_FORMAT` | `text` | `text`: one plain line per entry (`2026-11-09T13:40:51.531Z info  Storage.StorageCheck  storage.s3 bucket=subs ok`: UTC time, level, category, message; exception details on indented lines below; no colors). `json`: one JSON object per line (`Timestamp`, `LogLevel`, `Category`, `Message`, `State`, `Scopes`). Any other value falls back to `text` and logs `logging.config warning=invalid-log-format var=LOG_FORMAT ...` |

## Jobs and storage

Copy selected settings from [`.env.example`](../.env.example); pass it with Docker `--env-file`.
A `.env` file next to a Compose file is only interpolation input for `${...}` in that file. On its own it does
**not** put any variable into the container, so `S3_ALLOWED_BUCKETS` in `.env` alone leaves S3 off. The repo's
[`docker-compose.yml`](../docker-compose.yml) therefore loads it with `env_file: [{ path: .env, required: false }]`
(Docker Compose 2.24 or later); in your own Compose file add the same `env_file` entry or list the variables under
`environment`. Values under `environment` override the same variable from `env_file`. The startup log always
prints `S3_ALLOWED_BUCKETS`, `STORAGE_FILE_ROOTS` and `HTTP_ALLOWED_HOSTS` (as `(unset — S3 off)` and so on), so
`docker compose logs | grep storage.config` shows what the container actually received. Never commit real
credentials.

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

The IAM actions S3 needs, with a copy-pasteable policy, are in [S3 permissions](deployment.md#s3-permissions).
At startup the API logs its effective storage settings and checks each backend; see
[startup storage diagnostics](#startup-storage-diagnostics).

Active work is capped at `min(JOB_MAX_ENTRIES, QUEUE_CAPACITY + 1)` independently of history eviction.
Records keep metadata only, with a 128 KiB result/error budget. Detailed diagnostics above that budget become
a `metadataTruncated` summary. See [limits](limits.md), [URI usage and polling](usage.md#job-metadata) and
[mount permissions](deployment.md#mounted-subtitle-storage).

## Startup storage diagnostics

Right after start, a background check logs the effective storage configuration, warns about missing or malformed
values, resolves the AWS credential source and probes each enabled backend once. It is warning-only: it never
delays listening, never fails startup and does not affect `/healthz` or `/readyz`. It writes nothing and does not
contact `HTTP_ALLOWED_HOSTS`.

- **`storage.config`**: one line per setting (`S3_ALLOWED_BUCKETS`, `S3_ALLOWED_PREFIXES`, `S3_REGION`,
  `S3_ENDPOINT`, `S3_FORCE_PATH_STYLE`, `IO_TIMEOUT_SECONDS`, `IO_RETRIES`, `STORAGE_FILE_ROOTS`,
  `HTTP_ALLOWED_HOSTS`, `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`, `AWS_PROFILE`,
  `AWS_SHARED_CREDENTIALS_FILE`, `AWS_CONFIG_FILE`) with the value as given and the effective value, then
  `storage.config schemes=...`.
- **`storage.s3 credentials`**: the source the AWS SDK's default chain resolved: `env`, `profile` (shared
  credentials/config file, including SSO, process and assume-role profiles), `web-identity`, `container-role`,
  `instance-role`, or a warning when nothing was found. Bounded by `IO_TIMEOUT_SECONDS`.
- **`storage.s3 bucket=`**: one `HeadBucket` per allowlisted bucket (needs `s3:ListBucket`).
- **`storage.file root=`**: each root must be absolute, exist, be a listable directory and contain no symlink.
  `writable=` is informational; a read-only input root is fine.
- **`storage.check done ok=N warnings=M`**: always the last line. `ok` counts passed probes (credentials,
  buckets, roots), `warnings` counts warning lines.

The access key id, secret key and session token are never printed in full. They show as the first 3
characters, `***` and the last 4 (`AKI***MPLE`), or as `***` alone when shorter than 10 characters. The same
masking applies to any of these values inside SDK error text.

Every warning has the same shape, so it can be fixed from the line alone:

```text
storage.config warning=<code> var=<ENV_VAR> value=<value as seen, masked if secret> problem="..." fix="..."
storage.s3 bucket=<bucket> warning=<S3 error code|HTTP<status>|Timeout|...> status=<http> [bucket_region=<region>] problem="..." fix="..."
storage.file root=<path> warning=<code> var=STORAGE_FILE_ROOTS problem="..." fix="..."
```

Excerpt with a few mistakes:

```text
storage.config var=S3_ALLOWED_BUCKETS value="subs,s3://tmp/out" effective="subs,s3://tmp/out"
storage.config warning=bucket-has-scheme var=S3_ALLOWED_BUCKETS value="s3://tmp/out" problem="entries must be bare bucket names, not s3:// URIs" fix="use S3_ALLOWED_BUCKETS=tmp and S3_ALLOWED_PREFIXES=out/"
storage.config var=S3_REGION value="eu-west-1" effective="eu-west-1"
storage.config var=S3_ENDPOINT value=<unset> effective="AWS S3 regional endpoint for eu-west-1"
storage.config var=IO_TIMEOUT_SECONDS value="500" effective=300
storage.config warning=clamped var=IO_TIMEOUT_SECONDS value="500" problem="outside 1..300; using 300" fix="set IO_TIMEOUT_SECONDS between 1 and 300"
storage.config var=HTTP_ALLOWED_HOSTS value=<unset> effective="(unset — HTTPS off)"
storage.config var=AWS_ACCESS_KEY_ID value=ASI***MPLE
storage.config var=AWS_SECRET_ACCESS_KEY value=wJa***EKEY
storage.config var=AWS_SESSION_TOKEN value=<unset>
storage.config warning=session-token-missing var=AWS_SESSION_TOKEN value=<unset> problem="AWS_ACCESS_KEY_ID starts with ASIA (temporary credentials) but no session token is set; S3 answers 403 InvalidAccessKeyId/InvalidToken" fix="set AWS_SESSION_TOKEN from the same STS response, or use long-term (AKIA...) keys or a role"
storage.config schemes=file,s3
storage.s3 credentials ok source=env access_key_id=ASI***MPLE session_token=no type=EnvironmentVariablesAWSCredentials
storage.s3 bucket=subs warning=MovedPermanently status=301 bucket_region=eu-north-1 problem="bucket is in another region (eu-north-1) than S3_REGION=eu-west-1" fix="set S3_REGION=eu-north-1"
storage.s3 bucket=s3://tmp/out skipped reason=invalid-name detail="see the storage.config warning for S3_ALLOWED_BUCKETS"
storage.file root=/data/in ok writable=False
storage.file root=data/out warning=not-absolute var=STORAGE_FILE_ROOTS problem="STORAGE_FILE_ROOTS entries must be absolute container paths" fix="use an absolute path such as /data/out that matches the volume mount target"
storage.check done ok=2 warnings=5
```

A healthy start ends with lines like:

```text
storage.s3 credentials ok source=profile profile="subs-rw" credentials_file="/run/secrets/aws" config_file="~/.aws/config" access_key_id=AKI***MPLE session_token=no type=BasicAWSCredentials
storage.s3 bucket=subs ok
storage.file root=/data/out ok writable=True
storage.check done ok=3 warnings=0
```

`storage.config` warning codes: `quoted`, `whitespace`, `crlf` (values with surrounding quotes, spaces or a
Windows line ending), `bucket-has-scheme`, `bucket-is-arn`, `bucket-has-path`, `invalid-bucket-name`,
`prefixes-without-buckets`, `prefix-has-scheme`, `prefix-leading-slash`, `prefix-wildcard`, `region-missing`,
`region-unknown`, `endpoint-no-scheme`, `endpoint-invalid`, `endpoint-has-path`, `invalid-bool`,
`invalid-integer`, `clamped`, `root-has-comma`, `host-not-bare`, `host-wildcard`, `host-has-port`,
`credentials-incomplete`, `session-token-missing`, `session-token-unexpected`, `file-relative`, `file-missing`.
Lines with `note=` (for example `path-style-off` for a custom endpoint without `S3_FORCE_PATH_STYLE=true`) are
hints and are not counted as warnings. A `storage.s3 bucket=... warning=Forbidden status=403` from a policy
without `s3:ListBucket` is expected to be harmless; see [S3 permissions](deployment.md#s3-permissions).

Values that cannot be parsed at all still stop startup, as before, but the error names the variable, the value
and a fix, for example:

```text
Invalid configuration: IO_TIMEOUT_SECONDS="10s" is not a whole number. Fix: set IO_TIMEOUT_SECONDS=10 (or unset it for the default); whole seconds, clamped to 1..300.
Invalid configuration: S3_FORCE_PATH_STYLE="yes" is not true or false. Fix: set S3_FORCE_PATH_STYLE=true (or unset it for the default); true for MinIO/path-style endpoints, false for AWS.
```

## Queue

libse keeps its settings in global state, so one worker runs one conversion at a time from a bounded queue
(`QUEUE_CAPACITY`). A full queue answers `503 queue-full` with `Retry-After`; a job over
`CONVERSION_TIMEOUT_SECONDS` fails with `504 conversion-timeout` and the worker is recycled. Scale out with replicas.
Details: [design.md §2.4](design.md#24-conversion-queue-global-state-hazard).

## Health and limits

- `GET /healthz`: liveness, `{"status":"ok","version":"…","libse":"…"}` (the API version and the libse package
  version).
- `GET /readyz`: self-test, worker health, queue. Storage reachability is not part of readiness; see the
  startup storage check under [Jobs and storage](#jobs-and-storage).
- Both stay open when auth is on.
- `GET /v1/limits` reports the effective limits. It's under `/v1`, so it needs a key when auth is on, and it only
  says whether auth is required, never anything about keys.

The image has no shell or curl, so probe these from the orchestrator.

`ifExists` is per request, not an environment variable: error (default) | replace. Replacement uses sibling staging for filesystem writes. S3 default writes are conditional; HTTPS follows signer/receiver policy. [Job-file downloads](usage.md#re-reading-job-files-and-overwrite-policy) re-use current storage settings.
