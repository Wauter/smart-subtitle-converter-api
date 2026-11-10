# Security

The safeguards that exist today, and how to change them. By default the API is reachable only from your own
machine; exposing it is a deliberate configuration change.

## Securing the API

This is alpha software. The defaults below aim to be safe for one machine or a private network; please review
them before you put the API anywhere others can reach.

### Defaults

- An API key is required for everything under `/v1`. `/healthz`, `/readyz` and `/metrics` stay open for probes.
- The reference Compose and the quick start publish the port on `127.0.0.1` only.
- The API answers only to known host names (`ALLOWED_HOSTS`) and refuses browser requests from other sites.
- Error responses are short (`ERRORS_VERBOSE=false`); details go to the server log under the request id.
- Hidden files and folders, non-subtitle files and symlinks are never read, listed or written.
- The image is a chiseled ASP.NET Core base with no shell or curl, running as a non-root user (UID 1654).
- The reference [`docker-compose.yml`](../docker-compose.yml) adds a read-only root filesystem, a RAM-backed `tmpfs`
  for `/tmp`, `cap_drop: [ALL]`, `no-new-privileges` and `restart: unless-stopped`.

### Step 1: the API key file

The image reads keys from `API_KEYS_FILE=/api_keys.txt`. Create an empty file on the host, let the container user
write it, and mount it read-write:

```sh
touch api_keys.txt
chmod 600 api_keys.txt && sudo chown 1654 api_keys.txt
```

```yaml
services:
  smart-subtitle-converter-api:
    environment:
      ALLOWED_HOSTS: "smart-subtitle-converter-api"
    volumes:
      - ./api_keys.txt:/api_keys.txt      # read-write, a file (not a folder)
    restart: unless-stopped
```

On first start the API sees the file has no key, writes a strong random key into it (in place, mode `0600`) and
logs `auth.key-generated file=/api_keys.txt ...` with how to use it. The key itself is never printed, not even
partly. Read it from the file on the host (`cat api_keys.txt`) and send it as `Authorization: Bearer <key>`.

- Your own keys: put one per line in the file (`#` comments, optional `name:key` so logs show a name, never the
  key). The file is re-read when it changes; no restart needed.
- `API_KEYS=key1,key2` adds keys from the environment (with or without the file).
- Startup stops (exit code `78`) with a message saying what to do when the file is missing, is a folder (Docker
  creates a folder when the host file did not exist when the container started), can't be read, or has no key and
  can't be written. Startup logs show how many keys were loaded, never the keys.
- `API_AUTH_DISABLED=true` turns auth off. Use it only for throwaway local tests: anyone who can reach the port can
  use the API, and the storage locations it is configured for. Startup logs a loud warning (louder when storage is
  configured).

### Step 2: give the MCP the key

The Smart Subtitle Converter MCP sends the key for you. Set `SUBTITLE_API_KEY_FILE` to a file holding the key (comment
lines are skipped; the first key is used, and the file is re-read when it changes), or set `SUBTITLE_API_KEY=<key>` in
the MCP client configuration. When the MCP runs on the same machine as your user, point it at `api_keys.txt` directly
if your user can read it. In Docker, the MCP runs as another user than the API (65532, not 1654), so give it a copy:

```sh
sudo grep -v -e '^#' -e '^$' api_keys.txt | head -n 1 > mcp_api_key.txt
chmod 600 mcp_api_key.txt && sudo chown 65532 mcp_api_key.txt
```

and mount `mcp_api_key.txt` read-only into the MCP container. For Postman, set the environment variable `apiKey`.

### Step 3: host names

`ALLOWED_HOSTS` lists the host names callers use to reach the API, comma-separated. `localhost`, `127.0.0.1` and
`[::1]` are always allowed. Add the Compose service name when other containers call it
(`http://smart-subtitle-converter-api:8080`), and the public name when it sits behind a proxy. Any other `Host`
header gets `400 host-not-allowed`, which stops web pages from reaching the API through DNS tricks. `*` accepts any
host and logs a warning; use it only behind a proxy that checks the host itself.

Browsers send an `Origin` header. Requests without one (curl, scripts, the MCP) are not affected. When present, its
host and port must match the request's own `Host` (scheme is ignored; a missing port is 80 or 443) and that host must
be allowed, otherwise `403 origin-not-allowed`. A page on another port, even on localhost, is refused. The same
host-and-port match applies with `ALLOWED_HOSTS=*`. The API sends no CORS headers at all: a web page should not hold
an API key, so browser front ends call their own backend, which calls the API.

### Step 4: exposing it beyond localhost

With keys and host names in place, change the port mapping:

- Compose: `"8080:8080"` (all interfaces) or `"192.168.1.10:8080:8080"` (one interface) in `docker-compose.yml`.
- `docker run`: `-p 8080:8080`.

Behind a reverse proxy, set `KNOWN_PROXIES` so `X-Forwarded-*` headers are trusted only from it. Put TLS on the
proxy: keys travel in a header.

### A stuck conversion restarts the process

libse can't be stopped mid-conversion. When one runs past `CONVERSION_TIMEOUT_SECONDS`, the request gets
`504 conversion-timeout`, and no other conversion starts beside it: new ones get `503 conversion-engine-stuck` with
`Retry-After`, and `/readyz` reports not ready so a load balancer can route elsewhere. If the stuck run finishes, the
API carries on by itself. If it is still running after `CONVERSION_STUCK_GRACE_SECONDS` (by default twice the
timeout), the API logs what it was doing and exits with code `70`. Your restart policy (`restart: unless-stopped` in
Compose, or the orchestrator's) then starts a clean process. Set `DOTNET_DbgEnableMiniDump=1` (and a dump folder) if
you want a crash dump with every thread's stack for a bug report. Browsing and job file reads keep working while a
conversion is stuck.

## Abuse limits

- `RATE_LIMIT_PER_MINUTE`: per key name, or per client IP when auth is off (`429 rate-limited`).
- Size and shape limits: `MAX_UPLOAD_BYTES`, `PROFILE_MAX_BYTES`, `MAX_CUES`, `MAX_LINE_CHARS`.
- XML with DTD or entity declarations is refused (`422 unsafe-xml`), also after leading byte-order marks,
  zero-width characters or whitespace. XML is parsed with DTD processing prohibited, no resolver and entity limits.
- Listing a folder stops at 5000 matching entries (`422 folder-too-large`); narrow it with `prefix`.
- Listing, reading by location and job file reads share a browse queue (`BROWSE_CONCURRENCY` at once,
  `BROWSE_QUEUE_CAPACITY` waiting for up to `BROWSE_WAIT_SECONDS`, then `503 browse-busy` with `Retry-After`). It is
  separate from the conversion queue, so one can't starve the other.

All values: [configuration.md](configuration.md).

## Files and logs

- Binary files are handled in a private `0700` temp dir with random names, deleted after every job, including
  timeouts and worker restarts, and swept at startup.
- Error `debug` details never contain file content. By default (`ERRORS_VERBOSE=false`) responses carry the code,
  message, hint and request id only; the stack trace and internals are logged as `error.details requestId=...`.
  `ERRORS_VERBOSE=true` puts them back in responses, which helps when you debug locally.
- Warnings and verification report cue numbers and code points, never subtitle text.

Design background: [design.md §6](design.md#6-security-and-auth).

## HTTPS storage destinations

HTTPS requires exact host allowlisting. Resolution occurs inside the socket connection callback; every returned IP must pass the public-unicast policy, and connections use that checked IP set rather than resolving again. Loopback, private, link-local, unspecified, multicast, reserved/documentation ranges, common tunnels and cloud metadata addresses are rejected, including mapped IPv4. Proxies, cookies, redirects and decompression are disabled. TLS certificate and hostname validation remain enabled. A size cap applies while streaming even without Content-Length. No remote Content-Type is trusted.

For internal MinIO use `s3://` with a server-configured endpoint, not an HTTPS URL resolving to private addresses. Protect allowlist and endpoint configuration like other operator settings. A signed URL is a credential: job records remove its query, and transport errors do not echo it.

## Filesystem, object storage and jobs

Empty root/bucket/host allowlists disable the matching URI scheme. Filesystem paths must remain inside
configured roots and name regular files; dot segments, symlinks and directory destinations are rejected.
Linux directory handles and no-follow opens are used for each path component, for reads, writes, replaces,
sidecars and deletes; a symlink anywhere in the path is refused, never followed. Reads open the file as a path
handle first and check it is a regular file before opening it for reading, so a FIFO or device can't block a
request. Existing output files are
not replaced unless the request sends `ifExists:"replace"`. Outputs are created with mode `0644` (narrowed by the
umask), readable by other accounts on the mount. The only file the API deletes is a stale `{output}.json` sidecar
next to an output written without `sidecar` (filesystem and S3). With atomic writes off, readers may see a growing file; failed writes remove their own partial
file while the process is alive. A crash can leave a partial file. Enable atomic writes when consumers scan
folders, and verify the mount's rename semantics. No filesystem operation crosses into another protocol.

Browsing (`STORAGE_LIST_ENABLED`, off by default) adds no new scope: `GET /v1/storage/locations`, `/list` and
`/file` work inside the same roots, buckets and prefixes, with the same no-follow, dot-segment, size and allowlist
checks, before any storage call. With it off, those routes answer `403 storage-list-disabled` and reveal nothing
about the allowlists. Listings skip symlinks and special files; HTTPS is never listed or read by location.

Hidden files and folders (any path or key component starting with `.`, including inside a hidden allowed root) are
never read, listed, written, replaced or used as a conversion source or sidecar, with listing on or off, on every
route: storage reads and listings, convert and detect `input` and `output`, and job file reads. A location outside
the allowed folders gets `storage-location-rejected` first; a hidden one inside them gets `422 hidden-path`. A
listing `prefix` starting with `.` is refused too. The check runs on the decoded path before any storage call and
answers the same for existing and missing names; hidden roots and prefixes are left out of the locations. HTTPS
URLs are not checked: those resources belong to the remote server.

Only subtitle files are read, listed or written by file and S3 location: the extension must be one that libse reads
or writes (case-insensitive; the list is in `GET /v1/limits` as `storage.extensions`). Listings show only those
files (folders are still shown). An output must use an extension of its target format (`.vtt` for WebVTT, for
example); the `{output}.json` sidecar is the one exception. Anything else is `422 extension-not-allowed`. Job file
reads (`GET /v1/jobs/{id}/input` and `/output`) follow the same rule. Decoded control characters (an encoded NUL, `%00`) are rejected as
`422 storage-location-rejected` before any filesystem call. Details: [usage.md](usage.md#hidden-files-and-folders). Any valid API key can browse when it is on; run a separate instance if some keys must not.

S3 bucket/key-prefix checks precede SDK requests. A custom endpoint is trusted operator configuration,
not an untrusted request target; scope the role's permissions as well as the API allowlists. S3 defaults to a conditional PUT (`If-None-Match: *`); explicit `ifExists:"replace"` permits replacement. Sidecar and subtitle writes are independent, with warnings on sidecar failure. Removing a stale sidecar
needs delete permission on the output prefix; without it the subtitle is still written and `sidecar-delete-failed`
is reported.

Jobs are in-memory metadata. Any valid API key knowing an unguessable id may read metadata and re-read stored files; there is no owner check or admin key. An unauthenticated server grants the same id-based access. Restart clears history; TTL/cap eviction may remove running records without
cancelling work. Run one instance or use sticky routing if polling through a load balancer; there is no shared
store. Jobs do not retain subtitle content, but URIs, profiles and diagnostics may be sensitive. Protect access.
Signed HTTPS queries are excluded from returned metadata; do not put credentials in path names.
