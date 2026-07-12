#!/usr/bin/env bash
# Author the canonical BOYS demo scenario into a running stack, idempotently. Resets the ledger's
# transactional tables so the demo goal is always commitment_id=1 (what the engine replays), creates the
# History-class goal through the real AI gate (seeded mode → deterministic ACCEPT), activates it (escrow
# posted), and stages the milestone proof fixtures. Re-runnable between demo takes: it restores a
# byte-identical starting state.
#
# Prereqs: the demo stack is up (docker compose --profile demo up -d) and .env has MSSQL_SA_PASSWORD.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LEDGER="${LEDGER_HTTP:-127.0.0.1:8080}"
SEED_DIR="$ROOT/data/seed"

if [ ! -f "$ROOT/.env" ]; then echo "FAIL: .env missing"; exit 2; fi
PW=$(grep -E '^MSSQL_SA_PASSWORD=' "$ROOT/.env" | cut -d= -f2-)

echo "== BOYS demo seed =="

# --- portable UTC date arithmetic (BSD/macOS and GNU/Linux) ---
iso_plus_days() { # $1 = days ahead
    date -u -v+"$1"d +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -d "+$1 days" +%Y-%m-%dT%H:%M:%SZ
}
DEADLINE=$(iso_plus_days 120)
DUE1=$(iso_plus_days 40)
DUE2=$(iso_plus_days 80)
DUE3=$(iso_plus_days 115)

# --- 1. Reset the ledger's transactional tables → the demo goal becomes commitment_id 1 deterministically.
# Disable the append-only triggers for the reset, delete child→parent, reseed identities, restore the pool.
sql() {
    docker exec -i boys-mssql /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P "$PW" -C -d boys -h -1 -W -b -Q "SET NOCOUNT ON; $1"
}
echo "-- resetting ledger transactional tables"
sql "
DISABLE TRIGGER trg_postings_append_only ON ledger_postings;
DISABLE TRIGGER trg_txns_append_only ON ledger_transactions;
DISABLE TRIGGER trg_settlements_append_only ON settlements;
DISABLE TRIGGER trg_commitment_events_append_only ON commitment_events;

DELETE FROM ledger_postings;
DELETE FROM ledger_transactions;
DELETE FROM settlements;
DELETE FROM nav_snapshots;
DELETE FROM verifications;
DELETE FROM commitment_events;
DELETE FROM milestones;
DELETE FROM commitments;

-- Reseed identities so the next insert is 1 — but ONLY for tables that have actually held a row
-- (last_value IS NOT NULL). On a never-used table, RESEED sets the first id to the reseed value DIRECTLY
-- (a fresh table would become id 0), so we skip those: fresh tables already start at their seed of 1.
DECLARE @reseed NVARCHAR(MAX) = N'';
SELECT @reseed += 'DBCC CHECKIDENT(''' + t.name + ''', RESEED, 0) WITH NO_INFOMSGS;' + CHAR(10)
FROM (VALUES ('ledger_postings'),('settlements'),('nav_snapshots'),('verifications'),
             ('commitment_events'),('milestones'),('commitments')) AS t(name)
JOIN sys.identity_columns ic ON OBJECT_NAME(ic.object_id) = t.name AND ic.last_value IS NOT NULL;
IF LEN(@reseed) > 0 EXEC sp_executesql @reseed;

UPDATE community_pool_stats SET committed_people = 1204, pool_cents = 4730000 WHERE id = 1;

ENABLE TRIGGER trg_postings_append_only ON ledger_postings;
ENABLE TRIGGER trg_txns_append_only ON ledger_transactions;
ENABLE TRIGGER trg_settlements_append_only ON settlements;
ENABLE TRIGGER trg_commitment_events_append_only ON commitment_events;
" >/dev/null

# --- 2. Create the History-class goal through the AI gate. goal_text contains "90%" so seeded mode ACCEPTs.
echo "-- creating the demo goal"
BODY=$(cat <<JSON
{
  "goalText": "Finish my History class with a 90% overall grade",
  "stakeCents": 10000,
  "charityId": 1,
  "driveMode": "AUTO",
  "deadline": "$DEADLINE",
  "milestones": [
    { "description": "Midterm 1", "targetMetric": "score >= 85", "dueDate": "$DUE1" },
    { "description": "Midterm 2", "targetMetric": "score >= 85", "dueDate": "$DUE2" },
    { "description": "Final exam", "targetMetric": "score >= 90", "dueDate": "$DUE3" }
  ]
}
JSON
)
RESP=$(curl -fsS -X POST "http://$LEDGER/api/goals" -H 'Content-Type: application/json' -d "$BODY")
CID=$(printf '%s' "$RESP" | grep -oE '"commitmentId":[0-9]+' | grep -oE '[0-9]+' | head -1)
if [ "${CID:-}" != "1" ]; then
    echo "FAIL: expected commitmentId 1, got '$CID' (response: $RESP)"; exit 1
fi
echo "   goal created: commitment 1  ($(printf '%s' "$RESP" | grep -oE '"aiVerdict":"[^"]*"'))"

# --- 3. Activate → escrow the $100 stake, go live.
echo "-- activating (escrow \$100)"
ASTATE=$(curl -fsS -X POST "http://$LEDGER/api/goals/1/activate" | grep -oE '"state":"[^"]*"')
echo "   $ASTATE"

# --- 4. Stage the milestone proof fixtures (seeded AI checks the bytes: 'cropped' ⇒ reject, else approve).
echo "-- staging proof fixtures in data/seed/"
mkdir -p "$SEED_DIR"
# A minimal 1×1 PNG; the three "good" fixtures pass the seeded proof check (no 'cropped' in the bytes).
PNG_B64="iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="
printf '%s' "$PNG_B64" | base64 --decode > "$SEED_DIR/midterm1_grade.png"
cp "$SEED_DIR/midterm1_grade.png" "$SEED_DIR/midterm2_grade.png"
cp "$SEED_DIR/midterm1_grade.png" "$SEED_DIR/final_grade.png"
# The failure-path fixture: same image with the ASCII marker 'cropped' appended ⇒ seeded AI says insufficient.
cp "$SEED_DIR/midterm1_grade.png" "$SEED_DIR/midterm2_cropped.png"
printf 'cropped' >> "$SEED_DIR/midterm2_cropped.png"

cat > "$SEED_DIR/README.md" <<'MD'
# Demo proof fixtures (seeded AI mode)
Staged by `scripts/seed_demo.sh`. In seeded AI mode the referee checks only the evidence **bytes**:
- `*_grade.png` — pass (no `cropped` marker) ⇒ AI "Supported".
- `midterm2_cropped.png` — the failure-path fixture (`cropped` in the bytes) ⇒ AI "Insufficient".
Swap in real screenshots for the video; the byte rule is all the seeded gate needs.
MD

echo ""
echo "== demo seeded: commitment 1 active, \$100 escrowed, 3 milestones pending, fixtures staged =="
