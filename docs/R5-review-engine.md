# R5 — Real-Time Layer Audit (Engine Review)

Reviews the Go engine after B17–B20: the deterministic replay **ticker**, the WebSocket **hub**, and the
brain-outage **degradation monitor**. Scope: prove the concurrency design is safe (race-free, leak-free,
deadlock-free, backpressured), that it stays flat under a long soak, and that the WebSocket contract matches
the rest of the system.

## Verdict
- **Full suite green under `-race`**, `goleak` clean in every package. Hand-written internal coverage **94.6%**.
- **30-minute soak** (20 clients, 30× loop, 9 brain kills, 864k ticks): goroutines flat (79↔80) and heap
  flat, no client dropped — see [Soak results](#soak-results).
- **Adversarial audit** (4 lenses, find→refute): see [Findings](#findings--resolutions).
- **Cross-contract**: WS ↔ `docs/ws-contract.md` ↔ ledger REST — see [Cross-contract](#cross-contract-check).

---

## How the pieces fit together
```
brain (gRPC) ──curve at boot──▶ ticker (owns timeline) ──Ticks()──▶ hub ──fan-out──▶ N browsers (WebSocket)
     ▲                              ▲                                  ▲
     └── probe every 2s ── monitor ─┘ (drives degraded/healthy) ───────┘ (status broadcast)
                                    gRPC EngineService ──Start/Pause/SetSpeed──▶ ticker + hub.SetRunning
```
- The **ticker** fetches the NAV curve from brain once at boot, caches it in-memory, and replays it
  deterministically on an injected clock. After boot it never calls brain again — so an outage can't stall it.
- The **hub** is the single consumer of the tick stream and fans each tick out to every browser, with
  snapshot-on-connect and drop-on-backpressure.
- The **monitor** probes brain and flips a degraded/healthy status (hysteresis), broadcast through the hub.
- **EngineService** (gRPC) drives replay controls and keeps the hub's play/pause state in sync.

## Goroutine & channel diagram (matches the code)
Steady state with **N** connected clients:
```
                         ┌──────────────────────────────────────────────────────────────┐
  gRPC handler ──cmds───▶│ ticker.Run  (owns position/speed/running/finished — no locks) │
  (enginesvc)  ◀─ack─────│                                                                │
               ◀─reply(1)│   emits ──▶ out (unbuffered, = Ticks())                        │
                         └───────────────────────────────┬──────────────────────────────┘
                                                          │ (single consumer)
  monitor.Run ──statusCh (unbuf)──┐                       ▼
  (probes brain)                  ├──▶ ┌──────────────────────────────────────────────────┐
  enginesvc  ──runningCh (unbuf)──┤    │ hub.Run  (owns clients map + lastTick + running + │
  handler                         │    │           status — no locks)                     │
  client handler ──register───────┤    │                                                  │
                 ──unregister─────┘    │   per client: send chan (buffered 64) ───┐        │
                         ┌─────────────┴──────────────────────────────────────────┼──────┐│
                         │ per WebSocket client = 3 goroutines:                    ▼      ││
                         │   • writeLoop (Handler goroutine): send chan ──▶ ws socket      │
                         │   • read pump: conn.Read(Background) — discards inbound          │
                         │   • closer:   <-ctx.Done() ──▶ one conn.CloseNow()               │
                         └───────────────────────────────────────────────────────────────┘
  fixed goroutines: main(run/select) · ticker.Run · hub.Run · monitor.Run · http accept · gRPC accept
```

### Channel-close discipline (who closes what)
Every channel has **exactly one closer or none** — closing is only ever done by a channel's single writer,
and multi-sender input channels are **never** closed (shutdown flows through `ctx` + a `done` guard instead,
so no sender can ever race a close and panic).

| Channel | Buffer | Writer(s) | Reader | Closed by |
|---|---|---|---|---|
| `ticker.out` (`Ticks()`) | 0 | `ticker.Run` (sole) | `hub.Run` | `ticker.Run` on exit (signals stream end) |
| `ticker.cmds` | 0 | control methods (many) | `ticker.Run` | **never** — `send()` guards on `<-t.done` |
| `ticker.done` | 0 | — | control methods | `ticker.Run` on exit |
| `command.ack` | 0 | — | `send()` | `ticker.Run` after applying (makes control synchronous) |
| `ticker.reply` | 1 | `ticker.Run` | `State()` | never (GC'd); buffer 1 so Run never blocks |
| `hub.register`/`unregister` | 0 | client handlers (many) | `hub.Run` | **never** — done-guarded |
| `hub.statusCh` | 0 | `monitor` (via `BroadcastStatus`) | `hub.Run` | **never** — done-guarded |
| `hub.runningCh` | 0 | gRPC handlers (via `SetRunning`) | `hub.Run` | **never** — done-guarded |
| `hub.done` | 0 | — | `BroadcastStatus`/`SetRunning` | `hub.Run` on exit |
| `client.send` | 64 | `hub.Run` (sole) | `writeLoop` | **never** — `writeLoop` exits on `ctx`, not on close (no close/send race) |
| `main.errc` | 2 | http+gRPC server goroutines | `main` | never |

### Context propagation & shutdown ordering
One signal context (`signal.NotifyContext`) is passed to `ticker.Run`, `hub.Run`, and `monitor.Run`; **all
three exit on `ctx.Done()`**. Per-client goroutines deliberately use a `context.WithCancel(context.Background())`
(not the HTTP request ctx): the WebSocket outlives the request, and reads use a background context so they
never trigger coder/websocket's graceful-close path (which deadlocks against `CloseNow`). That client ctx is
cancelled on **every** teardown path — drop (`deliver` default), disconnect (read-pump error), engine shutdown
(`hub.Run`'s defer cancels all clients), and handler return.

Shutdown sequence (SIGTERM):
1. `ctx` cancels → `main`'s select unblocks → `httpSrv.Shutdown(5s)` + `grpcSrv.GracefulStop()`.
2. `ctx` also reaches the three `Run` loops: ticker returns (closes `out`+`done`), monitor returns, hub returns.
3. `hub.Run`'s defer cancels every client ctx → closers `CloseNow()` → read pumps + writeLoops return →
   Handler goroutines return → `httpSrv.Shutdown` observes handlers finish and completes.

### Buffer-size rationale
- **`client.send` = 64** — absorbs a 30× burst (~30 ticks/s) so a *briefly*-busy but alive browser isn't
  dropped, while bounding per-client memory. A client that can't drain 64 queued messages is genuinely too
  slow and is dropped (backpressure). Tests force the drop deterministically with buffer 8 + ~64KB payloads.
- **All command/control channels = unbuffered** — synchronous handoff to the single owning goroutine; no stale
  queued commands, and the owner never blocks (its only sends are the non-blocking per-client `deliver`), so
  senders return promptly. `ticker.out` unbuffered gives exact pacing and keeps the injected-clock tests
  deterministic.
- **`reply` = 1, `errc` = 2** — sized so a producer never blocks even if the consumer has moved on.

### Lock-order consistency
**There is no lock order to reason about** — production hot paths hold **no mutexes**. Each of the ticker,
hub, and monitor is a single goroutine that exclusively owns its mutable state (the actor pattern); all
cross-goroutine communication is by channel. The only mutexes in the tree are in the test-only `FakeClock` and
test recorders. This is the strongest possible answer to "lock-order consistency": there are no locks to order.

---

## The three mechanisms (explainer)
The parts to be able to explain end-to-end:

1. **Snapshot-on-connect.** Every browser gets one `snapshot` frame *before* any live tick, built from the
   hub's own `lastTick` + `running` + `status` (all owned by `hub.Run`, so it never calls back into the
   ticker — that would deadlock against the unbuffered emit). A client joining mid-replay is thus *caught up*
   to the exact current NAV/position/health, then receives only *subsequent* ticks — nothing missed, nothing
   duplicated.
2. **Backpressure-drop.** Each client has a **buffered** send channel (64). The single broadcaster does a
   **non-blocking** send to every client and, on a full buffer, **drops** that client (delete from the map +
   cancel its ctx → its socket is force-closed). So one slow browser can *never* stall the fan-out or any
   other client — the hub trades a dead-weight consumer for the health of everyone else.
3. **Degradation state machine.** The monitor probes brain every 2s and runs a hysteresis state machine:
   **2 consecutive misses → `degraded`**, **2 consecutive hits → `healthy`**, broadcast once per real
   transition (a single blip never flaps the badge). Throughout, the replay keeps streaming from the
   **cached curve** — brain being down changes only the status flag, never the tick stream.

## Test coverage
Full suite under `go test -race` with `goleak.VerifyTestMain` in every package.

| Package | Coverage | Residual (all defensive / test-infra) |
|---|---|---|
| `httpapi` | 100% | — |
| `config` | 100% | — |
| `enginesvc` | 100% | — |
| `health` | 97.5% | `New`'s nil-logger default branch |
| `hub` | 97.1% | `websocket.Accept` failure path, register↔done race window |
| `brainclient` | 93.3% | `Dial`'s `grpc.NewClient` error path (malformed target) |
| `replay` | 91.4% | shutdown-race `done`-guard early returns, `Seek` past-end edges |
| `clock` | 88.2% | `RealClock` is a thin `time.*` wrapper; `FakeClock` (fully exercised) mirrors its contract |
| **internal total** | **94.6%** | |

**Justified gaps.** Generated `gen/` (protobuf, 0%) and the `cmd/` mains are excluded — codegen isn't
hand-written, and the wiring in `cmd/engine` is exercised by the integration harnesses (`test_degradation.sh`,
`degradecheck`, `soak`), not unit tests. Every residual uncovered line inside `internal/` is a defensive
error/shutdown-race branch that can't be triggered deterministically from a unit test (a failed
`grpc.NewClient`, a `websocket.Accept` error, a `<-done` early return during shutdown) or a `FakeClock` edge.

**`time.Sleep` audit.** Exactly **one** `time.Sleep` exists in the entire test suite —
`hub_test.go` `TestClientDisconnectMidStreamIsCleanedUp` (a 50ms settle so the hub reaps a mid-stream
disconnect *before* the test's shutdown teardown, isolating that path). `goleak` is the authoritative
assertion (it fails on any leaked goroutine), and the same reap path is also covered **without** a sleep by
`TestSlowClientIsDroppedAndOthersAreUnaffected` (drop → cancel → reap). A true sync point would require a
test-only unregister hook in production code; the sleep is retained as a documented, reviewed exception — not
flaky (goleak asserts), not slow.

---

## Soak results
`go run ./cmd/soak -duration 30m -clients 20 -kill-every 3m -brain-down 20s -sample-every 30s` against the live
compose stack (engine built with `ENGINE_ENABLE_PPROF=1`). 20 WebSocket clients held open, the replay looped
at 30×, brain killed & restarted every 3 minutes, goroutines + heap sampled from pprof every 30s.

| Elapsed | Goroutines | HeapAlloc | Sys | Ticks delivered |
|---|---|---|---|---|
| 0s | 79 | 2.2 MB | 14.1 MB | 0 |
| 4m30s | 79 | 2.4 MB | 18.6 MB | 129,600 |
| 9m30s | 80 | 2.5 MB | 18.8 MB | 273,600 |
| 14m30s | 80 | 1.3 MB | 18.8 MB | 417,600 |
| 19m30s | 80 | 2.2 MB | 18.8 MB | 561,600 |
| 24m30s | 80 | 2.5 MB | 18.8 MB | 705,600 |
| 29m30s | 80 | 1.6 MB | 18.8 MB | 849,600 |

**Result: PASS.** Over 30 minutes / 60 samples / **9 brain kills**:
- **Goroutines flat**: baseline 79, final 80, min 79, max 80 — a range of **one** across the whole run (the
  expected ≈ 16 at rest + 20 clients × 3 goroutines). No leak.
- **Heap flat**: sawtooths 1.3–3.0 MB (GC), final (1.6 MB) *below* baseline (2.2 MB) — no growth. `Sys` warms
  to 18.8 MB in the first ~5 min (Go runtime arena) then holds dead flat for the remaining 25 min.
- **No client dropped**, **864,000 ticks** delivered across the 20 clients, and the replay never stalled across
  any of the 9 outages (each: engine → degraded, ticks continue from cache, brain back → healthy).

---

## Cross-contract check
WS wire (`hub.Message`) vs `docs/ws-contract.md` vs the ledger's REST valuation contract
(`/api/goals/{id}/valuation`).

| Check | Result |
|---|---|
| **NAV is always integer cents** | ✅ WS `navCents` is `int64` cents; ledger `navCents`/`*Cents` are cents; brain `NavPoint.nav.cents` is cents. No float on any wire. |
| **Health vocabulary matches** | ✅ WS `status: "healthy"｜"degraded"` aligns with the ledger's valuation `degraded: bool` — same word, same meaning (brain unreachable ⇒ serving a cached/fallback value). |
| **Replay vs lifecycle vocab don't collide** | ✅ The engine's `running`/`terminal` describe the *replay*; the ledger's `draft/active/riding/cashed_out/succeeded/failed/settled` describe the *commitment*. Distinct namespaces, no drift. |
| **Doc fields match the wire** | ⚠️ One drift found & fixed — see finding R5-1. |

## Findings & resolutions
Four lenses (close-discipline+shutdown · ctx+buffers · data-race/actor · cross-contract), each finding
adversarially verified by refutation.

**Concurrency hunt — clean bill (0 findings across 3 lenses).** No send-on-closed-channel, no goroutine/timer
leak, no context that fails to cancel, no unbounded growth, no deadlock or shutdown hang, and **no data race**
on the actor-owned state (`lastTick`/`running`/`status`/`clients` in the hub; `position`/`speed`/`running`/
`finished` in the ticker; the monitor's counters) — every one is touched only inside its owning `Run`
goroutine. The `-race` suite + `goleak` + the soak corroborate this.

**Cross-contract — 1 confirmed (fixed), 1 refuted.**
- **R5-1 (low, contract-drift) — FIXED.** `snapshotMessage` sets `status` on the snapshot frame, but
  `ws-contract.md` annotated `status` as "status messages only" and omitted it from the snapshot's field list.
  A docs-faithful frontend that connected during a **sustained** outage would ignore `snapshot.status` and
  never learn the engine was degraded — because a `status` message is only sent on a *change*, and a
  boot-degraded engine that stays degraded emits none. This is precisely the path the B20 boot-status fix
  relies on. **Resolution:** documented that the snapshot carries the current `status` (read it on connect) in
  `docs/ws-contract.md`, and locked it with `hub.TestSnapshotCarriesCurrentStatus`. (Fixing the doc, not the
  code: a joiner *should* learn health from the snapshot — that's the correct behavior.)
- **Refuted:** "status frames send `position:0`/`navCents:0`." The contract dispatches on `type`; a conformant
  client reads `status` from a status frame and never applies NAV from it. The pre-start snapshot already
  legitimately carries `navCents:0`, and no non-Go consumer exists. Not a triggerable defect.

**Coverage-driven fixes (this review).**
- Removed dead code: `health.Monitor.Status()` (zero callers).
- Added tests for previously-uncovered behavior: `enginesvc.SetSpeed` (0→100%), `brainclient.Probe` (reachable
  + unavailable), and defaulting paths in `config`, `hub.NewHub`, `health.withDefaults`, and `clock.RealClock`.
- Internal coverage rose **~89% → 94.6%**; `config`/`enginesvc`/`httpapi` now 100%.

---

## How to reproduce
```bash
cd services/engine
go test -race ./...                                   # full suite, goleak-clean
go test -race -covermode=atomic -coverprofile=c.out ./internal/... && go tool cover -func=c.out

# soak (needs the compose stack up + pprof enabled):
ENGINE_ENABLE_PPROF=1 docker compose up -d engine
go run ./cmd/soak -duration 30m -clients 20 -kill-every 3m -out soak.csv

# degradation drill:
./scripts/test_degradation.sh
```
