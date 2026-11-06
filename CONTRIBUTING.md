# Contributing

## Versioning

Every API change that ships bumps `<Version>` in
`src/SubtitleEditConverterApi/SubtitleEditConverterApi.csproj` (semver), in the same PR:

- **minor** (`x.y.z` → `x.(y+1).0`) for additive changes: new endpoints, fields, formats, options, warning codes;
- **patch** (`x.y.z` → `x.y.(z+1)`) for fixes that don't change the contract;
- **major** only for breaking changes to `/v1` (which should instead become `/v2`).

Add a [CHANGELOG.md](CHANGELOG.md) entry with it. `/healthz` `version` and the OpenAPI `info.version`
come from the csproj, so clients (the MCP server among them) can log and compare it.

CI's `version-check` job fails a PR that changes `src/` without raising the version above its merge
base. For a change under `src/` that really doesn't ship (comments, a refactor with no behaviour
change), label the PR `skip-version-check` (the job reads the labels of the triggering event, so add the label before
pushing, or push again after labelling). Renovate PRs are exempt; a dependency bump is folded into
the next versioned change.

Bumping the version is not a release: tags (`vX.Y.Z`) and GitHub Releases are cut only on the owner's go-ahead.

## Tests and contract

- `dotnet test` covers our layer only (libse itself is trusted upstream).
- After an intended API change, `UPDATE_OPENAPI=1 dotnet test` refreshes `docs/openapi.v1.json`.
- New error codes go in `ErrorCodes`, `docs/errors.md` and the OpenAPI enum (a test keeps them in sync);
  new warning codes go in `docs/errors.md` too. Each code gets an index row and a section with plain-language
  **What** / **Why** / **What to do**, followed by the technical details for integrators.
- Keep the Postman collection (`postman/`) in step; CI runs it with newman against the container.
