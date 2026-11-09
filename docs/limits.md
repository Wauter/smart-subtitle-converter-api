# Limits

`GET /v1/limits` returns effective settings with `Cache-Control: no-store`; it requires the same API key
as other API routes. It exposes whether auth is enabled, not keys, roots, buckets or host allowlists.

- `maxUploadBytes`, `maxRequestBodyBytes`, `json`, `maxProfileBytes`, `maxCues`, `maxLineChars`:
  decoded source size, framing/base64 budgets and conversion input limits. URI downloads have the same cap.
- `conversionTimeoutSeconds`, `queue.capacity`, `queue.depth`: conversion run/queue budgets and live depth.
- `requestMaxWaitSeconds`: maximum accepted caller wait.
- `requestWaitSeconds`: default caller wait (per-request `Prefer: wait` accepts 0..60).
- `io.timeoutSeconds`, `io.retries`: per-attempt timeout and number of **extra** transient attempts.
- `jobs.maxEntries`, `jobs.ttlSeconds`: history cap and TTL from creation, not from completion.
- `jobs.maxActive`: cap on work, including transfers, unaffected by history eviction.
- `jobs.maxMetadataBytes`: result/error metadata budget; exceeding it replaces detail with a summary.
- `storage.inputSchemes`, `storage.outputSchemes`: configured `file`, `https`, `s3` schemes. Filesystem
  support is Linux-only. An enabled scheme still needs working permissions/credentials/network access;
  the scheme list is configuration only. Warning-only startup diagnostics log the effective settings,
  malformed values, the AWS credential source, unreachable S3 buckets and missing filesystem roots (see
  [startup storage diagnostics](configuration.md#startup-storage-diagnostics)).
- `rateLimitPerMinute`, `auth.required`, `binary.ebuStlMaxBytes`: request rate, bearer requirement and
  the reader's EBU STL size bound.

There are no archive limits. Submit one subtitle per job. Job bytes are not cached. The job input/output routes re-read stored locations, not original upload or response bytes. Use an output URI if work may outlive the wait. More: [configuration](configuration.md),
[usage](usage.md#waiting-and-polling), [errors](errors.md).
