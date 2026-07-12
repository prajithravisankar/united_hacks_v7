# E5-B21 — nginx Edge + Full-Stack Compose + Demo Scenario Seed

## What we built (plain English)
The whole system now comes up with **one command** and is demo-ready: `./scripts/demo_up.sh` boots the full
stack behind an **nginx edge** (single origin — no CORS), builds the deterministic NAV curve, and seeds the
canonical History-class goal ($100, 3 milestones), activated with escrow posted. `scripts/check_stack.sh`
proves it end-to-end. Cold boot from `docker compose down -v` → green in **~45s**, repeatably.

## Key decisions
- **nginx is the single origin** (`127.0.0.1:8888`): `/api` → ledger, `/ws/live` → engine, `/api/health` →
  ledger readiness. Because the board (later) is served from the same origin, the browser needs **no CORS** —
  a real architectural win, documented in `docker/nginx/nginx.conf`.
- **Compose profiles**: the app services (brain, data-seed, ledger, engine, nginx) are behind
  `profiles: ["demo"]`; the two databases have **no profile**. So a bare `docker compose up` is the *dev*
  experience (DBs only, for running the test suites), and `docker compose --profile demo up` is the full
  *demo* stack. This keeps existing dev workflows working while adding the one-command demo.
- **The NAV curve is data, not runtime** — brain serves a precomputed curve from Oracle. A new one-shot
  **`data-seed`** service (reusing the brain image) loads the warehouse (`fact_match`) and builds
  `fact_nav_curve` **before brain starts**, so brain caches the full curve and the Go engine fetches it at
  boot. This is why the boot order is DBs → `data-seed` → brain → engine → nginx.
- **The demo goal is created through the real API**, not injected — it exercises the AI gate (seeded mode →
  deterministic ACCEPT because the goal text contains "90%") and the real escrow posting on activate. The
  seed **resets the ledger's transactional tables first** so the demo goal is always `commitment_id = 1`
  (what the engine replays), making the seed a repeatable reset.

## How it works
```
                         ┌─────────── nginx :8888 (single origin) ───────────┐
  browser / E2E ───────► │  /api → ledger:8080   /ws/live → engine:8090       │
                         └───────────────────────────────────────────────────┘
  boot order (health-gated):
    mssql ─► ledger-migrate ─► ledger ─┐
    oracle ─► data-seed ─► brain ─► engine ─┴─► nginx
             (loads fact_match + builds fact_nav_curve, then exits)
```
- `docker/nginx/nginx.conf` — routes, the WebSocket upgrade `map`, gzip, quiet access log.
- `docker-compose.yml` — `data-seed` + `nginx` services, `demo` profile, `brain`/`engine` gated on
  `data-seed: service_completed_successfully`.
- `scripts/seed_demo.sh` — resets ledger tables (append-only triggers disabled for the reset; identities
  reseeded only where a row has existed, so a fresh table still starts at 1), creates + activates the demo
  goal via `/api`, stages the milestone proof fixtures in `data/seed/`.
- `scripts/check_stack.sh` — every container healthy, `/api/health` (200) + WS handshake (101) through nginx,
  demo commitment present with a live state.
- `scripts/demo_up.sh` — the one-command wrapper: compose up `--wait` → seed → check.
- `services/brain/app/data/seed_oracle.py` — now honors `BOYS_RAW_DIR` / `BOYS_DDL_FILE` env overrides so the
  loader runs inside the `data-seed` container (data + DDL mounted in).

## How to run / verify it
```bash
./scripts/demo_up.sh                       # from nothing to demo-ready + verified
docker compose --profile demo down -v && ./scripts/demo_up.sh   # cold boot from zero
./scripts/seed_demo.sh                     # reset to pristine between takes
```
"Working" = `check_stack.sh` prints `== STACK OK ==`: 6 healthy containers, 2 completed one-shots, REST + WS
through nginx, demo commitment 1 `active`. Verified: **3 consecutive cold runs green, 46s / 44s / 41s.**

## Gotchas / follow-ups
- **`check_stack.sh` was created, not "extended"** — no `check_stack.sh` existed (the todo assumed one). It's
  new. [DEVIATION vs the todo wording, same outcome.]
- **nginx healthcheck uses `127.0.0.1`, not `localhost`** — busybox `wget` resolves `localhost` to IPv6 `::1`,
  but nginx listens on IPv4; `localhost` made the container read healthy-from-host but unhealthy-in-container.
- **Fresh-table identity quirk**: `DBCC CHECKIDENT(t, RESEED, 0)` on a *never-used* table makes the first row
  `id = 0` (SQL Server uses the reseed value directly on fresh tables). The reset reseeds only tables whose
  `sys.identity_columns.last_value IS NOT NULL`; fresh tables keep their natural seed of 1. `ledger_transactions`
  has a GUID PK (no identity) — not reseeded.
- **Milestone proof fixtures are functional, not photographic** — in seeded AI mode the referee only checks the
  evidence *bytes* (`cropped` ⇒ reject). Swap in real screenshots for the video; see `data/seed/README.md`.
- The `succeeded`/`Success`-settlement path is **not reachable over HTTP** yet (no `complete` endpoint) — B22
  owns that integration-glue fix for the golden path.
- Cold boot is fast here because the Oracle image ships a pre-initialized database; a truly first-ever pull is
  slower (image download + Oracle init `start_period` up to 90s).
