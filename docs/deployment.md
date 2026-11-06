# Deployment

## Docker

```bash
docker run -d --name subtitleedit-converter -p 127.0.0.1:4041:8080 ghcr.io/wauter/subtitleedit-converter-api:latest
```

To use your own presets, mount a folder at `/profiles`:

```bash
docker run -d --name subtitleedit-converter -p 127.0.0.1:4041:8080 \
  -v "$PWD/profiles:/profiles:ro" ghcr.io/wauter/subtitleedit-converter-api:latest
```

## Docker Compose

The repo's [`docker-compose.yml`](../docker-compose.yml) is the reference setup: host port 4041 on `127.0.0.1`,
read-only rootfs, `tmpfs` `/tmp`, `./profiles` mounted, all capabilities dropped (see [security.md](security.md)).

```bash
docker compose up -d           # pull the image
docker compose up --build -d   # or build it locally from this checkout
```

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
docker build -t subtitleedit-converter-api .
docker run --rm -p 127.0.0.1:4041:8080 -v "$PWD/profiles:/profiles:ro" subtitleedit-converter-api
```

The image has no shell or curl; probe `/healthz` and `/readyz` from the orchestrator
([configuration.md](configuration.md#health-and-limits)).

## From source

Needs the .NET SDK matching `TargetFramework` in `src/SubtitleEditConverterApi/SubtitleEditConverterApi.csproj`.

```bash
dotnet run --project src/SubtitleEditConverterApi   # listens on http://localhost:5000 (ASPNETCORE_HTTP_PORTS to change)
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

Parent directories must exist. Match the container UID/GID to permissions on the mount; check NFS root-squash and SMB ownership mapping. Files are created with mode 0600 (adjust downstream access through the host). The app speaks filesystem operations, not NFS/SMB/Aspera/Signiant protocols. Atomic mode uses a sibling temporary file and a no-replace rename, never a cross-device copy.


Set `user` to a UID/GID that can access the mount; avoid running the API as root to paper over permissions.
For a read-only input tree and a separate writable output tree, mount each separately and allowlist both
container roots (a JSON array avoids path-separator confusion). Container read-only-root settings still permit
an explicitly writable storage mount. Direct writes are the default; atomic mode requires rename support.

## Compatible object storage

For an internal MinIO service, set `S3_ENDPOINT=http://minio:9000`, `S3_REGION=us-east-1`,
`S3_FORCE_PATH_STYLE=true` and a narrow `S3_ALLOWED_BUCKETS` / `S3_ALLOWED_PREFIXES` list. Provision buckets
outside this API. Inject credentials using your runtime's environment or role configuration; do not bake them
into an image, Compose file or request. Public HTTPS links instead use `HTTP_ALLOWED_HOSTS`; private IPs
remain blocked for those links. See [configuration](configuration.md) and [the sample environment](../.env.example).

Each replica owns its own job metadata. Restart loses it; poll through sticky routing or a single replica.
There is no persisted history or result-download endpoint. The caller wait does not cancel an admitted job.
