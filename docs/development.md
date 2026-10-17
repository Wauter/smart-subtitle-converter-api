# Development

## Build and test

```bash
dotnet test                         # tests for our layer only (libse itself is trusted upstream)
UPDATE_OPENAPI=1 dotnet test        # after an intended API change: refresh docs/openapi.v1.json
scripts/smoke.sh subtitleedit-converter-api   # Docker smoke test against a built image
docker buildx build --platform linux/arm64 -o type=oci,dest=img.tar . && scripts/image-size.py img.tar slim linux/arm64
```

Running the API locally: [deployment.md](deployment.md#from-source).

## Versioning

Every API change that ships bumps `<Version>` in the csproj (semver: minor for additive, patch for fixes) with a
[CHANGELOG.md](../CHANGELOG.md) entry; see [CONTRIBUTING.md](../CONTRIBUTING.md). A PR-only `version-check` job
enforces it for `src/` changes. `/healthz` and the OpenAPI `info.version` report that version.

## Postman / newman

[`postman/`](../postman) has a v2.1 collection covering every endpoint with the examples from [usage.md](usage.md)
and [formats.md](formats.md) (health, formats, detect, profiles list/get/validate, schema, convert with target,
preset, overrides, inline profile, multi-output zip, zip in/out, JSON in/out, limits, binary formats: SRT→STL,
STL→SRT, PAC with a code page, a mixed text/binary zip) and the error cases (unsupported format, read-only target,
truncated STL, bad profile, unknown preset, wrong media type, missing/wrong API key). Each request has test scripts
for status codes and key headers (`X-Request-Id`, `X-Detected-*`, `X-Cue-Count`, `X-Profile`, `X-Entries-*`,
problem+json `code`).

- Import `postman/subtitleedit-converter-api.postman_collection.json` and
  `postman/subtitleedit-converter-api.postman_environment.json`.
- Environment: `baseUrl` (default `http://localhost:4041`), `apiKey` (leave empty for an open server; the
  auth-failure tests are then skipped).
- File parts point at repo files (`tests/smoke/*`, `postman/fixtures/*`) by relative path: in Postman set
  *Settings → General → Working directory* to your checkout.

```bash
npx newman run postman/subtitleedit-converter-api.postman_collection.json -e postman/subtitleedit-converter-api.postman_environment.json \
    --working-dir . --env-var baseUrl=http://localhost:4041 --env-var apiKey=$KEY
```

CI runs it against the built container on amd64 and arm64.

## CI and publishing

CI on every PR and push: build with warnings as errors, tests, vulnerable-package check, `docker compose config`,
then `linux/amd64` + `linux/arm64` images with the smoke test and newman on both, the size budget and Trivy.
When all of that is green:

- push to `main` → `ghcr.io/wauter/subtitleedit-converter-api:{edge,sha-…}` (`latest` doesn't move);
- `vX.Y.Z` tag → `:{X.Y.Z,X.Y,latest,sha-…}` plus a GitHub Release.

Both are multi-arch with SBOM and provenance. PRs never push. Tags and releases are cut only on the owner's
go-ahead. Image tags for users: [deployment.md](deployment.md#image-tags).
