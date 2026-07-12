# E4-B20 — Graceful Degradation & Resilience Beats

## What we built (plain English)
The on-camera resilience story, engineered and rehearsed. Kill brain mid-demo and **nothing stops**: the
engine keeps streaming the replay from its cached curve and broadcasts `status: degraded` to every browser;
bring brain back and it broadcasts `status: healthy` again — automatically, with no reconnect, no dropped
client, no 500 anywhere. The ledger degrades the same outage to a `200 + degraded:true` valuation (B16). All
of it is asserted by a script you run before every take.

## The design: cached curve + a hysteresis health monitor
```
brain (gRPC) ◄── probe every 2s ── health.Monitor ── status change ──▶ hub.BroadcastStatus ──▶ every browser
                    │                                                          ▲
   ListOpenMarkets (cheap, in-memory, no Oracle)          replay ticks keep flowing from the cached curve
```
- **Cached curve is authoritative.** The curve is fetched once at startup and lives in-memory in the replay
  ticker; the ticker never calls brain again. So a brain outage *cannot* interrupt the tick stream — the
  degradation is purely a status signal layered on top.
- **The probe** is brain's cheapest RPC, `ListOpenMarkets` — in-memory on brain's side, no Oracle, no
  business args — so it succeeds whenever brain is up and fails only on a transport error when brain is down.
- **The monitor** is a single-goroutine state machine (same discipline as the hub: it owns its state, no
  locks). It flips to `degraded` only after **2 consecutive** failed probes and back to `healthy` only after
  **2 consecutive** successes. That **hysteresis** is what stops a one-off blip from flapping the on-screen
  badge, and it broadcasts **exactly once per real transition** — never per probe.

## What's proven (unit, under `-race` + goleak)
`internal/health/monitor_test.go` — 8 tests, all green under `-race`, goleak-clean:
- Stays **silent while healthy** (no spurious broadcasts).
- **Degrades only after the threshold** run of failures, and a sustained outage broadcasts **once**.
- A **single blip does not degrade** (debounce).
- **Recovers only after the threshold** run of successes; a **blip mid-recovery resets** the streak.
- A **full outage→recovery→outage cycle** broadcasts `degraded, healthy, degraded` in order.
- The **probe carries the timeout** deadline; `Run` exits cleanly on ctx cancel (goleak).

## What's proven (full stack, `scripts/test_degradation.sh`)
The compose-level drill drives the exact demo beat and asserts it, **3 kill/restart cycles on one WebSocket
connection** (so a pass also proves no client is ever disconnected):

```
→ start replay, connect WS, confirm ticks flow
per cycle:  docker kill boys-brain
            → engine broadcasts status=degraded          (within ~4s: 2 missed probes)
            → ticks keep advancing through the outage     (replay uninterrupted, from cache)
            → ledger /api/goals/{id}/valuation = 200 degraded:true   (never a 500)
            docker start boys-brain  → wait healthy
            → engine broadcasts status=healthy            (within ~4s: 2 good probes)
            → ledger /api/goals/{id}/valuation = 200 degraded:false
PASS
```
Last run: 3/3 cycles green, tick positions advanced 0 → 57 → 116 across the outages, WS connection survived
all three. Engine logs show exactly three `entering degraded mode` / `recovered to healthy` pairs
(`consecutiveFailures:2` / `consecutiveSuccesses:2`) — no flapping.

## Demo choreography (what to type, what appears on screen)
1. **Set up.** `docker compose up -d` (whole stack healthy). Open the board on two screens, both connected to
   `/ws/live?goal=1`. Start the replay (30×) — NAV ticks stream in lockstep on both.
2. **The money shot.** In a terminal on camera: `docker kill boys-brain`.
   - Within ~4 seconds the status badge flips to **DEGRADED** on both screens.
   - **The NAV keeps ticking** — the replay never stutters. (Point this out: "brain is *gone*, and the fund is
     still live, because the curve is cached.")
   - Optionally hit the ledger: `curl -s 127.0.0.1:8080/api/goals/<id>/valuation` → `{"degraded":true,...}`,
     HTTP 200. "No 500s. The whole stack degrades, it doesn't fall over."
3. **The recovery.** `docker start boys-brain`.
   - A few seconds after brain is serving again, the badge flips back to **HEALTHY** on both screens —
     automatically, no refresh.
4. **The line.** "That's not luck — it's a health monitor with hysteresis and a cached curve. `docker stop` on
   a live demo only works if you engineered for it." Then run `./scripts/test_degradation.sh` to show it's
   asserted, not hoped for.

Rehearse it: run `./scripts/test_degradation.sh` before every take — it leaves the stack healthy.

## How to run / verify it
```bash
cd services/engine && go test -race ./internal/health/   # 8 tests, goleak clean
./scripts/test_degradation.sh                            # full-stack drill, 3 cycles (stack must be up)
```

## Adversarial audit (2 lenses: state-machine correctness + monitor↔hub concurrency)
2 confirmed (both low), 1 refuted — both confirmed **fixed with regression tests**:
- **Boot-down reported `healthy`.** If brain was down at startup, clients connecting in the first ~4s (before
  the monitor's hysteresis window) got a snapshot saying `status: healthy`. Fixed: the boot fetch result now
  seeds the hub's *and* the monitor's initial status (`degraded` if brain was down), so the first frame is
  truthful. Test: `TestSeededDegradedRecoversWithoutASpuriousDegradedBroadcast`.
- **`running: true` while paused.** The hub derived `running` from `!terminal`, so a `Pause` (which emits no
  tick) left snapshots and status messages claiming the replay was playing. Fixed: the hub now owns an
  authoritative `running` flag, updated from the tick stream *and* pushed by the gRPC control layer
  (`enginesvc` → `hub.SetRunning`) on start/pause. Tests: `hub.TestPausedReplayReportsNotRunning`,
  `enginesvc.TestStartPauseAndStateRoundTrip`; live-verified against the container (playing→true, paused→false).
- **Refuted:** a claim that ctx-cancel at shutdown could be misclassified as a brain failure and broadcast a
  spurious `degraded` — the shared ctx cancels `monitor.Run` before any broadcast can reach a client.

## Gotchas / follow-ups
- **Probe choice.** `ListOpenMarkets` ignores its `as_of` and returns brain's in-memory markets, so it's a
  true reachability probe (up ⇒ succeeds, container gone ⇒ transport error) without loading Oracle. If brain
  is up but Oracle is down, the probe still succeeds (markets are cached on brain) — that's fine; the ledger
  owns the Oracle-outage story, the engine owns the brain-outage story.
- **Thresholds** (2/2 at a 2s interval) give a ~4s flip each way — snappy on camera without flapping. Tunable
  via the `health...` constants in `cmd/engine/main.go`.
- **Boot-time outage.** If brain is down at *startup*, the curve fetch fails and the timeline is empty (there's
  nothing to replay). The demo assumes brain is up at boot; degradation covers losing brain *after* boot.
- **`scripts/test_degradation.sh`** self-seeds a `degradation-check` commitment via SQL (reused across runs)
  for the ledger sub-check; if the ledger DB isn't up it prints a NOTE and skips that sub-check without
  failing the engine assertions.
- The `cmd/degradecheck` harness is test-only (the Dockerfile builds only `cmd/engine`).
