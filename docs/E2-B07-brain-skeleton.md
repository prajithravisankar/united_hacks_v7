# E2-B07 — brain Service Skeleton (FastAPI + gRPC + Oracle)

## What we built (plain English)
The running spine of the brain service: a gRPC server (the real API that ledger/engine call) plus a small FastAPI app (health only), typed config, structured logging, and an Oracle connection pool. The quant and referee logic (B08–B10) hangs off this — the servicers are stubs for now.

## Key decisions
- **Two servers, one process.** gRPC (port 50061) is the real interface; FastAPI (port 8081) exists only for `/health/*` so Docker/compose has an HTTP probe. `serve()` starts the gRPC server (background threads) then runs uvicorn.
- **Stub servicers via inheritance.** `QuantServicer`/`RefereeServicer` subclass the generated base classes and override nothing — so every RPC already routes and returns `UNIMPLEMENTED` until B09/B10 fill them in (proven by a real gRPC test).
- **Config fails fast.** `Settings.require_prod_secrets()` raises if `ENV != local` and the Oracle password is missing — a misconfig dies at boot, not at 2am.
- **`/health/ready` tells the truth.** It runs `SELECT 1` against Oracle; Oracle down → `503`. That's what the compose healthcheck (and `depends_on`) key off.
- Oracle access is a lazy `oracledb` thin-mode pool (no host client needed).

## How it works
- `app/config.py` — pydantic-settings, reads the repo `.env` in dev, env vars in the container.
- `app/db.py` — `get_pool()` + `check_oracle()` (never raises).
- `app/logging_setup.py` — structlog JSON with a `request_id` contextvar.
- `app/grpc/server.py` — `create_server(port)` returns `(server, bound_port)` (port 0 = ephemeral, used by tests).
- `app/main.py` — FastAPI `/health/live` + `/health/ready`, and `serve()`.
- `services/brain/Dockerfile` + the `brain` compose service (`depends_on: oracle: service_healthy`, loopback-only ports, HTTP healthcheck).

## How to run / verify it
```bash
cd services/brain && uv run pytest tests/test_skeleton.py   # 6 tests
docker compose up -d brain                                   # healthy once Oracle is up
curl -s localhost:8081/health/ready                          # {"status":"ready","oracle":true}
```
The 6 tests: live=200; ready=503 when Oracle down and 200 when up (monkeypatched, no DB needed); config fail-fast; config local-ok; and a **real gRPC round-trip** proving the server routes (returns `UNIMPLEMENTED`).

## Gotchas / follow-ups
- Inside compose the brain reaches Oracle at `oracle:1521` (service DNS), not the host's `127.0.0.1:15211`.
- gRPC/structlog are untyped for mypy → narrow `ignore_missing_imports` overrides; the stub servicers carry a `# type: ignore[misc]` (subclassing a dynamically-typed base).
- Next: B08 fills `app/quant/`, B09 implements `QuantServicer`, B10 implements `RefereeServicer`.
