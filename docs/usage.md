# Usage

Every endpoint with copy-paste `curl` examples. The examples assume the API on `localhost:4041` (see
[deployment.md](deployment.md)). The interactive reference is at <http://localhost:4041/docs>, the OpenAPI
document at `/openapi/v1.json` (committed as [openapi.v1.json](openapi.v1.json)).

## Converting

```bash
# SRT -> WebVTT; -OJ saves using the server's file name (movie.vtt)
curl -F file=@movie.srt -F target=webvtt localhost:4041/v1/convert -OJ

# a built-in preset
curl -F file=@movie.srt -F preset=netflix-en-vtt localhost:4041/v1/convert -OJ

# preset + overrides (objects merge, arrays replace, null clears)
curl -F file=@movie.srt -F preset=netflix-en-vtt \
     -F 'profile={"rules":{"maxLineLength":37},"timing":{"offsetMs":-1500}}' localhost:4041/v1/convert -OJ

# TTML -> SRT, and SRT -> ASS
curl -F file=@movie.ttml -F target=srt localhost:4041/v1/convert -OJ
curl -F file=@movie.srt  -F preset=srt-to-ass localhost:4041/v1/convert -OJ

# several outputs at once -> zip with manifest.json
curl -F file=@movie.srt -F target=vtt -F 'profile={"target":{"additional":["ass","ttml"]}}' localhost:4041/v1/convert -OJ
```

Presets, profiles and operations: [profiles.md](profiles.md). Binary formats (EBU STL, PAC, …):
[formats.md](formats.md).

## Batches (zip in, zip out)

```bash
# one manifest entry per file; a bad file doesn't fail the batch unless failFast=true
curl -F file=@season1.zip -F target=vtt localhost:4041/v1/convert -OJ
curl -F file=@season1.zip -F target=vtt -F failFast=true localhost:4041/v1/convert -OJ
```

Entries with the same folder + name (`ep1.srt` and `EP1.vtt`) are rejected up front with
`422 ambiguous-batch-names`.

## JSON in/out (for agents and scripts)

```bash
curl localhost:4041/v1/convert/json -H 'content-type: application/json' \
     -d '{"content":"1\n00:00:01,000 --> 00:00:02,000\nHello\n","fileName":"a.srt","target":"vtt"}'
```

Binary content must be base64 (`"contentEncoding":"base64"`); binary outputs always come back base64. Example in
[formats.md](formats.md#examples).

## Detection

```bash
# what is this file? (text or binary; binary adds codePage/formatDetails and up to 5 candidates)
curl -F file=@mystery.txt localhost:4041/v1/detect
```

## Formats, presets, schema and limits

```bash
curl 'localhost:4041/v1/formats?q=vtt'
curl localhost:4041/v1/profiles                 # built-in + mounted, invalid ones listed with errors
curl localhost:4041/v1/profiles/netflix-en-vtt  # raw + resolved (extends flattened)
curl localhost:4041/v1/profiles/validate -H 'content-type: application/json' -d @my-profile.json
curl localhost:4041/v1/schemas/profile.json     # JSON Schema for editors and agents

# server limits (body caps, zip guards, queue depth, auth required?) before you upload
curl localhost:4041/v1/limits
```

## With API keys enabled

```bash
curl -H "Authorization: Bearer $KEY" -F file=@movie.srt -F target=vtt localhost:4041/v1/convert -OJ
```

How to enable keys: [security.md](security.md#api-keys).

## Response headers

Conversions return `X-Request-Id`, `X-Detected-Format`, `X-Detected-Encoding`, `X-Detected-Code-Page` (binary
input), `X-Output-Kind` (`text`/`binary`), `X-Cue-Count`, `X-Cue-Count-In`, `X-Warnings-Count`, `X-Warnings`,
`X-Profile`, `X-Libse-Version`, `X-Verification-Verdict` (single binary output) and, for zip input,
`X-Entries-Ok` / `X-Entries-Failed` / `X-Entries-Skipped`.

Send your own `X-Request-Id` (`[A-Za-z0-9._-]{1,64}`) to correlate requests; anything else is replaced by a fresh id.

## Errors

Errors are `application/problem+json` with a stable `code`, a `hint` and the `requestId`, which also appears in
the logs:

```json
{ "type": ".../docs/errors.md#invalid-profile", "title": "Invalid profile", "status": 400,
  "code": "invalid-profile", "detail": "The profile has 1 error(s); first at '/target/format': Unknown format 'vtt2'.",
  "hint": "Did you mean 'webvtt'? See GET /v1/formats.", "requestId": "01M52D3QQQP64E77FSF5QXGHJJ",
  "errors": [ { "pointer": "/target/format", "message": "Unknown format 'vtt2'.", "hint": "Did you mean 'webvtt'?" } ] }
```

Every error and warning code: [errors.md](errors.md).
