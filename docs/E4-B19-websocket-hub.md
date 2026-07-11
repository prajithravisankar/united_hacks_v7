# E4-B19 — WebSocket Hub (Fan-Out, Snapshot, Backpressure)

## What we built (plain English)
Go's headline: **correct concurrent fan-out**. `/ws/live?goal={id}` streams the replay to every connected browser — identically ordered, perfectly in sync. Late joiners get a **snapshot** first (caught up to the current NAV/position), then only subsequent ticks. A slow browser is **dropped** rather than allowed to stall anyone else. All of it is race-free and leak-free, proven by the test suite the two-browser demo beat depends on.

## The design: one broadcaster, per-client buffers, drop-don't-block
```
ticker.Ticks() ─▶ [ single broadcast goroutine: owns the client set + lastTick, no locks ] ─▶ per-client buffered chan ─▶ writeLoop ─▶ ws
                      register / unregister / status also arrive here (all serialized)
```
One goroutine owns everything mutable (the client map, `lastTick`), so there is **no per-message locking and no data race** — register, unregister, tick, and status are all handled in one `select`. Each client has a **buffered send channel**; broadcasting does a **non-blocking** send and **drops** any client whose buffer is full. The hub can never be stalled by a slow consumer.

## Per-client teardown (the coder/websocket-clean pattern)
Each connection runs three goroutines — a **read pump** (discards inbound frames; clients are read-only), a **closer** (`<-ctx.Done()` → one `CloseNow()`), and the **writeLoop**. Teardown is a **single forceful `CloseNow`** (never a graceful close, which deadlocks the library's `waitGoroutines`). Cancelling the client's context — on drop, disconnect, or engine shutdown — tears down all three cleanly. `goleak` verifies **zero leaks on every path**: slow-client drop, mid-stream disconnect, unknown-goal reject, and full shutdown.

## What's proven (all under `-race` + goleak)
- **Two clients receive byte-identical, identically-ordered sequences** (transcripts compared).
- **Late joiner:** connects mid-replay → gets a `snapshot` at the current position, then only positions after it — none missed, none duplicated (asserted against the ticker's authoritative sequence, on the fake clock for exact control).
- **Backpressure:** a deliberately-stalled client is dropped once its buffer fills; the fast client keeps receiving with **zero delay** (timing-asserted); hub goroutines don't leak on the drop.
- **100 concurrent clients** receive a full replay correctly under `-race`.
- **Disconnect mid-stream** → reaped (goleak); **inbound frames** → ignored safely; **unknown goal** → close code **4404** (documented).

## Verified live (the two-browser beat)
Two browsers connected to the container's `/ws/live?goal=1`, both got a snapshot, then `StartReplay(30×)` streamed them **the same ticks in lockstep**: pos 0/nav $100.00, pos 1/nav $92.02, pos 2/nav $90.82 — identical on both. That's the demo's "two screens, perfectly synced" moment, working end-to-end.

## How to run / verify it
```bash
cd services/engine && go test -race ./internal/hub/   # all green, goleak clean, incl. the 100-client test
```
The WS message contract is documented for the frontend in `docs/ws-contract.md`.

## Gotchas / follow-ups
- Per-client send buffer is 64 (production); the backpressure test uses 8 + ~64KB payloads to make the drop deterministic regardless of OS TCP buffer sizes.
- `snapshotMessage` reads the hub's own `lastTick` (owned by the broadcast goroutine) — it never calls back into the ticker, avoiding a deadlock against the ticker's unbuffered emit.
- Next: B20 broadcasts `status: degraded`/`healthy` through this same hub (the `statusCh` is already wired) when brain drops and recovers.
