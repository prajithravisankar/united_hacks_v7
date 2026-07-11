#!/usr/bin/env bash
# Build the deterministic NAV curve and write it to Oracle fact_nav_curve.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
( cd "$ROOT/services/brain" && uv run --quiet python -m app.quant.build )
