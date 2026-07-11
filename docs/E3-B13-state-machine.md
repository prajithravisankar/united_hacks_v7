# E3-B13 — Commitment State Machine (Hard Gates)

## What we built (plain English)
The product's rules made executable. A **pure** state machine — `(state, command, isFinalLeg) → new state | typed error` — with **hard gates** baked in: miss a verified milestone and the ticket is dead on the spot; the deadline passing while any leg is live fails the commitment automatically; and no illegal shortcut (cash out without a cleared milestone!) is reachable. Persisted with **optimistic concurrency** (rowversion — one racing writer wins) and an **append-only event trail** that reconstructs any commitment's full history. Every transition happens through the machine; there is no other way to change a commitment's state.

## The lifecycle (state diagram)
```
                     activate            submit_proof
        draft ─────────────────▶ active ───────────────▶ pending_verification
                                   ▲                        │        │
                          submit_  │                        │        │ reject_milestone (HARD GATE)
                          proof    │            clear_       │        ▼
                                 riding ◀── ride ── milestone_cleared │      failed
                                   │        (non-final)  │   │        │        │
                                   │                     │   │ cash_out (non-final)   │
                                   │                     │   ▼        │        │
                                   │                     │  cashed_out│        │
                                   │        complete     │            │        │
                                   │        (final leg)  ▼            │        │
                                   └──────────────────▶ succeeded     │        │
                                                          │           │        │
                                            settle ───────┴───────────┴────────┘
                                                          ▼
                                                       settled
```
`active` = a leg is running (the fund rides, awaiting proof). The complete transition table is specified and tested **exhaustively** — a `[Theory]` over **every** (9 states × 8 commands × 2 final-leg) triple asserts each is either its documented target or throws `IllegalTransitionException`. Nothing is left unspecified.

## The hard gates (locked product decisions, as code)
- **Verified miss → failed instantly.** `reject_milestone` from `pending_verification` → `failed`, regardless of legs remaining. One busted leg kills the ticket (parlay rules).
- **Deadline passes while live → failed.** A lazy sweep on every read and before every command: if the deadline has passed and the commitment is in a live state (`active`/`pending_verification`/`milestone_cleared`/`riding`), it trips to `failed` — no command needed, and a `deadline_gate` event is recorded.
- **No shortcut to money.** `cash_out`/`ride` are reachable **only** from `milestone_cleared` on a **non-final** leg; the final leg clears **only** into `succeeded`. Cashing out or riding on the final leg, or "completing" early, all throw.

## Deadline boundary — decided and tested to the tick
The boundary is **strict**: `deadlinePassed = clock.UtcNow > deadline`. The deadline instant *itself* is not yet a miss; anything strictly after it is. Tested to the tick: at exactly the deadline the commitment stays `active`; one tick later it is `failed`. Time is read only through the injected `IClock`, so this is deterministic under test — no wall-clock in the domain.

## Concurrency, idempotency, and the audit trail
- **Optimistic concurrency (rowversion).** `commitments` gained a `ROWVERSION` column (migration 004). A transition reads the version, then `UPDATE … WHERE row_version = @seen`; a concurrent transition that already moved the row updates **zero rows** and loses with a `ConcurrencyConflictException`. Tested: two writers race the same `draft → active`; exactly one wins, and the trail records the transition once.
- **Idempotent re-application.** Each transition writes one `commitment_events` row keyed by a `UNIQUE idempotency_key`. Re-delivering the same command returns the recorded result as a no-op (`WasApplied = false`).
- **Append-only trail.** `commitment_events` is append-only (trigger, like the ledger). Replaying its `(from → to)` chain from `draft` reconstructs the live state — tested by replaying a real sequence and matching the final state.

### Hardened by adversarial audit
A 2-lens adversarial audit (find → refute) of the concurrency/deadline/hard-gate logic caught three real issues, all fixed before commit:
- **(high) A command could be applied to a live, past-deadline leg** — the deadline gate lived only in a pre-transaction sweep, whose rowversion trip could silently no-op under a concurrent transition, laundering a missed deadline into a cashed-out/succeeded (money-won) state. Fix: the deadline gate now runs **inside the command transaction** under the same rowversion guard, so no command ever commits against a stale read. Regression: `Command_after_the_deadline_trips_and_is_rejected_in_one_step`.
- **(medium) `GetAsync` could report `failed`** when the trip updated zero rows but the true state was a different terminal state. Fix: the sweep re-reads and retries on a rowversion conflict, always returning the true state.
- **(medium) The system deadline key shared the caller's idempotency namespace.** Fix: system keys use a reserved `sys:` prefix that callers may not use (rejected). Regression: `Caller_may_not_use_a_reserved_system_key`.

## How to run / verify it
```bash
cd services/ledger && dotnet test Boys.Ledger.sln    # 144-case matrix + deadline/concurrency/idempotency
curl -s localhost:8080/internal/commitments/1/state   # current state (deadline gate applied on read)
curl -s localhost:8080/internal/commitments/1/events  # the audit trail
```

## Gotchas / follow-ups
- `isFinalLeg` is supplied by the caller (B14/B16 knows the milestone context). The machine is pure and trusts it; the calling layer computes it from milestone progress.
- The deadline sweep persists the trip lazily (on read/command), so there is no background scheduler — deterministic and demo-friendly.
- Next: B14 drives `submit_proof`/`clear`/`reject` from the brain proof-check + human referee; B15 drives `settle` with the real money math.
