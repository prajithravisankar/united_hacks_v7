#!/usr/bin/env bash
# Drive the demo replay from the command line (the board does this in-app later). Talks to the engine's gRPC
# control API. Demo pacing: speed 4 → the full curve plays in ~90s (~30s per third), the runbook's timing.
#
# Usage: scripts/replay.sh start [speed]   (default speed 4)
#        scripts/replay.sh pause
#        scripts/replay.sh speed <mult>
#        scripts/replay.sh state
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CMD="${1:-start}"
SPEED="${2:-4}"
GOAL="${GOAL:-1}"

cd "$ROOT/services/brain"
uv run --quiet python - "$CMD" "$SPEED" "$GOAL" <<'PY'
import sys, grpc
sys.path.insert(0, "gen")
from boys.engine.v1 import engine_pb2, engine_pb2_grpc

cmd, speed, goal = sys.argv[1], float(sys.argv[2]), sys.argv[3]
stub = engine_pb2_grpc.EngineServiceStub(grpc.insecure_channel("127.0.0.1:50071"))
if cmd == "start":
    st = stub.StartReplay(engine_pb2.StartReplayRequest(commitment_id=goal, speed=speed))
elif cmd == "pause":
    st = stub.Pause(engine_pb2.PauseRequest(commitment_id=goal))
elif cmd == "speed":
    st = stub.SetSpeed(engine_pb2.SetSpeedRequest(commitment_id=goal, speed=speed))
elif cmd == "state":
    st = stub.GetReplayState(engine_pb2.GetReplayStateRequest(commitment_id=goal))
else:
    print("usage: replay.sh start|pause|speed|state [speed]"); sys.exit(2)
print(f"replay: running={st.running} speed={st.speed} position={st.position} date={st.current_sim_date}")
PY
