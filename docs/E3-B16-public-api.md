# E3-B16 — Public REST API + Community Pool + OpenAPI Contract

## What we built (plain English)
The edge the frontend will consume, and where the demo user's seeded world becomes reachable over HTTP. Goal creation through the AI gate, activation/escrow, proof submission, the referee console, cash-out/ride/settle, a valuation proxy, the community-pool stats, and the vetted charities — all as **thin adapters** over the domain services built in B11–B15 (no business logic in the handlers). Plus a published, drift-checked **OpenAPI** document and a written API contract for the frontend track.

## Thin adapters over the domain
Every handler resolves a domain service and delegates: `POST /api/goals` → `GoalService`, `/proof` and `/decision` → `VerificationService`, `/settle` → `SettlementService`, `/cashout` and `/ride` → the `ICommitmentRepository` transitions, `/valuation` → the brain client. The only logic in the edge is HTTP translation (status codes, the envelope, header identity). The money and lifecycle rules stay in the audited domain.

## What's guaranteed (every one is a named test — 14 in total)
- **Goal creation through the AI gate:** valid → **201** with the AI verdict; **AI-rejected → 422 with the `suggestedRewrite`**; stake (`$20–$500`), milestone count (`1–5`), and deadline (`1 week–6 months`) violations → **422 naming the exact rule**.
- **Activation escrows exactly once** (idempotent — activating twice leaves escrow at the stake, not double).
- **The full golden path over HTTP** — create → activate → proof → referee approve → valuation → cash-out → settle → **receipt correct to the cent** (`takeHomeCents = 14675`, `carryCents = 825`).
- **Referee authority at the edge:** a learner spoofing the referee header on `/decision` → **403**.
- **Community pool + charities** return the seeded world (`1204` people / `$47,300`; 4 charities).
- **No endpoint ever returns a raw exception:** unknown id → 404 envelope; a malformed JSON body → **400** envelope (mapped from `BadHttpRequestException`), never a 500 with a stack trace.
- **Degraded-brain valuation:** brain down → **200 `{degraded:true}`**, never a 500.

## OpenAPI (published + drift-checked)
`AddOpenApi()` / `MapOpenApi()` serve the spec at `/openapi/v1.json`; it's committed at `services/ledger/openapi.json`. A **drift test** boots the app, fetches the live spec, and asserts its path set equals the committed file's — so adding an endpoint without regenerating fails CI. `docs/api-contract.md` documents the envelope, identity, endpoints, and the degraded-mode semantics for the frontend.

## Identity (v0 tradeoff, documented)
No auth (a locked decision). A seeded demo learner + referee, selected by an `X-User-Id` header; no header defaults to the demo learner. Referee endpoints check the header user's role and 403 otherwise. This is a v0 shortcut — a real deployment puts real auth in front of the same handlers.

## Verified live
Rebuilt the container and drove the edge over HTTP: `POST /api/goals` → **201** `{aiVerdict:"Accept", degraded:false}` (the goal gate hit the **real brain** over the compose network), `POST /activate` → `active`, `GET /api/pool` → the seeded stats.

## Gotchas / follow-ups
- The B14/B15 internal endpoints remain (diagnostic); the `/api` surface is the public one. Both are in the OpenAPI doc.
- Regenerate `openapi.json` after changing endpoints: `curl localhost:8080/openapi/v1.json | python3 -m json.tool > services/ledger/openapi.json` (the drift test enforces it).
- This completes Epic 3 (the financial core). Next epics (E4 engine / E5) build on this edge; R4 reviews it.
