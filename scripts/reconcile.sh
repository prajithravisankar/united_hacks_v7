#!/usr/bin/env bash
# Recompute every ledger balance from the raw postings and check the money invariants — the audit that
# nothing leaked. Runs against the live SQL Server. Reused between demo takes (E5) as a sanity gate.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PW=$(grep -E '^MSSQL_SA_PASSWORD=' "$ROOT/.env" | cut -d= -f2-)

sql() {
    docker exec -i boys-mssql /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P "$PW" -C -d boys -h -1 -W -Q "SET NOCOUNT ON; $1" 2>/dev/null | tr -d ' \r'
}

fail=0
echo "== BOYS ledger reconciliation =="

# 1. Every transaction's postings sum to zero (no unbalanced group ever hit the DB).
unbalanced=$(sql "SELECT COUNT(*) FROM (SELECT txn_id FROM ledger_postings GROUP BY txn_id HAVING SUM(delta_cents)<>0) x")
echo "unbalanced transactions          : $unbalanced"
[ "$unbalanced" = "0" ] || { echo "  FAIL: a transaction does not sum to zero"; fail=1; }

# 2. Global conservation: the sum across all accounts is exactly zero (no money created or destroyed).
total=$(sql "SELECT ISNULL(SUM(delta_cents),0) FROM ledger_postings")
echo "global sum of all postings       : $total"
[ "$total" = "0" ] || { echo "  FAIL: money was created or destroyed"; fail=1; }

# 3. The floor accounts never go negative.
escrow=$(sql "SELECT ISNULL(SUM(delta_cents),0) FROM ledger_postings WHERE account='USER_ESCROW'")
pool=$(sql "SELECT ISNULL(SUM(delta_cents),0) FROM ledger_postings WHERE account='WINNERS_BONUS_POOL'")
echo "USER_ESCROW balance              : $escrow"
echo "WINNERS_BONUS_POOL balance       : $pool"
[ "$escrow" -ge 0 ] 2>/dev/null || { echo "  FAIL: USER_ESCROW negative"; fail=1; }
[ "$pool"   -ge 0 ] 2>/dev/null || { echo "  FAIL: WINNERS_BONUS_POOL negative"; fail=1; }

# 4. Per-account balances, recomputed from raw postings.
echo "per-account balances (from raw postings):"
sql "SELECT '  ' + account + ' = ' + CAST(SUM(delta_cents) AS VARCHAR(20)) FROM ledger_postings GROUP BY account ORDER BY account"

# 5. Every settlement receipt matches what the ledger actually moved for that commitment.
mismatch=$(sql "
SELECT COUNT(*) FROM settlements s
CROSS APPLY (
  SELECT
    ISNULL(SUM(CASE WHEN p.account='USER_YIELD'      THEN p.delta_cents END),0) AS yield,
    ISNULL(SUM(CASE WHEN p.account='HOUSE_CARRY'     THEN p.delta_cents END),0) AS carry,
    ISNULL(SUM(CASE WHEN p.account='CHARITY_PAYABLE' THEN p.delta_cents END),0) AS charity
  FROM ledger_postings p JOIN ledger_transactions t ON p.txn_id=t.txn_id
  WHERE t.commitment_id = s.commitment_id
) b
WHERE b.yield <> s.take_home_cents OR b.carry <> s.carry_cents OR b.charity <> s.charity_cents")
echo "receipt-vs-ledger mismatches     : $mismatch"
[ "$mismatch" = "0" ] || { echo "  FAIL: a receipt disagrees with the ledger it settled"; fail=1; }

echo ""
if [ "$fail" = "0" ]; then echo "RECONCILE OK"; else echo "RECONCILE FAILED"; exit 1; fi
