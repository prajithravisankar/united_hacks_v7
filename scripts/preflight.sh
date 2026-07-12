#!/usr/bin/env bash
# Pre-flight before a demo take: ONE command that proves the whole system is camera-ready — stack healthy and
# routing through nginx, the demo seed pristine, the golden money path green, the resilience beat green, and
# enough disk/RAM headroom. Leaves the stack reset to pristine. Red until everything is in place.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG=/tmp/boys-preflight.log
fail=0

check() { # <label> <cmd...>
    local label="$1"; shift
    printf '  %-38s ' "$label"
    if "$@" >"$LOG" 2>&1; then echo "ok"; else echo "FAIL"; sed 's/^/      /' "$LOG" | tail -15; fail=1; fi
}

echo "== BOYS pre-flight =="

echo "-- 1. stack + edge --"
check "containers healthy, REST+WS via nginx" "$ROOT/scripts/check_stack.sh"

echo "-- 2. demo seed --"
check "reset to pristine (<30s)" "$ROOT/scripts/reset_demo.sh"

echo "-- 3. money path --"
check "golden success + succeed guard (E2E)" bash -c "cd '$ROOT/tests/e2e' && uv run --quiet pytest -q -k 'golden or cannot_succeed'"

echo "-- 4. resilience --"
check "degradation beat (1 kill/restart)" bash -c "CYCLES=1 '$ROOT/scripts/test_degradation.sh'"

echo "-- 5. resources --"
avail_kb=$(df -k / | awk 'NR==2{print $4}')
avail_gb=$(( avail_kb / 1024 / 1024 ))
printf '  %-38s ' "disk headroom (${avail_gb}GB free)"
if [ "$avail_gb" -ge 5 ]; then echo "ok"; else echo "FAIL (want >= 5GB)"; fail=1; fi
running=$(docker compose --profile demo ps --status running --format '{{.Name}}' 2>/dev/null | grep -c .)
printf '  %-38s ' "containers running (${running})"
if [ "$running" -ge 6 ]; then echo "ok"; else echo "FAIL (want >= 6)"; fail=1; fi

# The E2E + degradation mutated commitment 1 — leave it pristine for the take.
"$ROOT/scripts/reset_demo.sh" >/dev/null 2>&1 || true

echo
if [ "$fail" = 0 ]; then echo "== PRE-FLIGHT GREEN — cleared for the take =="; else echo "== PRE-FLIGHT FAILED =="; fi
exit "$fail"
