#!/usr/bin/env bash
# Formatters + linters across all three languages.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "== .NET (format check) =="
dotnet format "$ROOT/services/ledger/Boys.Ledger.sln" --verify-no-changes

echo ""
echo "== Python (ruff + black + mypy) =="
( cd "$ROOT/services/brain" \
  && uv run --quiet ruff check . \
  && uv run --quiet black --check . \
  && uv run --quiet mypy . )

echo ""
echo "== Go (gofmt + vet) =="
( cd "$ROOT/services/engine" \
  && test -z "$(gofmt -l .)" \
  && go vet ./... )

echo ""
echo "LINT OK"
