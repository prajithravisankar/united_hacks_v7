#!/usr/bin/env bash
# Manual live-Gemini smoke test (NOT part of the test suite). Needs GEMINI_API_KEY.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ -z "${GEMINI_API_KEY:-}" ]; then
  echo "Set GEMINI_API_KEY to run the live smoke test." >&2
  exit 2
fi
cd "$ROOT/services/brain"
uv run --quiet python - <<'PY'
import os
from app.referee.providers import GeminiProvider

p = GeminiProvider(os.environ["GEMINI_API_KEY"])
print("GOAL  :", p.validate_goal("Score 90% in my History class this semester"))
print("PROOF :", p.check_proof("Scored 87 on Midterm 1", b"screenshot showing 87/100", "image/png"))
PY
