#!/usr/bin/env bash
# Run the full BOYS end-to-end suite against the composed stack (through the nginx edge). Assumes the demo
# stack is up — bring it up first with scripts/demo_up.sh. Records total runtime. Extra args pass to pytest.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "== BOYS E2E suite (edge: ${BOYS_EDGE:-http://127.0.0.1:8888}) =="
start=$(date +%s)
( cd "$ROOT/tests/e2e" && uv run --quiet pytest "$@" )
echo "== E2E green in $(( $(date +%s) - start ))s =="
