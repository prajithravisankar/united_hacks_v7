#!/usr/bin/env bash
# Runs all three service test suites. The gate every assignment must keep green.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "== ledger (.NET) =="
dotnet test "$ROOT/services/ledger/Boys.Ledger.sln" --nologo -v q

echo ""
echo "== brain (Python) =="
( cd "$ROOT/services/brain" && uv run --quiet pytest -q )

echo ""
echo "== engine (Go) =="
( cd "$ROOT/services/engine" && go test ./... )

echo ""
echo "ALL GREEN"
