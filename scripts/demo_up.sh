#!/usr/bin/env bash
# One command, from nothing to a demo-ready system: build + start the full stack (databases → data-seed →
# brain/ledger → engine → nginx, all health-gated), author the demo scenario, and verify the edge. This is
# the "one command and it's alive" closer. Idempotent — re-run any time; add a fresh start with `down -v` first.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if [ ! -f .env ]; then echo "FAIL: .env missing — cp .env.example .env and fill it in"; exit 2; fi

echo "== 1/3  bringing up the full demo stack (docker compose --profile demo) =="
start=$(date +%s)
docker compose --profile demo up -d --build --wait
echo "   stack healthy in $(( $(date +%s) - start ))s"

echo ""
echo "== 2/3  seeding the demo scenario =="
"$ROOT/scripts/seed_demo.sh"

echo ""
echo "== 3/3  verifying the stack =="
"$ROOT/scripts/check_stack.sh"
