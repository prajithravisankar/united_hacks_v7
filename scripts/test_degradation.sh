#!/usr/bin/env bash
# The on-camera resilience beat, rehearsed and asserted against the live compose stack. It drives the exact
# demo move — kill brain, watch the engine keep streaming from its cached curve and broadcast "degraded",
# then restart brain and watch it recover — and fails loudly if any part of that story breaks. Runnable
# before every demo take. Repeats the kill/restart cycle 3× on one WebSocket connection, so a pass also
# proves no client is ever disconnected during an outage.
#
# Usage: scripts/test_degradation.sh          (full stack must already be up: docker compose up -d)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

ENGINE_HTTP="127.0.0.1:8090"
BRAIN_HTTP="127.0.0.1:8081"
LEDGER_HTTP="127.0.0.1:8080"
BRAIN_CONTAINER="boys-brain"
CYCLES="${CYCLES:-3}"

echo "== BOYS degradation drill =="

# ---- 1. Preflight: the whole stack must be up and healthy before we start breaking things. ----
require_ready() {
    local name="$1" url="$2"
    if ! curl -fsS -o /dev/null --max-time 5 "$url"; then
        echo "  FAIL: $name is not ready at $url — bring the stack up first: docker compose up -d"
        exit 1
    fi
    echo "  $name ready"
}
require_ready "engine" "http://$ENGINE_HTTP/health/ready"
require_ready "brain"  "http://$BRAIN_HTTP/health/ready"

# ---- 2. Seed a ledger commitment so we can re-verify the ledger's degraded valuation under the same outage. ----
# Reuse an existing degradation-check commitment if one is already there; otherwise insert one. Direct SQL
# (like reconcile.sh) keeps this self-contained — the valuation path only needs the row to exist.
LEDGER_GOAL=0
if [ -f "$ROOT/.env" ] && docker ps --format '{{.Names}}' | grep -q '^boys-mssql$'; then
    PW=$(grep -E '^MSSQL_SA_PASSWORD=' "$ROOT/.env" | cut -d= -f2-)
    sql() {
        docker exec -i boys-mssql /opt/mssql-tools18/bin/sqlcmd \
            -S localhost -U sa -P "$PW" -C -d boys -h -1 -W -Q "SET NOCOUNT ON; $1" 2>/dev/null | tr -d ' \r'
    }
    LEDGER_GOAL=$(sql "
        DECLARE @id INT = (SELECT TOP 1 commitment_id FROM commitments WHERE goal_text='degradation-check' ORDER BY commitment_id);
        IF @id IS NULL BEGIN
            INSERT INTO commitments (user_id, goal_text, stake_cents, charity_id, drive_mode, state, deadline)
            VALUES (1, 'degradation-check', 10000, 1, 'AUTO', 'riding', DATEADD(MONTH, 1, SYSUTCDATETIME()));
            SET @id = SCOPE_IDENTITY();
        END
        SELECT @id;")
    if [[ "$LEDGER_GOAL" =~ ^[0-9]+$ ]]; then
        echo "  ledger valuation re-verify will use commitment $LEDGER_GOAL"
    else
        echo "  NOTE: could not seed a ledger commitment; skipping the ledger valuation sub-check"
        LEDGER_GOAL=0
    fi
else
    echo "  NOTE: ledger DB (boys-mssql) not found; skipping the ledger valuation sub-check"
fi

# ---- 3. Drive the beat and assert it. All the WebSocket/gRPC/docker choreography lives in the Go harness. ----
echo
echo "== driving $CYCLES kill/restart cycle(s) =="
cd "$ROOT/services/engine"
go run ./cmd/degradecheck \
    -engine-http "$ENGINE_HTTP" \
    -brain-http "$BRAIN_HTTP" \
    -ledger-http "$LEDGER_HTTP" \
    -brain-container "$BRAIN_CONTAINER" \
    -ledger-goal "$LEDGER_GOAL" \
    -cycles "$CYCLES"

echo
echo "== degradation drill passed =="
