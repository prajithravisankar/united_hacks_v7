# R4 — Financial Core Audit (Epic 3)

*The money audit. Read this cold and you can defend every cent: where money lives, how it can and cannot move, and why the two stories — $100 → $146.75 and the $90/$10 failure — are exact. This is the interview answer.*

---

## 1. Headline

**The financial core is conservation-clean, audit-consistent, and correct to the cent.** 265 ledger tests (Domain **93.0%** — the repo's highest hand-written coverage). A reconciliation script recomputes every balance from raw postings and finds zero unbalanced transactions, global sum zero, and zero receipt-vs-ledger mismatches — after the golden path **and** after the entire test suite. Brain's valuation preview equals the ledger's settlement to the cent across four stakes. A cross-cutting adversarial audit found **two real audit-trail bugs** (forgeable keys, receipt divergence) — both fixed with regression tests.

## 2. The six accounts (where money lives)
| Account | Meaning | Rule |
|---|---|---|
| `USER_ESCROW` | the user's **protected principal** — the floor | never negative, never enters `ACTION_POOL` |
| `ACTION_POOL` | the fund **float** — fronts deposits, pays gains | may run negative (house capital) |
| `USER_YIELD` | the user's **realized winnings**, payable | credited on cash-out / success |
| `WINNERS_BONUS_POOL` | **forfeited stakes** — funds bonuses | never negative (global lock + guard) |
| `CHARITY_PAYABLE` | owed to charity (the 10% on failure) | — |
| `HOUSE_CARRY` | the platform's **carry** — 15% of gains only | never on principal, never on charity |

Money is integer cents everywhere. **Every transaction's postings sum to zero**, so the sum across all six accounts is conserved forever.

## 3. The four settlement recipes (worked to the cent)
Stake **$100 (10 000¢)**, NAV **$155**, gain **$55**, carry = 15% of gain = **$8.25**.

**Cash-out** — user **$146.75**, house **$8.25**:
```
USER_ESCROW -10000   ACTION_POOL -5500   USER_YIELD +14675   HOUSE_CARRY +825      Σ=0
```
**Cash-out at a loss (floor)** — NAV $90: `USER_ESCROW -10000 → USER_YIELD +10000` (exactly principal; the escrowed principal covers the floor, no carry). **Even a negative NAV floors at principal** (tested).

**Success** — cash-out **+ bonus** = `min(10% principal, winners-pool balance)` (never over-drawn).

**Failure** — user **$90**, charity exactly **$10**, house **$0**:
```
USER_ESCROW -10000   USER_YIELD +9000   CHARITY_PAYABLE +1000   (if gain>0: ACTION_POOL -gain → WINNERS_BONUS_POOL +gain)   Σ=0
```
Odd cents ($33.33): charity = banker's-rounded 10% = 333, user takes the remainder 3 000 → **no lost cent**.

**Ride** — no posting; new base = NAV, floor stays the **original** principal.

## 4. How money CAN and CANNOT move
**Can:** deposit (ACTION_POOL→USER_ESCROW on activation); cash-out/success/failure settlements (balanced groups above); a success bonus draw from the winners pool. That's it — every path is one balanced group through the B12 gate.

**Cannot** (each rejected by a named test — the "money hunt"):
- **cash out without a cleared milestone** → `IllegalTransition` (`Attack_cash_out_without_a_cleared_milestone_is_rejected`)
- **ride past the final leg** → `IllegalTransition`
- **settle twice / before terminal** → one settlement / `IllegalTransition`
- **post an unbalanced group** → `UnbalancedPostingsException`
- **drive escrow into the action pool** → `EscrowViolationException` (`Principal_Never_Enters_Action_Pool`)
- **negative / zero stake** → `LedgerValidationException`; **negative NAV** → floors at principal
- **submit proof after the deadline** → the deadline gate trips inside the command transaction → rejected
- **replay a referee decision** → idempotent no-op
- **over-draw the winners pool** (concurrent successes) → global lock + non-negative guard
- **failure ever pays the house** → 0 to house, exactly 10% to charity

## 5. Cross-service parity (brain ↔ ledger)
Brain previews `carry_preview` and `user_take_home` (Python `Decimal` ROUND_HALF_EVEN, floored). The ledger **independently recomputes** them (C# `Math.Round` ToEven). For the same (principal, NAV) they must agree to the cent — proven for stakes $20 / $100 / $333.33 / $500 against the real brain container (`Brain_valuation_preview_equals_ledger_settlement_to_the_cent`). Live, a $100 stake values to NAV **14960** → take-home **14216** / carry **744** on both sides.

## 6. Reconciliation (`scripts/reconcile.sh`)
Recomputes every balance from raw postings and checks: no unbalanced transaction, global sum = 0, escrow ≥ 0, winners pool ≥ 0, and **every receipt matches the ledger it settled**. Result after a fresh reset + the golden path — and again after the whole 265-test suite:
```
unbalanced transactions : 0     global sum of all postings : 0     receipt-vs-ledger mismatches : 0     RECONCILE OK
```

## 7. Findings & resolutions
**This audit (R4) — 2 HIGH, both fixed:**
1. **Forgeable system keys.** The derived transition keys (`settle:{id}` etc.) weren't in the reserved `sys:` namespace, and the `commitment_events` idempotency check is global — so a caller could POST `/proof` with `idempotencyKey:"settle:{victim}"` and poison the victim's settle transition (paid + receipted but stuck at `cashed_out`; cross-user griefable). **Fix:** system keys are now `sys:`-prefixed and unforgeable; `TransitionAsync` takes a `systemKey` flag so the guard blocks caller forgery but lets the system through. Regression: `A_caller_cannot_forge_a_system_key_to_poison_another_settlement`.
2. **Receipt could diverge from the money.** On an idempotent replay (crash+retry / concurrent settle), the receipt was written from the *recomputed* plan, whose pool-dependent Success bonus could differ from what was posted. **Fix:** the receipt is now **reconstructed from the ledger's actual postings**, so it can never disagree with the money. Regression: `Success_receipt_is_reconciled_from_the_ledger_not_the_recomputed_plan`.

**Earlier build-time audits (B12/B13/B15) — all fixed before commit:** principal over-release via a null-commitment escrow debit; a command applied to a live past-deadline leg under concurrency; the winners-pool over-draw; a crash bricking a settlement. See each assignment doc.

**Refuted (not bugs):** unvalidated `CharityId`/`DriveMode` producing a 500 (a DB-FK 500, below the money-bug bar — a future 422 refinement); the key-collision claim in its DoS framing (the money/state divergence version was the confirmed one).

## 8. Coverage
Domain **93.0%** (238/256) — highest hand-written project (Api 84.7%, Migrations 68.9%; generated Contracts excluded). The 18 uncovered Domain lines are all non-logic: `const` rate declarations, an unreachable `_ => throw` enum default, one unused helper, and exception constructor bodies thrown only under specific runtime failures (concurrency conflict, applock timeout, pool-insufficiency, milestone-not-found). **The money-logic classes — `SettlementCalculator`, `CommitmentMachine`, `PostingPlan`, `LedgerService`, `MoneyMath` — are fully covered.**

## 9. The two stories (walk these unaided)
- **Win/cash-out:** you stake **$100** → it's escrowed (protected) while the fund rides to **$155** → you clear a milestone → cash out. You get your **$100 back plus $46.75** of the $55 gain; the house takes **$8.25** carry (15% of the gain only). Escrow empties, everything balances. **You could never get less than $100.**
- **Fall short:** you miss a gate → **$90 back to you, $10 to your charity**, zero to the house, and any fund gains are forfeited to the community pool that funds the people who finish. *"We never profit from your failure."*

## 10. Verdict
Epic 3 passes the money audit. Conservation holds, receipts match the ledger, the two services agree to the cent, every adversarial money move is rejected by a named test, and the audit trail can no longer be forged or made to lie. Cleared to build the engine (Epic 4).
