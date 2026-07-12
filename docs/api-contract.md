# BOYS Ledger — Public API Contract (v0)

The REST edge the frontend consumes. Base path `/api`. JSON in, JSON out. The machine-readable spec is `services/ledger/openapi.json` (served live at `/openapi/v1.json`); a drift test keeps them in sync.

## Identity (v0: no auth — locked decision)
A seeded demo world with one learner and one referee. The acting user is selected by an **`X-User-Id`** header; with no header, the demo learner is assumed. Referee actions require that the header identify a user whose role is `referee` — otherwise **403**.

## The standard error envelope
Every error is:
```json
{ "error": { "code": "not_found", "message": "…", "requestId": "…" } }
```
No endpoint returns a raw exception or stack trace. Codes → status: `not_found`→404, `forbidden`→403, `validation`/`illegal_transition`/`escrow_violation`/`oversized_evidence`/`unsupported_mime`→422, `conflict`→409, `brain_unavailable`→503, `bad_request`→400. Goal rejection is a 422 whose body also carries `suggestedRewrite`.

## Endpoints
| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/goals` | Create a goal through the AI SMART-goal gate. Valid → **201** `{commitmentId, aiVerdict, degraded}`. AI wants a revision → **422** `{error, suggestedRewrite}`. Stake/milestones/deadline outside limits → **422** naming the rule. |
| `POST` | `/api/goals/{id}/activate` | Escrow the stake and move to `active`. Idempotent. |
| `GET` | `/api/goals/{id}` | Current `state`, `deadline`, the `milestones` (`{milestoneId, ordinal, description, targetMetric, dueDate, state}`), and the event `timeline`. Unknown → 404. |
| `POST` | `/api/goals/{id}/proof` | Submit evidence for a milestone → AI first pass. Oversized/unsupported → 422. |
| `POST` | `/api/milestones/{id}/decision` | **Referee only.** `{decision:"approve"\|"reject"}` — approve clears the milestone, reject fails the commitment. Idempotent. |
| `POST` | `/api/goals/{id}/cashout` | Bow out at a cleared milestone → `cashed_out`. |
| `POST` | `/api/goals/{id}/ride` | Compound to the next leg → `riding`. Per-leg idempotent. |
| `POST` | `/api/goals/{id}/succeed` | Clear the **final** leg → `succeeded` (the winning terminal; settlement then adds the winners-pool bonus). |
| `POST` | `/api/goals/{id}/settle` | Settle → the **receipt** `{type, principalCents, navCents, gainCents, carryCents, charityCents, bonusCents, takeHomeCents}`. Exactly-once. |
| `GET` | `/api/goals/{id}/receipt` | The settlement receipt (404 if not settled). |
| `GET` | `/api/goals/{id}/valuation` | Proxy to brain's valuation. **Degraded contract:** if brain is down this returns **200** `{degraded:true, …}`, never a 500. |
| `GET` | `/api/pool` | Community-pool backdrop `{committedPeople, poolCents}`. |
| `GET` | `/api/charities` | The vetted charity list. |

## The degraded-mode contract (what the frontend relies on)
- **Valuation** (`GET /api/goals/{id}/valuation`): brain down → `200 {degraded:true}`. The UI shows "live value unavailable," never an error page.
- **Goal creation** (`POST /api/goals`): brain down → the goal is still created with `degraded:true` (the AI gate is advisory; a human referee reviews later).
- **Proof** (`POST /api/goals/{id}/proof`): brain down → the submission is accepted with `ai.status:"PendingAi"`, `degraded:true`; the referee can still decide.
- **Settlement** needs the NAV, so a brain outage means "settle later" (503), never a wrong settlement.

## The golden path (what the demo walks)
`POST /api/goals` → `POST /activate` → `POST /proof` → (referee) `POST /milestones/{id}/decision approve` → `GET /valuation` → `POST /cashout` → `POST /settle` → the receipt, correct to the cent (`$100` stake, NAV `$155` → take-home `$146.75`, carry `$8.25`).
