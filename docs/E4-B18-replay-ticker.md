# E4-B18 — Deterministic 30× Replay Ticker

## What we built (plain English)
The clock of the demo. A ticker that walks the precomputed timeline (NAV points + events) at a controllable multiplier (1×/8×/30×) and emits an **ordered, identical-every-run** tick stream. Start/pause/seek/set-speed drive it over EngineService. Because time is an **injected clock**, the whole suite runs in **half a second** with exact, race-free assertions — and because the driver is a **single-goroutine actor**, there are no locks and no data races by construction.

## The design: one actor, commands in, ticks out
```
control RPCs ──cmd──▶ [ single driver goroutine owns ALL state ] ──tick──▶ consumer (hub / test)
                          position, speed, running, finished
```
Every control call (`Start/Pause/SetSpeed/Seek/State`) sends a **command** to the driver and blocks for an **ack**; the driver is the only goroutine that touches the mutable state. So there is nothing to lock and nothing to race — `go test -race` is clean by construction, even with 40 goroutines hammering the controls at once (`TestConcurrentControlCallsAreRaceClean`). Emissions go out on an unbuffered channel the single consumer (the B19 hub) reads.

## Injected clock → deterministic, millisecond-fast
`internal/clock` defines a `Clock`/`Timer` interface: `RealClock` in production, a controllable `FakeClock` in tests. The fake only advances when the test says so, and `BlockUntil` lets a test wait for the driver to arm its next timer before advancing — so an emission happens **exactly** at its deadline, never a nanosecond early. The **ack-after-stop** discipline (a control call returns only after the driver has torn down any live timer) is what makes `BlockUntil` observe only the *next* arm, never a stale one — the fix for a real seek/rearm race the tests caught.

## What's proven (every acceptance is a named fast test)
- **Determinism:** same timeline + speed run twice → byte-identical tick sequences.
- **Speed math is exact:** at 30× (step 1×=30ms → 1ms/step) a tick fires at exactly its deadline and not one nanosecond sooner.
- **Pause/resume exactness:** pause → zero emissions; resume → continues from the **exact** position (no skip, no repeat); pause-when-paused / start-when-running are state-level no-ops.
- **Seek:** jump to a position → the next emission is that position; seek past the end **completes cleanly** (Done).
- **Speed change mid-replay:** takes effect from the next tick, no reordering.
- **Completion:** exactly one terminal tick; restart after completion replays from 0 (the demo-retake path).
- **Cancel:** the driver goroutine exits on context cancel and closes its output — goleak clean.

## Verified live
Against the running container's EngineService (gRPC `:50071`): `StartReplay(commitment 1, 30×)` → after 300 ms the replay had walked to **position 8, sim-date 2021-09-11** (≈33 ms/point at 30×); `Pause` → position 8 preserved. The replay walks the 360-point historical timeline in real time.

## How to run / verify it
```bash
cd services/engine && go test -race ./internal/replay/ ./internal/clock/   # < 1s, goleak clean
```

## Gotchas / follow-ups
- `stepAt1x` (wall-clock per point at 1×) is injectable: production uses 1s (≈33 ms/point at 30×, so the 360-point curve replays in ~12 s); tests use 30 ms for clean 1×/8×/30× divisions.
- The ticker emits on an **unbuffered** channel — a single consumer must drain it (main drains it for now; **B19's hub becomes that consumer** and fans out with per-client backpressure).
- Next: B19 puts the WebSocket hub in front of the tick stream (snapshot-on-connect, backpressure-drop, fan-out to many browsers).
