# E5-B23 — Demo Runbook, Reset Tooling & Ops Polish

## What we built (plain English)
Turned a correct system into a **performable** one: a minute-by-minute [demo runbook](demo-runbook.md), a
one-command **pre-flight** gate, an instant **reset** between takes, command-line replay control, a live NAV
viewer, and quiet demo logs. One `./scripts/preflight.sh` proves the whole system is camera-ready; the full
demo was dress-rehearsed end-to-end with zero deviations.

## Key decisions
- **Pre-flight is one command, all-or-nothing.** `scripts/preflight.sh` chains: stack healthy + REST/WS through
  nginx, seed pristine (<30s), the golden money path (E2E), the degradation beat (1 kill/restart), and disk/RAM
  headroom — then leaves the stack reset. Green = cleared for the take.
- **Reset is seconds, not a rebuild.** `scripts/reset_demo.sh` delegates to `seed_demo.sh` (proven idempotent
  by the B22 reset-property test) and asserts <30s. Measured: **~1s**.
- **Replay pacing is runtime config, not code.** `scripts/replay.sh start 4` runs the fund window at **4×** →
  the full curve plays in **~90s** (~30s per leg), the demo's tempo. Speed is a `StartReplay` parameter, so
  pacing is tuned live with no rebuild; the 1s/point base is the only code constant.
- **Log hygiene via env, not code.** The demo profile sets `Logging__LogLevel__Microsoft.AspNetCore=Warning`
  on the ledger so `docker logs boys-ledger` shows only meaningful events (goal created, referee decision,
  degraded) — the on-camera logs beat looks clean. Engine/brain were already quiet.

## What's in the toolbox
| Script | Does |
|---|---|
| `scripts/preflight.sh` | One-command camera-ready gate (5 checks); leaves the stack pristine. |
| `scripts/reset_demo.sh` | Restore the pristine demo scenario in ~1s (no rebuild). |
| `scripts/replay.sh` | `start [speed] \| pause \| speed <m> \| state` — drive the engine replay. |
| `scripts/ws_watch.sh` | Live NAV viewer through nginx — run in two terminals to show the two-screen sync. |
| `docs/demo-runbook.md` | The minute-by-minute script (cold open → AI gate → replay → proof → ride → `docker stop` beat → failure encore → architecture close). |

## How to run / verify it
```bash
./scripts/demo_up.sh        # stack up + seeded
./scripts/preflight.sh      # → "PRE-FLIGHT GREEN — cleared for the take"
# then follow docs/demo-runbook.md
```

## Dress rehearsal (verified)
Ran the runbook's exact commands end-to-end:
- AI gate rejects a vague goal (422 + rewrite); the seeded demo goal is `active` with 3 milestones.
- Replay at 4× streams; proof → AI `Supported` → referee approve → `milestone_cleared` → ride → `riding`.
- **`docker kill boys-brain`** → valuation stays **HTTP 200 `degraded:true`**, replay keeps ticking →
  **`docker start`** → healthy again.
- Failure encore: referee reject → `failed` → settle → **Failure** receipt (`charity 1000`, `takeHome 9000`,
  to the cent) → `RECONCILE OK` → reset to pristine.

**Zero deviations from the runbook.** `scripts/preflight.sh` green start-to-finish; reset ~1s.

## Gotchas / follow-ups
- **The board UI is a later track.** The two-screen sync beat is shown with two `ws_watch.sh` terminals
  today; the board will render it in-app. The runbook notes this.
- **Body-less POSTs need `-d ''`** through nginx (documented in the runbook's curl idiom note).
- **Real-Gemini goal gate is optional** — default is seeded (offline, deterministic). For on-camera
  authenticity, set `AI_MODE=live` + `GEMINI_API_KEY` and restart brain; the seeded provider stays armed as
  the automatic fallback, so the demo never depends on the network.
- Replay speed 4× is a suggestion — `scripts/replay.sh speed <m>` retimes live if the room wants it faster.
