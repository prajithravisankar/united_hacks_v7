# E3-B12 — Double-Entry Escrow Ledger (Append-Only, Idempotent)

## What we built (plain English)
The one gate every cent moves through. A **double-entry** postings engine: each money movement is a group of signed cent-deltas across the six accounts that **sums to exactly zero** (no money created or destroyed), written **append-only** with an **idempotency key** so a retried or double-clicked post applies exactly once. Two structural invariants are enforced in the domain (pure, unit-tested), and the balance guard is enforced at persistence against the live balance. This is the auditability spine — one choke-point for money means one place invariants live and one place to test them to death.

## The six accounts (the map)
| Account | Meaning | Sign convention |
|---|---|---|
| `USER_ESCROW` | the user's **protected principal** — the floor | grows on deposit, never negative, never rides |
| `ACTION_POOL` | the platform **float / pooled capital** backing commitments | fronts deposits (may run negative — house capital) |
| `USER_YIELD` | the user's **realized winnings**, payable out | credited on cash-out / success |
| `WINNERS_BONUS_POOL` | **forfeited stakes** from quitters — funds winners' bonuses | credited on failure, debited to pay bonuses |
| `CHARITY_PAYABLE` | money owed to the user's chosen **charity** (10% on failure) | credited on failure |
| `HOUSE_CARRY` | the platform's **carry** — 15% of gains only | credited on cash-out / success |

Balances are `BIGINT`/`long` integer cents. The **sum across all six accounts is conserved** by every transaction — a property test posts 5,000 random balanced transfers and the total stays exactly zero.

## The invariants (as executable rules)
1. **Balanced.** `PostingPlan`'s constructor rejects any group whose deltas don't sum to zero (`UnbalancedPostingsException`) — an invalid plan cannot be constructed.
2. **Principal never rides** (`Principal_Never_Enters_Action_Pool`, a named test). No single group may debit `USER_ESCROW` while crediting `ACTION_POOL` → `EscrowViolationException`. `ReleaseEscrow(to: ACTION_POOL)` is rejected by the same rule.
3. **Escrow never negative.** At persistence, a plan that would drive this commitment's `USER_ESCROW` below zero is rejected (`NegativeBalanceException`) — you can't release more principal than was escrowed. The guard reads the live balance under a per-commitment lock, so it can't be raced.
4. **Idempotent, even concurrently.** `idempotency_key` is `UNIQUE`; a `sp_getapplock` keyed on the commitment serializes same-commitment posts (different commitments still run in parallel — no deadlocks). Eight parallel posts of the same key → exactly one applies; the rest return the original txn as a no-op. Tested.
5. **Append-only.** `UPDATE`/`DELETE` on `ledger_postings` / `ledger_transactions` are blocked by DB triggers (from B06) — proven by an integration test.
6. **Atomic.** Header + all lines commit together or not at all; a rejected post writes nothing (tested: a failed over-release leaves the balance unchanged).
7. **Escrow is always commitment-scoped.** A `USER_ESCROW` posting must belong to a commitment (`PostingPlan` rejects an escrow line with a null `commitmentId`). This is what makes rule 3's guard measure the *right* scope and be serialized by the right lock.

### Hardened by adversarial audit
Before committing, a multi-agent adversarial audit (four independent lenses, each finding then refuted by a skeptic) hunted for invariant holes. It caught a real one: the negative-escrow guard measured **global** escrow for a `commitmentId=null` plan but **per-commitment** escrow otherwise, and the two scopes weren't serialized — so a `BuildTransfer(commitmentId: null, …)` escrow debit could over-release principal and drive `USER_ESCROW` negative (even sequentially). The primitives never emit such a plan, but the general `BuildTransfer` seam permitted it — a trap B15 would have sprung. Fix: invariant 7 above (escrow always commitment-scoped) + the guard now always scopes per-commitment under the commitment lock. Regression test: `Escrow_posting_without_a_commitment_is_refused`.

## The two primitives (B12) and the posting recipes (design → implemented in B15)
B12 ships the primitives; B15 composes the settlement recipes from them. Every recipe is a balanced group. Worked with a **$100.00 stake (10 000¢)**, fund NAV **$155.00**, gain **$55.00**, carry = 15% of gain = **$8.25**.

**Deposit / escrow (on activation) — B12, live now**
```
ACTION_POOL   -10000
USER_ESCROW   +10000      (principal escrowed, floored; action pool fronts the float)
```

**Cash-out / graceful exit (verified milestone) — B15**
```
USER_ESCROW        -10000   (release principal)
WINNERS_BONUS_POOL  -5500   (realize the $55 gain from the pool)
USER_YIELD         +14675   (principal 10000 + gain-after-carry 4675 = $146.75)
HOUSE_CARRY          +825   (15% of the $55 gain = $8.25)
                    ------
                        0
```

**Success (final goal hit) — B15**: cash-out **plus** a bonus draw `WINNERS_BONUS_POOL -bonus → USER_YIELD +bonus` (bonus by a documented pool-share formula; the pool is never over-drawn).

**Failure (miss milestone / deadline) — B15**
```
USER_ESCROW      -10000   (release principal)
USER_YIELD        +9000   (90% back to the user — "never lose your deposit" ... minus the 10%)
CHARITY_PAYABLE   +1000   (exactly 10% to the chosen charity — "never profit from failure")
                  ------
                      0
```
Odd-cent rule (e.g. $33.33): charity gets banker's-rounded 10%, the **user takes the remainder**, so no cent is lost — documented and property-tested in B15. Positive notional yield at failure is forfeited to `WINNERS_BONUS_POOL` (0 to the house).

**Carry (never on principal, never on charity):** `carry = round_half_even(gain × 0.15)` only when `gain > 0`; the user keeps `gain − carry`. B15 owns the exact math and its property tests.

## How to run / verify it
```bash
cd services/ledger && dotnet test Boys.Ledger.sln     # domain invariants + real-DB idempotency/concurrency
curl -s localhost:8080/internal/commitments/1/balances # per-account balances for a commitment
curl -s localhost:8080/internal/accounts/USER_ESCROW/balance
```

## Gotchas / follow-ups
- `ACTION_POOL` may go negative in the demo (it's the house float fronting principals); only `USER_ESCROW` is floored at zero, by design.
- The escrow-inviolability rule is presence-based (debit-escrow + credit-action in one group). Settlement keeps escrow-release and action-pool credits in separate concerns, so it never trips falsely.
- Next: B13 puts the commitment state machine in Domain; B15 composes the recipes above into real settlements through this same gate.
