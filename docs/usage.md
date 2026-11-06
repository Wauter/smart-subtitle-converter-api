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

