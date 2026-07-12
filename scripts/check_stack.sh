#!/usr/bin/env bash
# Full-stack smoke: every container healthy, the nginx edge routes REST + WebSocket, and the demo commitment
# is present with the right state. Fails before B21 is wired, passes after. Safe to run before every demo take.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NGINX="${NGINX_HTTP:-127.0.0.1:8888}"

fail=0
ok()   { echo "  ok   $1"; }
bad()  { echo "  FAIL $1"; fail=1; }

echo "== BOYS stack check (edge: $NGINX) =="

# --- 1. Containers healthy (long-running) / completed (one-shots) ---
for c in boys-mssql boys-oracle boys-brain boys-ledger boys-engine boys-nginx; do
    st=$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$c" 2>/dev/null || echo "missing")
    [ "$st" = "healthy" ] && ok "$c healthy" || bad "$c = $st (want healthy)"
done
for c in boys-ledger-migrate boys-data-seed; do
    read -r status code < <(docker inspect -f '{{.State.Status}} {{.State.ExitCode}}' "$c" 2>/dev/null || echo "missing -")
    { [ "$status" = "exited" ] && [ "$code" = "0" ]; } && ok "$c completed (exit 0)" || bad "$c = $status/$code (want exited 0)"
done

# --- 2. REST via nginx: edge health ---
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://$NGINX/api/health" || echo 000)
[ "$code" = "200" ] && ok "/api/health via nginx = 200" || bad "/api/health via nginx = $code (want 200)"

# --- 3. WebSocket handshake via nginx: expect 101 Switching Protocols from the engine through the edge ---
line=$(curl -s -i --max-time 5 \
    -H "Connection: Upgrade" -H "Upgrade: websocket" \
    -H "Sec-WebSocket-Version: 13" -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" \
    "http://$NGINX/ws/live?goal=1" 2>/dev/null | head -1)
printf '%s' "$line" | grep -q "101" && ok "ws upgrade via nginx = 101" || bad "ws upgrade via nginx = '${line:-no response}' (want 101)"

# --- 4. Demo commitment present via nginx with a live state ---
goal=$(curl -s --max-time 5 "http://$NGINX/api/goals/1" || echo "")
state=$(printf '%s' "$goal" | grep -oE '"state":"[^"]*"' | head -1 | cut -d'"' -f4)
case "$state" in
    active|riding|pending_verification|milestone_cleared|cashed_out|succeeded|failed|settled)
        ok "demo commitment 1 present via nginx (state=$state)" ;;
    *) bad "demo commitment 1 not found via nginx (got: ${goal:-nothing})" ;;
esac

echo ""
if [ "$fail" = 0 ]; then echo "== STACK OK =="; else echo "== STACK CHECK FAILED =="; fi
exit "$fail"
