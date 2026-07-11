# E3-B14 — Milestone Verification Workflow (AI First-Pass + Human Referee)

## What we built (plain English)
The trust mechanic. Submit evidence for a milestone → the ledger calls brain's `CheckProof` (gRPC) for an **AI first pass** → a **human referee** decides with **final authority** → the milestone clears or the ticket fails. AI recommends; the human decides — exactly the locked hybrid. When brain is unreachable the submission is **still accepted** (verdict `PendingAi`, degraded) so the referee can decide manually — the whole product never blocks on the AI. This is the seam between two services, so it carries the graceful-degradation and human-authority guarantees, and it's proven with one real cross-service round-trip.

## The loop
```
  submit proof ──▶ validate size+mime ──▶ store evidence (URI) ──▶ commitment: submit_proof
                                                                        │  (active/riding → pending_verification)
                                                                        ▼
                                              brain CheckProof (gRPC)  ──▶  AI verdict recorded
                                              (brain down → PendingAi, degraded=true)
                                                                        ▼
  referee decides ──▶ approve → clear_milestone (→ milestone_cleared, cash-out unlocked)
     (referee role   └▶ reject  → reject_milestone (→ failed, HARD GATE)
      required)
```

## What's guaranteed (every one is a named test)
- **Happy path:** AI supports → referee approves → `milestone_cleared`, cash-out unlocked.
- **AI insufficient → stays pending, reason surfaced;** resubmission is allowed, re-runs the AI, and the **attempt count advances** (a resubmission does *not* re-transition the already-pending commitment).
- **Human authority is absolute — both directions:** AI-yes / referee-no → `failed` (hard gate); AI-no / referee-yes → `cleared`.
- **Referee decision is idempotent** (double-click safe — the second click is a no-op returning the same state), and **only a referee** may decide (a learner → `403 forbidden`).
- **Brain unreachable → submission accepted, verdict `PendingAi` (degraded), referee can still decide manually** — proven with a failing fake brain.
- **Evidence validation at the boundary:** oversized (> 5 MB) → `422 oversized_evidence`; unsupported MIME → `422 unsupported_mime`; both rejected **before** anything is stored or transitioned. The evidence **bytes are never logged** — only the stored URI is.

## The real cross-service proof (the polyglot payoff)
Two tests exercise the *real* brain, not a fake:
1. **In-process:** a real `BrainClient` → the brain container (seeded-AI) returns a non-degraded verdict — the `CheckProof` proto contract works end-to-end.
2. **Container-to-container (the demo path):** `POST /internal/.../proof` on the ledger container calls the brain container over the compose network (`brain:50061`) and returns `{"ai":{"status":"Supported","degraded":false,...}}`, then `POST /.../decision` (approve) → `milestone_cleared`. The event trail records `active → pending_verification (submit_proof) → milestone_cleared (clear_milestone)`.

## How to run / verify it
```bash
cd services/ledger && dotnet test Boys.Ledger.sln    # 9 workflow tests, incl. the real-brain round-trip
# live loop through both containers (commitment must be 'active'):
curl -X POST localhost:8080/internal/commitments/{cid}/milestones/{mid}/proof \
  -d '{"claim":"Scored 90%","evidenceBase64":"...","mime":"image/png","idempotencyKey":"k1"}'
curl -X POST localhost:8080/internal/milestones/{mid}/decision \
  -d '{"decision":"approve","refereeUserId":2,"idempotencyKey":"k2"}'
```

## Gotchas / follow-ups
- **Source of truth vs. denormalization:** the commitment state (the event-sourced B13 machine) is authoritative; `milestones.state` and `verifications.referee_decision` are best-effort denormalized views updated right after the transition. They are not written in one transaction with the commitment move — a v0 boundary (a crash between the two could leave `milestones.state` stale while the commitment is correct). Acceptable for the demo; a real system would fold them into the transition.
- Resubmission count is derived (`COUNT(verifications)`), and the degraded flag lives in the stored AI-verdict JSON — no schema change needed.
- The internal endpoints here are formalized as the public REST API in B16 (with the standard envelope and demo-user/referee header auth).
- Next: B15 settles `cashed_out`/`succeeded`/`failed` with the real money math through the B12 ledger.
