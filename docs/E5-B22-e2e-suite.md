# E5-B22 — End-to-End Test Suite (Golden, Failure & Degradation Paths)

## What we built (plain English)
A Python E2E suite (`tests/e2e/`) that drives the exact stories the demo tells — through the **nginx edge**,
against the real composed stack — with **every settlement asserted to the cent** and **reconciliation clean
after each scenario**. 8 tests: the golden Success path, cash-out, the two failure paths (referee reject &
deadline expiry), the you-drive divergence, the degradation beat, and the reset property. Green twice
consecutively against a cold-booted stack (17s / 21s).

## Key decisions
- **Through nginx, exact-cent, reconcile-checked.** Tests hit `http://127.0.0.1:8888/api` (the edge). Money
  assertions recompute the expected receipt with the ledger's own **banker's rounding** (`Decimal` +
  `ROUND_HALF_EVEN`) and compare field-by-field — no tolerance ranges. Each money test calls
  `scripts/reconcile.sh` and requires `RECONCILE OK`.
- **Curve-relative, not hard-coded numbers.** Settlement NAV comes from the backtested curve, so the tests
  assert the *relationships* (`gain = nav − principal`, `carry = round_half_even(gain×0.15)`,
  `takeHome = max(principal, principal+gain−carry)`, `charity = round_half_even(principal×0.10)`), reading the
  actual `navCents` from the receipt. Robust to any curve.
- **Faithful shortcuts where a live path isn't wired** (documented deviations, below): the deadline miss is
  induced by moving the commitment's dates into the past (the real lazy-gate then trips it on read); the
  you-drive divergence is asserted at the brain-gRPC level (the engine's live WS curve is boot-once AUTO).

## The stories (all reset commitment 1 to pristine first, via `seed_demo.sh`)
- **Golden Success** (`test_golden_success_path`): 3 legs (proof → referee approve → ride, ×2, then
  proof → approve → **succeed**) → settle → **Success** receipt asserted to the cent (incl. the winners-pool
  bonus = `min(10% of principal, pool)`), receipt stable, `settled`, reconcile clean. Also collects live ws
  ticks for goal 1 through nginx (`test_live_ws_ticks_stream_through_nginx`).
- **Cash-out** (`test_cashout_after_leg_one`): clear leg 1 → cashout → settle → **CashOut** receipt to the
  cent (carry on the gain, take-home floored at principal), **escrow released to zero**, reconcile clean.
- **Failure — referee reject** (`test_failure_path_referee_reject`): proof → referee **reject** → `failed`;
  a **dead commitment rejects further actions**; settle → **Failure** receipt (charity 10%, user 90%, exact),
  reconcile clean.
- **Failure — deadline** (`test_deadline_expiry_is_a_failure`): push the commitment's dates into the past →
  `GET` trips it to `failed` with a `deadline_gate` timeline event → settle → **identical Failure** math.
- **You-drive** (`test_you_drive_user_bet_diverges_from_auto`): brain gRPC — USER curve == AUTO before any
  bet; `ListOpenMarkets` → `PlaceUserBet` (accepted) → USER curve now **diverges** from the AUTO baseline.
- **Degradation** (`test_degradation_beat`): runs `scripts/test_degradation.sh` (1 cycle) — engine streams
  from the cached curve, broadcasts degraded, recovers; ledger degrades to 200, never 500.
- **Reset property** (`test_reset_restores_identical_state`): snapshot → mutate → `seed_demo.sh` →
  snapshot **byte-identical** (the retake guarantee).

## Integration glue this assignment added
The E2E exposed real gaps; B22 owns fixing them:
- **`POST /api/goals/{id}/succeed`** — the `succeeded`/Success terminal had **no HTTP endpoint** (`Complete`
  was unreachable), so the golden Success path couldn't be driven at all. Added it.
- **`GET /api/goals/{id}` now returns `milestones`** (with ids) — no read endpoint exposed milestone ids, so a
  client couldn't submit proofs. Added the `milestones` array.
- **Fixed a real bug: multi-leg ride was broken.** `ride` used a fixed idempotency key `sys:ride:{id}`, so the
  **2nd+ ride was a silent no-op** (the demo's ride-ride-succeed died at leg 3). Now keyed per-leg
  (`sys:ride:{id}:leg{clearedCount}`) — multi-leg works, and a double-click on the same leg still collapses to
  one transition. Regenerated `services/ledger/openapi.json` (drift test green).

## How to run / verify it
```bash
./scripts/demo_up.sh        # stack up + seeded
./scripts/e2e.sh            # the full suite through nginx (records runtime)
./scripts/e2e.sh -k cashout # a single story
```
Verified: **cold boot → e2e green twice consecutively (17s, 21s)**; ledger suite 200 unit + 65 integration green.

## Gotchas / follow-ups
- **Body-less POSTs through nginx need a real empty body.** `curl -X POST` with no `Content-Length` → nginx
  **400**; proper clients (Python httpx, browser `fetch`) send `Content-Length: 0` and get 200. Use `curl -d ''`
  in shell. (Not a defect — an HTTP-framing idiom; documented for the runbook.)
- **Deadline test writes SQL** (moves `created_at`/`deadline` into the past) — the deadline `CHECK` requires
  `deadline ≥ created_at+7d`, so both move together. The lazy gate is exercised for real on the `GET` path.
- **You-drive is a brain-gRPC assertion, not a live-WS one** [DEVIATION]. The engine fetches an AUTO curve
  once at boot and never re-fetches, so the WS stream can't diverge for USER mode today. Full live-WS
  divergence would need: thread `DriveMode` through `brainclient.FetchNavCurve`, an engine runtime re-fetch,
  and a bet→re-fetch→rebroadcast path. Logged for a future assignment.
- **Winners-pool bonus is 0 in the isolated golden test** — a fresh reset empties the pool (it's fed by failed
  commitments' forfeited yield). The test asserts the exact formula (`min(10%, pool_before)` = 0 here); a
  funded-pool bonus is a compound scenario left for the demo narrative.
- Tests reset commitment 1 between money scenarios, so they're **order-independent and re-runnable**; the
  you-drive test uses a fresh brain commitment key (brain's user-bet state is per-commitment, in-memory).
