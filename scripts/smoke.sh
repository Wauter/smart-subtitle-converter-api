#!/usr/bin/env bash
# Docker smoke test (design §9): boot the image, wait for /readyz, convert corpus files,
# check auth off and on, and enforce the size budget.
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

start
for c in "sample.srt webvtt" "sample.ttml subrip" "sample.ass webvtt" "sample-cp1252.srt assa"; do
  set -- $c
  code=$(convert "$1" "$2"); [ "$code" = 200 ] || fail "$1 -> $2 gave $code: $(cat /tmp/smoke.out)"
  [ -s /tmp/smoke.out ] || fail "$1 -> $2 empty output"
  echo "ok  $1 -> $2 ($(wc -c </tmp/smoke.out) bytes)"
done

start -e API_KEYS=smoke-key
code=$(convert sample.srt webvtt); [ "$code" = 401 ] || fail "auth on, no key gave $code"
code=$(convert sample.srt webvtt -H "Authorization: Bearer smoke-key"); [ "$code" = 200 ] || fail "auth on, good key gave $code"
curl -sf "localhost:$PORT/healthz" >/dev/null || fail "/healthz not open with auth on"
echo "ok  auth on/off"
docker rm -f se-smoke >/dev/null
echo "SMOKE PASS ($PLATFORM)"
