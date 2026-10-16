# Roadmap

Extracted from [design.md §11](design.md#11-roadmap). The design doc is the source of truth; keep the two in sync.

| Phase | Scope | Exit criteria |
|---|---|---|
| **v1.0: text API** | design §4 endpoints, option A adapter (design §2.1), queue, optional API keys, safeguards, built-in presets, multi-arch image | All CI gates green; smoke corpus passes with the real sample files |
| **v1.x: presets and hardening** | Mounted-preset hot reload, settings-hash fast path (no lock), `MultipleReplace` import of SE `.template`/`.csv`, OpenTelemetry traces, more smoke samples, the upstream PRs (format registration, explicit settings, `libseconv`) | p95 under 50 ms for 1 k cues |
| **MCP server** | `subtitle-mcp` 1.0 (design.md §10) | Works in Claude Desktop, Cursor and an HTTP client; contract tests green |
| **OCR (separate image tag)** | `:X.Y-ocr` adds Tesseract and traineddata (about +100–300 MB, depending on languages), plus image inputs (sup, VobSub). AI engines (PaddleOCR, llama.cpp vision) go in `:X.Y-ocr-ai` (GBs, possibly GPU), or are delegated to an external endpoint (Ollama or `llama-server` URL). This needs libuilogic, so either option B or C from design §2.1 is a prerequisite. Introduces **async jobs**: `POST /v1/jobs` → `202` + `Location`, `GET /v1/jobs/{id}`, result download, in-memory queue first and then Redis or a similar store. | The text-only image stays unchanged in size |
| **Container extraction** | MKV/MKS, MP4, TS teletext and MXF text tracks via libse `ContainerFormats`, with `GET /v1/inspect` listing tracks and `trackNumbers`/`forcedOnly` in the profile. Large uploads move to async jobs or streaming (multi-GB media → presigned URL or chunked upload) | Upload limits are configurable per endpoint |
| **Saved server profiles** | Server-side CRUD `/v1/profiles` (DB-backed) with per-key ownership, plus an audit log. Mounted and built-in presets stay read-only. SSO and tenants still come from a gateway. | Read-only presets are unaffected |
