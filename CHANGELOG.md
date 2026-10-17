# Changelog

All notable API changes. Versions follow [semver](https://semver.org/); the version is the
`<Version>` in `src/SubtitleEditConverterApi/SubtitleEditConverterApi.csproj` and is what
`GET /healthz` (`version`) and the OpenAPI `info.version` report. See [CONTRIBUTING.md](CONTRIBUTING.md)
for the bump rule. Nothing here has been tagged or released yet; the private `:edge` image carries
the version of the `main` commit it was built from.

## 1.2.0 (unreleased)

Conversion verification and diagnostics (client feedback 2026-10-17, approved by Wauter).

- **Structured read-back verification** for every binary output: `outputs[].verification` (JSON),
  `entries[].verification` keyed by output path (zip manifests), `X-Verification-Verdict` (single binary
  output, multipart). `verdict` is `lossless` | `quantised` | `lossy` | `unverified`, with `cuesWritten`,
  `cuesReadBack`, `timing` (`maxShiftMs`, `meanShiftMs`, `frameMs`, `cuesOverOneFrame`), `replacedChars`
  (cue numbers and code points, first 20, exact totals), `truncatedCues`, `missingCues`, `zeroLengthCues`.
  When cue counts differ, cues are matched by start time (1.1 skipped every check). A read-back that throws
  now gives `unverified` with a `reason` and a `verification-failed` warning (1.1 stayed silent). Positions
  and code points only, never cue text, in responses and logs. See [docs/errors.md](docs/errors.md#verification-binary-outputs-since-120).
- **Warnings carry `hint` and `details`** (JSON body and manifest only; `X-Warnings` keeps code, message,
  count). `characters-replaced` names the script, the cues and the code points, and suggests the remedy
  for the target (`formatOptions.pac.codePage`, `formatOptions.ebuStl.characterTable`,
  `formatOptions.cavena890.language`, or target `fpc`); nothing is switched automatically.
- New warning codes: `text-truncated` (Cavena 890 / Cheetah CAP Old cut lines; no longer counted as
  replaced characters), `zero-length-cue`, `source-replacement-chars` (U+FFFD already in the source; no
  longer counted as an encoding loss), `verification-failed`. A zero-length cue written to EBU STL now
  gets a `lossy-format` warning naming the cue and the 99:59:59,999 read-back, instead of "Timing changed
  by up to 359999999 ms".
- `POST /v1/detect` reports `bom` and `lineEndings` (crlf | lf | cr | mixed | none) for text input, and
  `warnings` (`zero-length-cue`, `source-replacement-chars`).
- **Behaviour change: `422 ambiguous-format`.** PAC and PAC Unicode (FPC) share their header bytes. When
  auto-detection finds bytes valid as both and no `.pac` / `.fpc` file name decides, the input is now
  rejected (`candidates: ["pacscreenelectronics", "pacunicodeunipac"]`, hint to set
  `profile.source.format`). 1.1 silently picked PAC, so FPC files without their extension were reported as
  PAC. Correct extensions and an explicit `source.format` work as before; zip entries are checked by entry
  name. PAC files that only parse as PAC (e.g. the Greek PAC fixture) are unaffected.
- Known libse 5.1.0 limits now visible through verification: Cavena 890 doesn't round-trip Greek,
  Cyrillic or Arabic (garbled, or its reader throws on its own Greek output); Hebrew and Chinese do.

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
