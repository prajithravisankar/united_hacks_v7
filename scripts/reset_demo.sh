#!/usr/bin/env bash
# Restore the pristine demo scenario in seconds, between takes — NO compose rebuild. This is the fast reset
# the runbook leans on; it delegates to seed_demo.sh (which the B22 reset-property test proves idempotent) and
# asserts it lands in well under 30s.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

start=$(date +%s)
"$ROOT/scripts/seed_demo.sh" >/dev/null
elapsed=$(( $(date +%s) - start ))

echo "demo reset to pristine in ${elapsed}s (commitment 1 active, \$100 escrowed, 3 milestones pending)"
if [ "$elapsed" -ge 30 ]; then
    echo "WARN: reset took ${elapsed}s (target < 30s)"; exit 1
fi
