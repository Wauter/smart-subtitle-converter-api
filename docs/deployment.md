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
