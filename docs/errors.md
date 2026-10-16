# Error codes

Every error is `application/problem+json` (RFC 7807). Branch on `code`; it never changes within v1.
`code`, `title`, `detail`, `hint` and `requestId` are always present. `errors[]` holds JSON pointers
into the request (profile fields, zip entries). `debug` (exception, stack trace, detected encoding,
first-line shape) is included only when `ERRORS_VERBOSE=true` (the default).

| Status | code | Meaning / what to do |
|---|---|---|
| 400 | <a id="invalid-request"></a>`invalid-request` | Missing `file`/`content`/target, bad base64, both `preset` and `profile.extends`. |
| 400 | <a id="invalid-profile"></a>`invalid-profile` | The profile is invalid; see `errors[]` (pointer + hint). Unknown keys are errors. |
| 400 | <a id="empty-input"></a>`empty-input` | The file (or zip) is empty. |
| 401 | <a id="unauthorized"></a>`unauthorized` | Send `Authorization: Bearer <key>`. |
| 404 | <a id="not-found"></a>`not-found` | No such endpoint. |
| 404 | <a id="preset-not-found"></a>`preset-not-found` | Unknown preset; the hint lists known ones. Invalid mounted presets are listed by `GET /v1/profiles`. |
| 413 | <a id="payload-too-large"></a>`payload-too-large` | Body over `MAX_UPLOAD_BYTES`, or profile over 64 KB. |
| 413 | <a id="zip-limits-exceeded"></a>`zip-limits-exceeded` | Too many entries, too large unpacked, or compression ratio over 100:1. |
| 415 | <a id="unsupported-media-type"></a>`unsupported-media-type` | Use `multipart/form-data`, or `POST /v1/convert/json`. |
| 422 | <a id="format-not-detected"></a>`format-not-detected` | No text format matched; set `profile.source.format`. The hint describes what the input looks like. |
| 422 | <a id="format-not-supported"></a>`format-not-supported` | Image/binary/container formats are not in v1. |
| 422 | <a id="encoding-error"></a>`encoding-error` | Reserved: the input could not be decoded; set `encoding.input`. |
| 422 | <a id="unsafe-xml"></a>`unsafe-xml` | DTD / entity declarations are not allowed. |
| 422 | <a id="unsafe-zip"></a>`unsafe-zip` | A zip entry name contains `..`, an absolute path, a drive letter or a backslash. |
| 422 | <a id="input-limits-exceeded"></a>`input-limits-exceeded` | More than `MAX_CUES` cues or a line over `MAX_LINE_CHARS`. |
| 422 | <a id="conversion-failed"></a>`conversion-failed` | An operation or writer threw; `debug.operation` names it. |
| 429 | <a id="rate-limited"></a>`rate-limited` | Over `RATE_LIMIT_PER_MINUTE`; see `Retry-After`. |
| 503 | <a id="queue-full"></a>`queue-full` | Conversion queue full; retry after `Retry-After` seconds or scale out. |
| 504 | <a id="conversion-timeout"></a>`conversion-timeout` | Over `CONVERSION_TIMEOUT_SECONDS`; split the file or simplify operations. |
| 500 | <a id="internal-error"></a>`internal-error` | A bug. Report it with the `requestId`. |
