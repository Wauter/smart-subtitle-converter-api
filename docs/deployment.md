# Deployment

## Docker

An API key is required by default. Create an empty key file the container user (UID 1654) can write, and mount it
read-write at `/api_keys.txt`; a key is generated into it on first start ([Securing the API](security.md#securing-the-api)):

```bash
touch api_keys.txt && sudo chown 1654 api_keys.txt && chmod 600 api_keys.txt
docker run -d --name smart-subtitle-converter -p 127.0.0.1:8080:8080 --restart unless-stopped \
  -v "$PWD/api_keys.txt:/api_keys.txt" ghcr.io/wauter/smart-subtitle-converter-api:latest
```

If the file is missing, Docker creates a folder named `api_keys.txt` instead, and the API stops with exit code 78
and a message saying how to fix it (the same for a read-only empty file). Use `-e API_KEYS=<key>` instead of the
file if you prefer, or `-e API_AUTH_DISABLED=true` for throwaway local tests only.

To use your own presets, also mount a folder at `/profiles`:

```bash
docker run -d --name smart-subtitle-converter -p 127.0.0.1:8080:8080 --restart unless-stopped \
  -v "$PWD/api_keys.txt:/api_keys.txt" -v "$PWD/profiles:/profiles:ro" ghcr.io/wauter/smart-subtitle-converter-api:latest
```

Keep a restart policy (`--restart unless-stopped`, `restart: unless-stopped` in Compose, or your orchestrator's):
when a conversion gets stuck past its grace period, the API exits with code 70 on purpose so a clean process starts.
Exit code 78 means a configuration problem; the log says what to change, and restarting won't help until you do.

## Docker Compose

The repo's [`docker-compose.yml`](../docker-compose.yml) is the reference setup: host port 8080 on `127.0.0.1`,
read-only rootfs, `tmpfs` `/tmp`, `./profiles` and `./api_keys.txt` (read-write) mounted, `ALLOWED_HOSTS` set to the
service name, all capabilities dropped and `restart: unless-stopped` (see [security.md](security.md)).

```bash
touch api_keys.txt && sudo chown 1654 api_keys.txt && chmod 600 api_keys.txt   # once
docker compose up -d           # pull the image
docker compose up --build -d   # or build it locally from this checkout
```

Put settings in a `.env` next to `docker-compose.yml` (start from [`.env.example`](../.env.example)). The
reference file loads it into the container with `env_file` (`required: false`, so `up` still works without one;
needs Docker Compose 2.24 or later). Compose's `.env` on its own only feeds `${...}` interpolation and never
reaches the container: without `env_file` or `environment` entries, `S3_ALLOWED_BUCKETS` set in `.env` is ignored
and the startup log shows `storage.config var=S3_ALLOWED_BUCKETS value=<unset> effective="(unset — S3 off)"`.
Variables under `environment:` override the same name from `.env`. To run your own build from a checkout, set
`image:` to a local tag (for example `smart-subtitle-converter-api:dev` after `docker build -t
smart-subtitle-converter-api:dev .`).

Environment variables: [configuration.md](configuration.md).

## Image tags

Images are multi-arch (`linux/amd64` + `linux/arm64`) with SBOM and provenance.

| Tag | Moves when |
|---|---|
| `:latest` | A release is tagged |
| `:X.Y.Z`, `:X.Y` | Fixed to a release (`:X.Y` follows its latest patch) |
| `:edge` | Every green push to `main` |
| `:sha-<short commit>` | Never; one build |

Pin a release (or `:sha-…`) for anything you don't want to change under you. Versions and what changed:
[CHANGELOG.md](../CHANGELOG.md).

A private registry (or a private fork's package) needs a login first, with a token that has the `read:packages` scope:

```bash
echo "$GHCR_TOKEN" | docker login ghcr.io -u <github-user> --password-stdin
```

## The image

libse is a pinned NuGet package inside the image; `/healthz` (`libse`) and the `X-Libse-Version` header report
its version.

The image is plain `aspnet:*-noble-chiseled` plus a trimmed ICU/tzdata overlay, so it keeps full globalization
but is smaller than `-chiseled-extra` (see [design.md §13](design.md#13-v1-implementation-notes-and-deviations)).
Sizes are measured on every CI run (job summary). `--build-arg RUNTIME_BASE=base-extra` builds on the stock
`-chiseled-extra` base instead.

```bash
docker build -t smart-subtitle-converter-api .
docker run --rm -p 127.0.0.1:8080:8080 -v "$PWD/profiles:/profiles:ro" smart-subtitle-converter-api
```

The image has no shell or curl; probe `/healthz` and `/readyz` from the orchestrator
([configuration.md](configuration.md#health-and-limits)).

## From source

Needs the .NET SDK matching `TargetFramework` in `src/SmartSubtitleConverterApi/SmartSubtitleConverterApi.csproj`.

```bash
dotnet run --project src/SmartSubtitleConverterApi   # listens on http://localhost:5000 (ASPNETCORE_HTTP_PORTS to change)
```

## Mounted subtitle storage

On a Linux host, mount the files into the container and allowlist the container path, not the host path:

```yaml
services:
  api:
    volumes:
      - /srv/subtitles:/data
    environment:
      STORAGE_FILE_ROOTS: /data
      STORAGE_FILE_ATOMIC: "false"
```

Parent directories must exist. Match the container UID/GID to permissions on the mount; check NFS root-squash and SMB ownership mapping. Files are created with mode 0644, narrowed by the process umask: other accounts on the mount (MAM, playout) can read deliveries; write access stays with the API user. The app speaks filesystem operations, not NFS/SMB/Aspera/Signiant protocols. Atomic mode uses a sibling temporary file and a no-replace rename, never a cross-device copy.

Set `user` to a UID/GID that can access the mount; avoid running the API as root to paper over permissions.
For a read-only input tree and a separate writable output tree, mount each separately and allowlist both
container roots (a JSON array avoids path-separator confusion). Container read-only-root settings still permit
an explicitly writable storage mount. Direct writes are the default; atomic mode requires rename support.

## S3 permissions

The API calls exactly six S3 operations, always on a bucket in `S3_ALLOWED_BUCKETS` and a key inside
`S3_ALLOWED_PREFIXES`. It lists keys only for `GET /v1/storage/list` with `STORAGE_LIST_ENABLED=true`, and never copies, starts multipart uploads, sets ACLs or tags, sends
server-side-encryption headers, or creates or deletes buckets. Credentials come from the default AWS chain
(see [startup diagnostics](configuration.md#startup-storage-diagnostics) for which source was used).

| S3 call | IAM action | Resource | What uses it | What fails without it |
|---|---|---|---|---|
| `GetObject` | `s3:GetObject` | object `arn:aws:s3:::subs/in/*` | Reading an `s3://` `input` (`/v1/convert`, `/v1/convert/json`, `/v1/detect`); re-reading the stored file for `GET /v1/jobs/{id}/input` and `/output` | Those requests fail with `502 storage-io-failed` (S3 HTTP 403) |
| `PutObject` with `If-None-Match: *` (default) | `s3:PutObject` | object `arn:aws:s3:::subs/out/*` | Writing `output`, and `{output}.json` with `sidecar:true`, without overwriting | Writes fail with `502 storage-io-failed` (HTTP 403). An existing key is not a permission problem: S3 answers 412 and the API returns `409 output-exists` |
| `PutObject` (with `ifExists:"replace"`) | `s3:PutObject` | object | Overwriting an existing output or sidecar | Same as above |
| `HeadObject` | `s3:GetObject` (S3 has no separate HeadObject action) | object | After writing an output without `sidecar:true`, checking whether a stale `{output}.json` is left from an earlier job | The conversion still succeeds, with warning `sidecar-delete-failed` |
| `DeleteObject` | `s3:DeleteObject` | object | Deleting that stale `{output}.json`. Only called when it exists | Warning `sidecar-delete-failed`; the stale sidecar stays |
| `ListObjectsV2` (delimiter `/`, 200 keys a page) | `s3:ListBucket` (scope it with an `s3:prefix` condition to the allowed prefixes) | bucket `arn:aws:s3:::subs` | `GET /v1/storage/list` when `STORAGE_LIST_ENABLED=true` | Listing fails with `502 storage-io-failed` (HTTP 403); nothing else is affected |
| `HeadBucket` | `s3:ListBucket` | bucket `arn:aws:s3:::subs` | The [startup check](configuration.md#startup-storage-diagnostics), once per bucket | Startup warning `storage.s3 bucket=subs warning=Forbidden status=403`; conversions are not affected |

`s3:ListBucket` also changes how S3 reports a missing key. With it, S3 answers 404, so a missing input gives
`404 storage-not-found`, a deleted job file gives `404 job-file-gone`, and the stale-sidecar check simply finds
nothing. Without it, S3 answers 403 for every missing key: those become `502 storage-io-failed`, and every
S3 write without `sidecar:true` carries a `sidecar-delete-failed` warning. `s3:ListBucket` lets the credentials
list key names in the bucket, not read objects; the API itself lists only for `GET /v1/storage/list`, and only inside
the allowed prefixes.

### Per access pattern

| Action | Input-only bucket/prefix | Output-only bucket/prefix | Input and output |
|---|---|---|---|
| `s3:GetObject` on objects | Required | Recommended (job `/output` download, stale-sidecar check) | Required |
| `s3:PutObject` on objects | Not needed | Required | Required |
| `s3:DeleteObject` on objects | Not needed | Optional (stale-sidecar cleanup) | Optional |
| `s3:ListBucket` on the bucket | Recommended (startup check, 404s); required for listing | Recommended; required for listing | Recommended; required for listing |
| `kms:Decrypt` on the key (SSE-KMS only) | Required | Required if `s3:GetObject` is granted | Required |
| `kms:GenerateDataKey` on the key (SSE-KMS only) | Not needed | Required | Required |

The strict minimum is `s3:GetObject` for inputs and `s3:PutObject` for outputs. The rest buys clean 404s, the
job file routes, sidecar cleanup and a quiet startup check.

### ARNs and prefixes

- Object actions (`s3:GetObject`, `s3:PutObject`, `s3:DeleteObject`) take the **object** ARN,
  `arn:aws:s3:::subs/*` or a prefix such as `arn:aws:s3:::subs/in/*`.
- `s3:ListBucket` takes the **bucket** ARN, `arn:aws:s3:::subs`, without `/*`. Granting it on `subs/*` has no
  effect, which is a common cause of `HeadBucket` 403s.
- Scope object ARNs to the same prefixes as `S3_ALLOWED_PREFIXES`. `S3_ALLOWED_PREFIXES` is a plain string prefix
  that applies to both input and output: `in` also matches `input/...`, so end prefixes with `/` in both places.
  IAM is what separates read-only input prefixes from writable output prefixes.
- You can limit `s3:ListBucket` with an `s3:prefix` condition. `HeadBucket` sends no prefix, so the startup check
  then reports 403. That warning is harmless.
- If a bucket policy enforces conditional writes with the `s3:if-none-match` condition key, requests with
  `ifExists:"replace"` are denied.

### Encryption (SSE-KMS)

SSE-S3, the S3 default, needs no extra permission. With SSE-KMS the API relies on the bucket's default
encryption: it sends no encryption headers, so a bucket policy that denies `PutObject` without
`x-amz-server-side-encryption` denies every write. Use default bucket encryption instead. With a customer managed
KMS key, grant `kms:Decrypt` (reads) and `kms:GenerateDataKey` (writes) on the key ARN, and make sure the key
policy allows the same principal. The AWS managed key `aws/s3` in the same account usually needs no extra
statement.

### Minimal policy

For `S3_ALLOWED_BUCKETS=subs` and `S3_ALLOWED_PREFIXES=in/,out/`, with inputs read from `in/` and outputs
written to `out/`:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "BucketCheckAnd404s",
      "Effect": "Allow",
      "Action": "s3:ListBucket",
      "Resource": "arn:aws:s3:::subs"
    },
    {
      "Sid": "ReadInputs",
      "Effect": "Allow",
      "Action": "s3:GetObject",
      "Resource": "arn:aws:s3:::subs/in/*"
    },
    {
      "Sid": "WriteOutputs",
      "Effect": "Allow",
      "Action": ["s3:PutObject", "s3:GetObject", "s3:DeleteObject"],
      "Resource": "arn:aws:s3:::subs/out/*"
    }
  ]
}
```

For SSE-KMS with a customer managed key, add:

```json
{
  "Sid": "UseBucketKmsKey",
  "Effect": "Allow",
  "Action": ["kms:Decrypt", "kms:GenerateDataKey"],
  "Resource": "arn:aws:kms:<region>:<account-id>:key/<key-id>"
}
```

### MinIO

MinIO accepts the same policy JSON (same actions and ARNs, without the KMS statement). Create it with
`mc admin policy create <alias> subtitle-api policy.json` and attach it with
`mc admin policy attach <alias> subtitle-api --user <access-key>`. Set `S3_ENDPOINT`,
`S3_FORCE_PATH_STYLE=true` and `S3_REGION` to the server's region (`us-east-1` unless `MINIO_REGION` says
otherwise). Older MinIO releases may ignore `If-None-Match`, which turns the default no-overwrite write into an
overwrite: check that writing the same `output` twice returns `409 output-exists`.

## Compatible object storage

For an internal MinIO service, set `S3_ENDPOINT=http://minio:9000`, `S3_REGION=us-east-1`,
`S3_FORCE_PATH_STYLE=true` and a narrow `S3_ALLOWED_BUCKETS` / `S3_ALLOWED_PREFIXES` list. Provision buckets
outside this API. The credentials need the [S3 permissions](#s3-permissions) above (the same policy works on
MinIO). Inject credentials using your runtime's environment or role configuration; do not bake them
into an image, Compose file or request. Public HTTPS links instead use `HTTP_ALLOWED_HOSTS`; private IPs
remain blocked for those links. See [configuration](configuration.md) and [the sample environment](../.env.example).
After a restart, check the logs for `storage.config` / `storage.s3` / `storage.file` lines and the closing
`storage.check done` line: the [startup diagnostics](configuration.md#startup-storage-diagnostics) print the
effective settings and warn about malformed values, missing credentials, an unreachable bucket, a wrong region or a
missing mount, each with a fix, without blocking readiness.

Each replica owns its own job metadata. Restart loses it; poll through sticky routing or a single replica.
There is no persisted history or cached-result download; job file routes re-read storage. The caller wait does not cancel an admitted job.
