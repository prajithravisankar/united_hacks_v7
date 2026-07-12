# R6 — Final Review & Handover (whole system)

The last review: a clean-bootstrap verification, coverage across all three services, five cross-cutting
audits, the money stories with real numbers, the honest limitations, and a day-1 guide for the frontend
track. **Verdict: green.** One audit finding (a resilience seam) was found and fixed; every other lens came
back clean.

## System map
```
                         ┌──────────────── nginx :8888 (single origin, no CORS) ────────────────┐
  browser / board  ────► │  /api → ledger:8080        /ws/live → engine:8090                    │
                         └──────────────────────────────────────────────────────────────────────┘
   REST │                         gRPC │                              WebSocket ▲
        ▼                              ▼                                        │
  ledger (.NET 9)  ──gRPC──►  brain (Python 3.12)  ◄──gRPC── engine (Go 1.25) ──┘
  escrow · double-entry       quant fund (NAV curve)          deterministic 30× replay
  state machine · settle      + AI referee (seeded)           + WS fan-out
        │                            │                               │ (curve fetched once at boot,
        ▼                            ▼                               │  self-heals if brain was down)
  SQL Server (OLTP)          Oracle (OLAP warehouse)          cached curve in-memory
```
Money is **integer cents everywhere**, banker's rounding matched across .NET (`ToEven`) and Python
(`ROUND_HALF_EVEN`). Boot order (health-gated): dbs → `data-seed` (builds the NAV curve into Oracle) →
brain/ledger → engine → nginx.

## The three money stories (real numbers, $100 stake, reconcile-clean)
Settlement values come from the backtested curve at the fund window (2021-08-13 → 2024-05-19, +49.6% on the
$100 principal → NAV **$149.60**). All exact to the cent; `reconcile.sh` clean after each.

| Story | Path | Receipt |
|---|---|---|
| **Cash out** | clear 1 leg → cash out → settle | `CashOut` — NAV $149.60, gain $49.60, **carry $7.44** (15%), **take-home $142.16** |
| **Success** | clear 3 legs → succeed → settle | `Success` — same as cash-out **+ bonus** (`min(10% of principal, winners pool)` — $0 here on a fresh pool, up to $10 from a funded pool) → **$142.16** |
| **Failure** | referee reject **or** deadline miss → settle | `Failure` — **charity $10.00** (10%), **take-home $90.00** (90%), carry $0; positive yield forfeited to the winners pool |
Canonical worked example (from the settlement docs): $100 stake, NAV $155 → take-home $146.75, carry $8.25.

## Tests & coverage
Full gate green (`./scripts/verify.sh` → `ALL GREEN`): **~413 automated tests**.

| Service | Suite | Tests | Coverage (hand-written) | Notable gaps (justified) |
|---|---|---|---|---|
| ledger (.NET) | unit + integration | 200 + 65 | Domain **84.4%**, Api **83.0%** | generated Contracts/Migrations excluded (codegen); residual = defensive error branches |
| brain (Python) | pytest | 74 | **88%** | residual = provider/error edges, live-Gemini path (offline in demo) |
| engine (Go) | `-race` + goleak | 56 funcs / 9 pkgs | internal **94.6%** | residual = shutdown-race `done`-guards, `RealClock` wrapper (R5) |
| e2e (Python) | pytest through nginx | 9 | — | drives the demo stories end-to-end, exact-cent + reconcile |

## Cross-cutting audits (5 lenses, adversarial find→refute)
| Lens | Result |
|---|---|
| **Money conservation** | ✅ clean — no path creates/destroys money; postings sum to zero (trigger-enforced + tested); no float on any money path; ledger↔brain rounding agrees to the cent; winners pool never over-drawn. |
| **Determinism** | ✅ clean — NAV curve **byte-identical across builds** (verified); reset is byte-identical (E2E); no wall-clock/randomness on any curve/money/settlement outcome (idempotency keys use commitment ids, not randomness). |
| **Contracts** | ✅ clean — protos↔codegen construct test green; ledger `openapi.json` drift test green; money is integer cents on every wire; state/status vocabulary consistent across REST/WS/docs. |
| **Resilience** | ⚠️ **1 found → FIXED.** See below. Matrix: **oracle down** → brain serves from its cached engine, engine healthy; **mssql down** → ledger hard-fails (correct — no stale money data), recovers; **nginx down** → edge only, services direct still 200; **brain down** → engine streams cached curve + degraded/healthy, ledger valuation 200/degraded; **engine down** → replay interrupted, **self-heals**. |
| **Security hygiene** | ✅ clean — no secrets in tracked files (`.env` gitignored); Dapper parameterizes all SQL (no injection); evidence bytes never logged (only a hash/uri); referee authority enforced (`X-User-Id` role check); system idempotency keys (`sys:`) unforgeable by callers. |

**R6-1 (resilience, medium) — FIXED.** If the engine booted while brain was down, it fetched an empty curve
once and never re-fetched — a permanently frozen tick stream, and after brain recovered the monitor
broadcast `healthy` over an empty stream (misleading). Fix: the engine now **self-heals** — a background
re-fetch loads the curve into the ticker (new `Ticker.Load`) the moment brain returns, flipping
`/health/ready` to 200 and streaming ticks. Verified live (boots 503/no-ticks → brain up → "now serving,
points:360" → 200 + ticks) and by `replay.TestLoadRecoversFromAnEmptyTimeline` under `-race`. (The demo never
hit this — compose gates the engine on brain-healthy — but production restarts can.)

## Known limitations (v0, deliberate) + how they'd be productionized
| Limitation | Why it's fine for v0 | Productionization |
|---|---|---|
| **No auth/identity** — `X-User-Id` header is trusted | single-tenant demo; referee role still enforced | JWT/OIDC at nginx; map identity→role; sign the header server-side |
| **Lazy deadline sweep** — a commitment fails on the next *read* after its deadline, not at the instant | correct for a demo (reads are frequent); the gate is faithful | a background sweeper job (or DB scheduled task) trips expired commitments proactively |
| **Local evidence storage** — proof bytes on the ledger's disk | fine single-node; bytes never logged | object storage (S3) + presigned uploads; keep only the URI + hash in the DB |
| **Single-node everything** — one of each container | demo scale | replicas behind nginx; SQL Server AG / Oracle RAC; the engine's actor model already shards cleanly per commitment |
| **You-drive divergence is brain-only** — the WS curve doesn't re-diverge live after a user bet | engine fetches AUTO once at boot (documented) | thread `DriveMode` through `FetchNavCurve` + a bet→re-fetch→`Ticker.Load`→rebroadcast path (the `Load` seam now exists) |
| **Winners-pool bonus needs a funded pool** | mechanic is correct; a fresh demo pool is empty | seed the pool, or run a failure before a success to show a non-zero bonus |

## Day-1 guide — building the frontend (`board`) against this backend
- **One origin, no CORS:** everything is `http://127.0.0.1:8888`. REST at `/api`, WebSocket at `/ws/live?goal={id}`.
- **Contracts to build against:** [`docs/api-contract.md`](api-contract.md) (+ machine-readable
  `services/ledger/openapi.json`) and [`docs/ws-contract.md`](ws-contract.md). Money is **always integer
  cents** — format client-side. State vocabulary: `draft/active/pending_verification/milestone_cleared/
  riding/cashed_out/succeeded/failed/settled`; WS `status: healthy|degraded`.
- **The golden flow:** `POST /api/goals` → `POST /goals/{id}/activate` → connect the WS → `POST /goals/{id}/proof`
  → (referee, `X-User-Id: 2`) `POST /milestones/{mid}/decision` → `ride`/`cashout`/`succeed` → `settle` →
  `GET /goals/{id}/receipt`. `GET /goals/{id}` returns the milestones (with ids) and the event timeline.
- **Gotcha:** body-less POSTs must send a real empty body (`fetch` and axios do this automatically;
  `Content-Length: 0`). Brain-outage endpoints degrade to `200 {degraded:true}`, never 500 — render "live
  value unavailable," not an error.
- **Bring it up:** `./scripts/demo_up.sh` → `./scripts/preflight.sh`.

## Docs index
- **Contracts:** [api-contract.md](api-contract.md), [ws-contract.md](ws-contract.md), `protos/CHANGELOG.md`, `services/ledger/openapi.json`
- **Ops:** [demo-runbook.md](demo-runbook.md), and `scripts/`: `demo_up` · `preflight` · `reset_demo` · `replay` · `ws_watch` · `check_stack` · `e2e` · `verify` · `reconcile` · `test_degradation`
- **Build assignments:** `E0-B01`…`E5-B23` (per-assignment `docs/E{e}-B{##}-*.md`)
- **Reviews:** [R1](R1-review-foundation.md) · [R2](R2-review-data-platform.md) · [R3](R3-review-brain.md) · [R4](R4-review-financial-core.md) · [R5](R5-review-engine.md) · **R6 (this doc)**

## Handover checklist (for Prajith, unassisted)
- [ ] Read this doc top to bottom.
- [ ] Trace one request through all three services in the code: `POST /api/goals` → `GoalService.CreateAsync`
      → `BrainClient.ValidateGoalAsync` → brain `RefereeServicer.ValidateGoal` → back → DB insert.
- [ ] Run the demo runbook personally, unassisted: `./scripts/demo_up.sh`, then follow
      [demo-runbook.md](demo-runbook.md) end to end (incl. the `docker kill boys-brain` beat).
- [ ] Confirm `./scripts/preflight.sh` prints **PRE-FLIGHT GREEN**.

Clean-machine bootstrap → full green from docs alone; all five audits pass (finding resolved). Handover is
complete once the runbook has been performed unassisted.
