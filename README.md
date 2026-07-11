# BOYS — Bet On Your Self

A commitment-device platform where you stake money on a personal goal, and while you chase it your stake rides a simulated sports-outcomes fund. Prove progress at verified milestones to unlock cash-out (or let it ride). **Backend is a polyglot microservices system** — the point of the build.

> Sports-theme hackathon project + polyglot-microservices job showcase. This repo is the **backend**; the frontend (`board`) is a later track built against the published REST + WebSocket contracts.

## Architecture

```
Browser (board — later)
  ├── REST ─────────────► ledger (.NET)  ──owns──► SQL Server (OLTP: escrow, ledger, state machine)
  │                          ├── gRPC ──► brain (Python) ──owns──► Oracle (OLAP: odds, results, probs)
  │                          └── gRPC ──► engine (Go)
  └── WebSocket ◄── live NAV ticks ── engine (Go)
```

| Service | Tech | Job |
|---|---|---|
| `services/ledger` | .NET 9 + Dapper + SQL Server | Financial core: escrow, double-entry ledger, commitment state machine, settlement. |
| `services/brain` | Python 3.12 + FastAPI + Oracle | Quant fund (backtested NAV curve) + AI referee (goal/proof checks). |
| `services/engine` | Go 1.25 | Deterministic 30× replay + WebSocket streaming. |
| `protos/` | Protocol Buffers | The single gRPC contract shared by all three languages. |

## Quick start (dev)

Prereqs: Docker Desktop (≥ 8GB RAM), .NET 9 SDK, Go 1.25+, `uv` (Python), `protoc`.

```bash
# run every service's test suite + linters (the gate)
./scripts/verify.sh

# bring up the databases (Epic E0/B02)
cp .env.example .env          # fill in / keep dev defaults
docker compose up -d          # first run pulls ~3GB of images
./scripts/check_dbs.sh        # proves both accept a real query
```

### Databases (Apple Silicon notes)
- **SQL Server** = `mcr.microsoft.com/azure-sql-edge` (ARM-native). The normal `mssql/server` image is amd64-only and slow under emulation. sql-edge ships **no `sqlcmd`**, so the healthcheck probes the TDS port and `check_dbs.sh` uses a pure-Python client.
- **Oracle** = `gvenzl/oracle-free:23-slim` (ARM-native). First boot initializes the database and takes **~1–2 minutes** (healthcheck `start_period` accounts for it); later boots are fast.
- Give **Docker Desktop ≥ 8GB RAM** — two databases plus (later) three service runtimes.
- Host ports are non-default: SQL Server `14333`, Oracle `15211` (so local DBs don't collide).

Per-service test commands:

```bash
dotnet test services/ledger/Boys.Ledger.sln          # ledger (.NET)
cd services/brain && uv run pytest                    # brain (Python)
cd services/engine && go test ./...                   # engine (Go)
```

## Repo layout

```
services/ledger   .NET solution (Domain / Api / Tests)
services/brain    Python package (app / tests)
services/engine   Go module (cmd / internal)
protos/           shared gRPC .proto contracts
data/             seed data (raw + processed; payloads gitignored, checksums committed)
docker/           per-image DB init scripts
scripts/          verify.sh, lint.sh, and ops helpers
docs/             per-assignment docs (E{epic}-B{##}-<slug>.md) + reviews
```

Build order and full assignment tracker live in `backend-todo.md` (local only).
