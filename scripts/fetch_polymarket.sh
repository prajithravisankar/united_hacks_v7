#!/usr/bin/env bash
# Best-effort wrapper around fetch_polymarket.py (stdlib only). Non-zero exit is fine.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
python3 "$ROOT/scripts/fetch_polymarket.py" "$ROOT/data/raw/polymarket_ticks.json"
