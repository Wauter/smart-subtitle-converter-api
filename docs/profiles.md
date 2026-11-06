# Presets and profiles

A **profile** is a JSON document that describes a conversion: source and target format, rules, timing, format
options and operations. A **preset** is a named profile. The JSON Schema is served at `/v1/schemas/profile.json`;
validate your own with `POST /v1/profiles/validate`.

## Built-in presets

`srt-to-vtt`, `clean-srt`, `netflix-en-vtt`, `pal-to-film`, `srt-to-ass`, `to-ttml`. `GET /v1/profiles` lists every
preset in effect; `GET /v1/profiles/{name}` shows one raw and resolved (`extends` flattened).

## Your own presets

Add `*.json` files to the folder mounted at `PROFILES_DIR` (default `/profiles`; see
[deployment.md](deployment.md) for the mount). The file name is the preset name (`[a-z0-9-]+`). The folder is
re-read when it changes, no restart needed. A mounted preset with the same name as a built-in one replaces it
(shown as `shadows` in `GET /v1/profiles`). Invalid presets are listed with their errors.

Example: [profiles/example-house-style.json](../profiles/example-house-style.json).

## Overrides

Send `preset` and `profile` together to adjust a preset per request: objects merge, arrays replace, `null` clears.

```bash
curl -F file=@movie.srt -F preset=netflix-en-vtt \
     -F 'profile={"rules":{"maxLineLength":37},"timing":{"offsetMs":-1500}}' localhost:8080/v1/convert -OJ
```

## Operations

Operations run in array order; names and behaviour follow Subtitle Edit's `seconv`:

- `RemoveTextForHI`
- `FixCommonErrors` (`rules`, `language`)
- `RemoveFormatting` (`rules`)
- `MergeSameTexts`, `MergeSameTimeCodes`, `MergeShortLines`
- `SplitLongLines`, `BalanceLines`, `RemoveLineBreaks`
- `RedoCasing`
- `ApplyDurationLimits`, `BeautifyTimeCodes`
- `ConvertColorsToDialog`
- `FixRtlViaUnicodeChars`, `RemoveUnicodeControlChars`, `ReverseRtlStartEnd`
- `DeleteFirst` / `DeleteLast` (`count`), `DeleteContains` (`text`)
- `MultipleReplace` (`rules: [{find, replace, mode: text|caseSensitive|regex}]`)

Format-specific options (`formatOptions.ebuStl`, `pac`, `cavena890`, `cheetah`): [formats.md](formats.md#format-options).
Design background: [design.md §5](design.md#5-profile-json-schema-draft).
