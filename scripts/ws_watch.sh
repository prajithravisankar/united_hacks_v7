#!/usr/bin/env bash
# Watch the live NAV stream through the nginx edge (what the board will render). Run it in two terminals to
# show the two-screens-in-lockstep beat before the board UI exists. Ctrl-C to stop.
#
# Usage: scripts/ws_watch.sh [goal]   (default goal 1)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GOAL="${1:-1}"
EDGE="${BOYS_EDGE_WS:-ws://127.0.0.1:8888}"

cd "$ROOT/tests/e2e"
uv run --quiet python -u - "$EDGE/ws/live?goal=$GOAL" <<'PY'
import json, sys
from websockets.sync.client import connect

url = sys.argv[1]
print(f"watching {url}  (Ctrl-C to stop)")
with connect(url, open_timeout=10) as ws:
    while True:
        m = json.loads(ws.recv())
        nav = f"${m['navCents']/100:,.2f}"
        if m["type"] == "snapshot":
            print(f"  snapshot  pos={m['position']:>3}  {nav:>10}  status={m.get('status')}")
        elif m["type"] == "tick":
            tag = "  (final)" if m.get("terminal") else ""
            print(f"  tick      pos={m['position']:>3}  {nav:>10}  {m['date']}{tag}")
        elif m["type"] == "status":
            print(f"  ** STATUS -> {m['status'].upper()} **")
PY
