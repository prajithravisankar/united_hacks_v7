# E2-B09 — QuantService gRPC: Valuation, Projections, You-Drive

## What we built (plain English)
The real `QuantService` — the numbers ledger settles with and the frontend dramatizes. It serves the precomputed NAV curve as **per-commitment valuations** (with the 15% carry and the principal floor), **cash-now-vs-ride projections**, and the **"you drive"** market list + user bets. The logic lives in a plain `QuantEngine` that takes an already-loaded curve, so it unit-tests without a database; the servicer just translates proto ↔ engine and maps errors to gRPC status codes.

## Key decisions
- **Brain is stateless about commitments.** Ledger supplies the commitment facts — `principal_cents` + `start_date` were added to the quant read requests (additive, wire-safe). Brain does the math against the fund curve; it never stores commitments.
- **Per-commitment NAV = principal × fund growth from its start.** `nav = principal × fund_nav[as_of] / fund_nav[start]`. At the start `nav == principal`; as the fund grows, gain appears.
- **Carry/floor math is one pure function** (`compute_valuation`) shared conceptually with ledger's settlement (B15): 15% carry on gains only, `take_home` floored at the principal. The canonical `$100 → $155 → $146.75` is a named test.
- **You-drive** keeps user bets as an in-memory P&L overlay per commitment; `USER`-mode valuations add it, so the curve **visibly diverges** from `AUTO`. (Demo-scope; a real system would persist the bets.)
- **Projections are deterministic** — percentiles of the fund's historical 30-step growth, applied to the current NAV, then run through carry/floor. No randomness.

## gRPC status codes (all tested)
| Case | Status |
|---|---|
| `as_of` before `start_date` | `INVALID_ARGUMENT` |
| window before any fund data | `NOT_FOUND` |
| unknown market on `PlaceUserBet` | `BetAck{accepted:false}` |
| stake > available pool | `BetAck{accepted:false}` |

## How it works
- `app/quant/valuation.py` — pure carry/floor.
- `app/quant/engine.py` — `QuantEngine`: `get_valuation`, `get_nav_curve` (window-clips, never pads), `project_outcomes`, `list_open_markets`, `place_user_bet`.
- `app/quant/repo.py` — loads the engine from Oracle (`fact_nav_curve` + a few home-win markets from `fact_match`).
- `app/grpc/servicers.py` — `QuantServicer` wraps the engine, injectable for tests.

## How to run / verify it
```bash
cd services/brain && uv run pytest tests/quant/    # 29 tests (quant engine + valuation + gRPC)
```
Verified live against real Oracle: a $100 stake over the full 3-season curve values at **NAV $174.75 → take-home $163.54** (carry $11.21) — matching the +74.8% fund. gRPC tests cover the canonical valuation over the wire and every failure status.

## Gotchas / follow-ups
- The valuation floor applies on success/cash-out; the **failure** payout (90% principal, 10% charity) is ledger-owned (B15) and intentionally not representable in `Valuation` (see R1 note).
- `PlaceUserBet` validates stake against a demo pool cap (`MAX_USER_STAKE_CENTS`), since the request doesn't carry the live pool; user bets are in-memory (per-process).
- Next: B10 implements `RefereeServicer` (goal gate + proof check) — the other half of the brain.
