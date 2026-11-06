# Security

The safeguards that exist today, and how to change them. By default the API is reachable only from your own
machine; exposing it is a deliberate configuration change.

## Defaults

- The reference Compose and the quick start publish the port on `127.0.0.1` only.
- Without API keys the API is open (fine for local and sidecar use); startup logs a `WARN auth.disabled` line.
- The image is a chiseled ASP.NET Core base with no shell or curl, running as a non-root user.
- The reference [`docker-compose.yml`](../docker-compose.yml) adds a read-only root filesystem, a RAM-backed `tmpfs`
  for `/tmp`, `cap_drop: [ALL]` and `no-new-privileges`.

## Exposing it beyond localhost

Enable API keys first (below). Then change the port mapping:

- Compose: `"4041:8080"` (all interfaces) or `"192.168.1.10:4041:8080"` (one interface) in `docker-compose.yml`.
- `docker run`: `-p 4041:8080`.

Behind a reverse proxy, set `KNOWN_PROXIES` so `X-Forwarded-*` headers are trusted only from it. For browser
clients, set `CORS_ORIGINS` to an allow-list.

## API keys

- `API_KEYS`: comma-separated bearer keys, `name:key` allowed.
- `API_KEYS_FILE`: one key per line, `#` comments, `name:key`; reloaded when the file changes. In Compose, mount it
  as a secret (`./api_keys.txt:/run/secrets/api_keys:ro`, `API_KEYS_FILE: /run/secrets/api_keys`).

Clients send `Authorization: Bearer <key>`. `/healthz` and `/readyz` stay open; everything under `/v1` needs a key.
`GET /v1/limits` says whether auth is required, never anything about keys.

## Abuse limits

- `RATE_LIMIT_PER_MINUTE`: per key name, or per client IP when auth is off (`429 rate-limited`).
- Size and shape limits: `MAX_UPLOAD_BYTES`, `PROFILE_MAX_BYTES`, `MAX_CUES`, `MAX_LINE_CHARS`.
- XML with DTD or entity declarations is refused (`422 unsafe-xml`).

All values: [configuration.md](configuration.md).

## Files and logs

- Binary files are handled in a private `0700` temp dir with random names, deleted after every job, including
  timeouts and worker restarts, and swept at startup.
- Error `debug` details never contain file content; set `ERRORS_VERBOSE=false` to remove stack traces and internals.
- Warnings and verification report cue numbers and code points, never subtitle text.

Design background: [design.md §6](design.md#6-security-and-auth).

## HTTPS storage destinations

HTTPS requires exact host allowlisting. Resolution occurs inside the socket connection callback; every returned IP must pass the public-unicast policy, and connections use that checked IP set rather than resolving again. Loopback, private, link-local, unspecified, multicast, reserved/documentation ranges, common tunnels and cloud metadata addresses are rejected, including mapped IPv4. Proxies, cookies, redirects and decompression are disabled. TLS certificate and hostname validation remain enabled. A size cap applies while streaming even without Content-Length. No remote Content-Type is trusted.

For internal MinIO use `s3://` with a server-configured endpoint, not an HTTPS URL resolving to private addresses. Protect allowlist and endpoint configuration like other operator settings. A signed URL is a credential: job records remove its query, and transport errors do not echo it.

## Filesystem, object storage and jobs

Empty root/bucket/host allowlists disable the matching URI scheme. Filesystem paths must remain inside
configured roots and name regular files; dot segments, symlinks and directory destinations are rejected.
Linux directory handles and no-follow opens are used for each path component. Existing output files are
not replaced unless the request sends `ifExists:"replace"`. Outputs are created with mode `0644` (narrowed by the
umask), readable by other accounts on the mount. The only file the API deletes is a stale `{output}.json` sidecar
next to an output written without `sidecar` (filesystem and S3). With atomic writes off, readers may see a growing file; failed writes remove their own partial
file while the process is alive. A crash can leave a partial file. Enable atomic writes when consumers scan
folders, and verify the mount's rename semantics. No filesystem operation crosses into another protocol.

S3 bucket/key-prefix checks precede SDK requests. A custom endpoint is trusted operator configuration,
not an untrusted request target; scope the role's permissions as well as the API allowlists. S3 defaults to a conditional PUT (`If-None-Match: *`); explicit `ifExists:"replace"` permits replacement. Sidecar and subtitle writes are independent, with warnings on sidecar failure. Removing a stale sidecar
needs delete permission on the output prefix; without it the subtitle is still written and `sidecar-delete-failed`
is reported.

Jobs are in-memory metadata. Any valid API key knowing an unguessable id may read metadata and re-read stored files; there is no owner check or admin key. An unauthenticated server grants the same id-based access. Restart clears history; TTL/cap eviction may remove running records without
cancelling work. Run one instance or use sticky routing if polling through a load balancer; there is no shared
store. Jobs do not retain subtitle content, but URIs, profiles and diagnostics may be sensitive. Protect access.
Signed HTTPS queries are excluded from returned metadata; do not put credentials in path names.
