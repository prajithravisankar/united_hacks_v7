# E3-B15 — Settlement Engine: Cash-Out / Ride / Success / Failure Money Math

## What we built (plain English)
The money endgame, exact to the cent. A **pure** `SettlementCalculator` (state + NAV → balanced posting plan + receipt) is the ledger's money authority — it computes carry, the floor, and the splits itself; brain supplies only the NAV. Every promise in idea.md lives here as a test: **"never lose your deposit"** (floor), **"never profit from failure"** (10% to charity, 0 to house), **"carry on gains only"** (15% of a positive gain, never principal). The `SettlementService` fetches the NAV, runs the calculator, posts through the B12 gate, moves the commitment to `settled`, and records the receipt — **exactly once** per commitment.

## The four recipes (worked; $100 stake = 10 000¢, NAV $155, gain $55, carry $8.25)

**Cash-out (graceful exit)** — take-home $146.75, carry $8.25:
```
USER_ESCROW   -10000   (release principal)      HOUSE_CARRY  +825    (15% of the $55 gain)
USER_YIELD   +14675   (principal + gain − carry) ACTION_POOL -5500   (the fund pays the gain)   Σ = 0
```

**Cash-out with a loss (floor)** — NAV $90 < principal:
```
USER_ESCROW  -10000    USER_YIELD  +10000   (exactly principal — the escrowed principal covers the floor;
                                             no carry, no action-pool posting)          Σ = 0
```

**Success** — cash-out **plus** a bonus = `min(10% principal, winners-pool balance)` (never over-drawn):
```
…the cash-out group…   WINNERS_BONUS_POOL -1000   USER_YIELD +1000    → take-home $156.75
```

**Failure** — 90% back, 10% to charity, positive yield forfeited to the winners pool, **0 to the house**:
```
USER_ESCROW  -10000   USER_YIELD +9000   CHARITY_PAYABLE +1000
(if gain>0)  ACTION_POOL -gain   WINNERS_BONUS_POOL +gain                Σ = 0
```
Odd-cent rule ($33.33 → 3 333¢): `charity = round_half_even(10%) = 333`, `user = principal − charity = 3 000` → **no lost cent**.

**Ride** — no posting; `newBase = NAV` (earnings compound), `floor = ORIGINAL principal` (you only ever risk your winnings). The floor holds across N rides.

## The invariants (property-tested over thousands of seeded scenarios)
- **Conservation:** every settlement group sums to exactly zero; a full deposit→settle scenario keeps the total across all six accounts at zero and returns escrow to zero (3 000 seeded scenarios).
- **Floor:** take-home ≥ principal on every cash-out/success path (2 000 seeded NAVs, including NAV = 0).
- **Carry on gains only:** `carry = gain > 0 ? round_half_even(gain × 0.15) : 0`, never exceeds the gain, never touches principal.
- **Rounding parity with brain:** banker's rounding (round-half-to-even) — the same policy brain uses (Python `Decimal` ROUND_HALF_EVEN) — so the two services never disagree by a cent.

## Exactly-once settlement
All of settlement's internal keys are **commitment-derived** (`settle:{id}`) — for the posting, the state transition, and the receipt. So retries and concurrent settlements **converge to a single settlement**: the B12 per-commitment applock + the unique idempotency key mean the money posts once, the `Settle` transition fires once, and one receipt row is written. An already-`settled` commitment short-circuits to its existing receipt. Tested: settle-twice settles once; 6 concurrent settles apply the money exactly once (losers of the state race throw and are ignored).

### Hardened by adversarial audit
A 3-lens adversarial audit (money-math / exactly-once / edge-integration) of the settlement engine caught two real issues, both fixed before commit:
- **(high) The Success bonus could over-draw `WINNERS_BONUS_POOL` under cross-commitment concurrency** — the cap `min(target, poolBalance)` was a read-modify-write on the *global* pool read outside any lock, so two Successes settling at once each drew a bonus and drove the pool negative. Fix: winners-pool **draws** now take a global applock and are guarded non-negative in the ledger; the settlement recomputes a smaller bonus (down to zero) from the live balance if a draw is rejected. Regression: `Concurrent_success_settlements_never_over_draw_the_winners_pool` (5 concurrent Successes against a pool that covers ~2).
- **(high) A crash/cancel between the `Settle` transition and the receipt insert bricked the commitment** — it left state `settled` with no receipt, so every later call threw "settled but has no receipt" (a permanent 500). Fix: reordered to **post → receipt → transition**, so the receipt always exists before the commitment is marked settled.

## How to run / verify it
```bash
cd services/ledger && dotnet test Boys.Ledger.sln    # 11 calculator + 6 service tests
# live (commitment must be cashed_out/succeeded/failed):
curl -X POST localhost:8080/internal/commitments/{cid}/settle   # returns the receipt
curl -s   localhost:8080/internal/commitments/{cid}/receipt
```

## Gotchas / follow-ups
- The **ledger is the money authority**: it recomputes carry/floor/splits from `(principal, NAV)`; brain supplies only the NAV. They agree because both use banker's rounding on `gain × 0.15`.
- **Demo fund window:** a "now" commitment's stake is valued against a historical slice of the backtested curve (`FundStartDate`..`FundAsOfDate`) — the "simulate the past as if live" mechanic.
- **All-or-nothing** is via ordering + idempotency (post → transition → receipt, all keyed `settle:{id}`), not one physical transaction across the three tables; a crash mid-way is recovered by re-running settlement (each step is idempotent). Settling needs brain for the NAV — a brain outage means "settle later," not a wrong settlement.
- Next: B16 exposes cash-out/ride/settle as the public REST API with the standard envelope and the community-pool stats.
