# subtitle-api

HTTP service for converting subtitle files between text formats. It wraps
[Subtitle Edit](https://github.com/SubtitleEdit/subtitleedit)'s `libse` library
(pinned NuGet package) in an ASP.NET Core (.NET 10) API and ships as a small
multi-arch (amd64/arm64) container.

**Status: design phase. There is no application code yet.**

- Design: [docs/design.md](docs/design.md)
- Roadmap: [docs/roadmap.md](docs/roadmap.md)
- MCP server (planned): [Wauter/subtitle-mcp](https://github.com/Wauter/subtitle-mcp)

## Planned v1 scope

- Text-to-text conversion, synchronous, with a bounded queue and a single worker
- `POST /v1/convert` (multipart, zip in/out), `POST /v1/convert/json`, `GET /v1/formats`,
  `POST /v1/detect`, `GET /v1/profiles[/{name}]`, `POST /v1/profiles/validate`,
  `GET /healthz`, `GET /readyz`
- Named presets (built-in and mounted folder), operation pipeline matching `seconv`
- Optional bearer API keys, RFC 7807 errors with stable codes and fix hints
- OpenAPI at `/openapi/v1.json`, Scalar docs at `/docs`

## License

MIT, see [LICENSE](LICENSE). Built on `libse` from Subtitle Edit by Nikolaj Olsson (MIT).
