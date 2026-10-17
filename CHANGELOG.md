# Changelog

All notable API changes. Versions follow [semver](https://semver.org/); the version is the
`<Version>` in `src/SubtitleEditConverterApi/SubtitleEditConverterApi.csproj` and is what
`GET /healthz` (`version`) and the OpenAPI `info.version` report. See [CONTRIBUTING.md](CONTRIBUTING.md)
for the bump rule. Nothing here has been tagged or released yet; the private `:edge` image carries
the version of the `main` commit it was built from.

## 1.1.1 (unreleased)

- Default host port is now **4041** instead of 8080 (8080 is often in use): `docker-compose.yml` publishes
  `127.0.0.1:4041:8080`, bound to localhost only by default (README "Ports" explains how to expose it on other
  interfaces). README, design examples and the Postman collection/environment `baseUrl` use
  `http://localhost:4041`. The container still listens on 8080 internally (`ASPNETCORE_HTTP_PORTS`, `EXPOSE`,
  healthcheck unchanged). No API contract change.

## 1.1.0 (unreleased)

Binary broadcast formats milestone (PR #4) plus the `frame-rate-changed` warning.

- Read and write EBU STL, PAC (incl. Unicode PAC), Cavena 890, CapMaker Plus and Cheetah CAP
  (`formatOptions.ebuStl`, `.pac`, `.cavena890`, `.cheetah`); binary detection with `codePage` and
  `formatDetails`; binary content as base64 in `/v1/convert/json`.
- `GET /v1/limits`: effective body caps, zip guards, queue depth and whether auth is required.
- MCP review fixes A1–A11 (OpenAPI problem schema and security, binding errors, zip entry pointers,
  `504 conversion-timeout` `phase`, ...).
- `422 ambiguous-batch-names`: zip entries sharing folder + stem, or colliding output names, are
  rejected up front instead of renamed.
- New warning `frame-rate-changed` (EBU STL): the written Disk Format Code implies a different frame
  rate than the requested or source rate, e.g. 29.97 fps written as `STL30.01`
  ([docs/errors.md](docs/errors.md#warning-codes), design §13 row 23).
- `/healthz` and OpenAPI `info.version` now report `1.1.0`; OpenAPI `info.version` follows the csproj.

## 1.0.0 (unreleased)

First text-to-text version: `/v1/convert`, `/v1/convert/json`, `/v1/detect`, formats, presets,
profiles and schema endpoints, FIFO conversion queue, optional API keys.
