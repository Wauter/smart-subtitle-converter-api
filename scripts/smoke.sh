#!/usr/bin/env bash
# Docker smoke test (design §9): boot the image, wait for /readyz, convert corpus files,
# check auth (default on, generated key, API_KEYS, disabled), host/origin checks, and enforce the size budget.
#   scripts/smoke.sh <image> [platform] [max-mb]
set -euo pipefail
IMAGE=$1; PLATFORM=${2:-linux/amd64}; MAX_MB=${3:-200}
PORT=${PORT:-18080}; DIR=$(cd "$(dirname "$0")/.." && pwd)
fail() { echo "SMOKE FAIL: $*"; docker logs se-smoke 2>&1 | tail -30 || true; docker rm -f se-smoke >/dev/null 2>&1 || true; exit 1; }

size=$(docker image inspect "$IMAGE" --format '{{.Size}}')
mb=$((size / 1024 / 1024))
echo "image $IMAGE ($PLATFORM): ${mb} MB (budget ${MAX_MB} MB)"
[ "$mb" -le "$MAX_MB" ] || fail "image is ${mb} MB > ${MAX_MB} MB"

start() {
  docker rm -f se-smoke >/dev/null 2>&1 || true
  docker run -d --name se-smoke --platform "$PLATFORM" -p "$PORT:8080" --read-only --tmpfs /tmp --cap-drop ALL "$@" "$IMAGE" >/dev/null
  for _ in $(seq 1 120); do
    curl -sf "localhost:$PORT/readyz" >/dev/null && return 0
    sleep 1
  done
  fail "not ready after 120 s: $(curl -s localhost:$PORT/readyz || true)"
}

convert() { # file target [extra curl args]
  local f=$1 t=$2; shift 2
  curl -s -o /tmp/smoke.out -w '%{http_code}' -F "file=@$DIR/tests/smoke/$f" -F "target=$t" "$@" "localhost:$PORT/v1/convert"
}

# Auth is on by default: no key file and no API_KEYS must stop startup (exit 78) with instructions.
docker rm -f se-smoke >/dev/null 2>&1 || true
docker run --name se-smoke --platform "$PLATFORM" --read-only --tmpfs /tmp --cap-drop ALL "$IMAGE" >/tmp/smoke.log 2>&1 && fail "started without any API key"
code=$(docker inspect se-smoke --format '{{.State.ExitCode}}'); [ "$code" = 78 ] || fail "no key: exit code $code, expected 78"
grep -q "api_keys.txt" /tmp/smoke.log || fail "no key: startup error does not explain the api_keys.txt fix"
echo "ok  no key -> exit 78 with instructions"

start -e API_AUTH_DISABLED=true
for c in "sample.srt webvtt" "sample.ttml subrip" "sample.ass webvtt" "sample-cp1252.srt assa"; do
  set -- $c
  code=$(convert "$1" "$2"); [ "$code" = 200 ] || fail "$1 -> $2 gave $code: $(cat /tmp/smoke.out)"
  [ -s /tmp/smoke.out ] || fail "$1 -> $2 empty output"
  echo "ok  $1 -> $2 ($(wc -c </tmp/smoke.out) bytes)"
done
code=$(convert sample.srt webvtt -H "Host: evil.example"); [ "$code" = 400 ] || fail "foreign Host header gave $code"
code=$(convert sample.srt webvtt -H "Origin: https://evil.example"); [ "$code" = 403 ] || fail "foreign Origin gave $code"
echo "ok  host and origin checks"

# Empty, writable key file: a key is generated into it in place and never logged.
KEYS=$(mktemp); chmod 666 "$KEYS"
start -v "$KEYS:/api_keys.txt"
KEY=$(grep '^ssc_' "$KEYS" | head -1); [ -n "$KEY" ] || fail "no key generated into the mounted file"
# Logs go to a file first: `docker logs | grep -q` can fail under pipefail when grep exits early (SIGPIPE).
LOGS=$(mktemp); docker logs se-smoke >"$LOGS" 2>&1
grep -qF "$KEY" "$LOGS" && fail "generated key appears in the logs"
grep -q "auth.key-generated" "$LOGS" || fail "key generation is not logged"
rm -f "$LOGS"
code=$(convert sample.srt webvtt); [ "$code" = 401 ] || fail "generated key, no key sent gave $code"
code=$(convert sample.srt webvtt -H "Authorization: Bearer $KEY"); [ "$code" = 200 ] || fail "generated key gave $code"
echo "ok  key generated into the mounted file"
rm -f "$KEYS"

start -e API_KEYS=smoke-key
code=$(convert sample.srt webvtt); [ "$code" = 401 ] || fail "auth on, no key gave $code"
code=$(convert sample.srt webvtt -H "Authorization: Bearer smoke-key"); [ "$code" = 200 ] || fail "auth on, good key gave $code"
curl -sf "localhost:$PORT/healthz" >/dev/null || fail "/healthz not open with auth on"
echo "ok  auth on/off"
docker rm -f se-smoke >/dev/null
echo "SMOKE PASS ($PLATFORM)"
