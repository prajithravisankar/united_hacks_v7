# E3-B11 — ledger Service Skeleton (ASP.NET Core + Dapper + gRPC Clients)

## What we built (plain English)
The spine every financial flow hangs off. A running ASP.NET Core service that: boots with **typed config validated at startup** (a missing SQL connection string kills the process, never the first request); reports **liveness vs readiness** truthfully (`/health/ready` opens a real `SELECT 1`, so "SQL down → not ready" is provable); wraps every failure in **one JSON error envelope** (no stack traces, every response carries a request id); talks to **brain over gRPC** through a single resilient client that turns any transport failure into one typed `BrainUnavailableException`; and keeps the **Domain project pure** (an architecture test fails the build if anyone adds Dapper/gRPC/ASP.NET to it). Plus the Dockerfile and a compose entry that boots a healthy ledger gated on SQL Server.

## The four projects (and the one rule that matters)
| Project | Role | May reference I/O? |
|---|---|---|
| `Boys.Ledger.Domain` | entities, state machine, settlement — **pure logic** | **No** — enforced by `ArchitectureTests` |
| `Boys.Ledger.Api` | endpoints, Dapper repos, gRPC clients, middleware | Yes |
| `Boys.Ledger.Contracts` | generated C# gRPC stubs from `protos/` | Yes (protobuf runtime) |
| `Boys.Ledger.Migrations` | applies the `.sql` schema (from E1) | Yes |

The load-bearing rule: **Domain is pure.** That's what lets B15's settlement math be unit-tested to the cent with zero database. `ArchitectureTests.Domain_assembly_references_no_io_framework` reflects over the Domain assembly's referenced assemblies and goes red if `Dapper`, `Microsoft.Data.SqlClient`, `Grpc`, `Google.Protobuf`, `Microsoft.AspNetCore`, or `Microsoft.Extensions.Http` ever appears.

## Health: liveness vs readiness
- `GET /health/live` → always 200 while the process is up (compose uses it to know the container didn't crash). Independent of the database — proven by a test that boots against a dead connection string and still gets 200.
- `GET /health/ready` → 200 only when SQL Server answers `SELECT 1`; otherwise **503** (never a thrown 500). This is the truthful readiness compose gates dependents on.

## The error envelope (one shape, everywhere)
Every error — unknown route, domain rule violation, or an unexpected crash — returns:
```json
{ "error": { "code": "not_found", "message": "resource not found", "requestId": "141c3e4a…" } }
```
`ErrorEnvelopeMiddleware` is the single exception boundary: a `DomainException` maps its stable `code` to a status (`brain_unavailable`→503, `*_violation`/`illegal_transition`/validation→422, `not_found`→404, `conflict`→409, else 400); anything unexpected is logged **in full server-side** and returned as a generic 500 that leaks neither the internal message nor a stack trace. `RequestIdMiddleware` stamps every request (honouring an inbound `X-Request-Id`), echoes it on the response, and opens a logging scope — so a user-reported failure is one grep away.

## Brain gRPC client (resilience → one typed error)
`BrainClient` wraps the generated quant + referee clients. Each call gets a **deadline** off `IClock` (config `BrainTimeoutMs`, default 3s) so a hung brain can't hang the demo; the underlying HttpClient carries the **standard resilience handler** (retry + circuit breaker + attempt timeout). Any `RpcException` becomes a `BrainUnavailableException` — callers never see a raw gRPC error, so degraded-mode handling (E4/E5) lives in exactly one place. Proven both ways: a round-trip against a real in-memory stub server returns the valuation unchanged; an unreachable address throws the typed exception.

## Deviations & decisions (honest notes)
- **[DEVIATION] C# gRPC codegen is now committed, not build-time.** The plan had Grpc.Tools generate C# into `obj/` at build. Its bundled **arm64 `protoc` segfaults (exit 139) inside the Docker build** (both 2.82.0 and 2.71.0 — it's the Docker-VM/arm64 combo, not a version bug). Fix: pre-generate into `services/ledger/src/Boys.Ledger.Contracts/gen/` and commit it — exactly the pattern the repo already uses for Go (`services/engine/gen`) and Python (`services/brain/gen`). One `.proto` source of truth is preserved; `scripts/gen_protos.sh` now regenerates all three languages. Reliable host build **and** container build.
- **[NOTE] Tests split into unit + integration.** `Boys.Ledger.Tests` is pure/in-memory (architecture, boot, envelope, gRPC round-trip); `Boys.Ledger.IntegrationTests` holds the DB-touching tests (the moved migration suite + `/health/ready` against real SQL Server). The unit suite needs no database.
- **[NOTE] Self-contained compose.** A one-shot `ledger-migrate` service applies the schema (idempotent) and exits; `ledger` waits for it via `service_completed_successfully`, so the API's schema always exists. `MIGRATIONS_DIR` lets the migrator find the copied-in `.sql` inside the container (no repo tree there).

## How to run / verify it
```bash
cd services/ledger && dotnet test Boys.Ledger.sln          # 15 unit + 14 integration green
docker compose up -d ledger                                # migrate one-shot -> healthy ledger
curl -s localhost:8080/health/ready                        # 200 when SQL up
curl -s localhost:8080/nope                                # {"error":{"code":"not_found",...}}
```

## Gotchas / follow-ups
- The API reads its SQL connection from `Ledger:SqlConnectionString` (config/env); the Migrations tool builds its own from `MSSQL_*` env (the E1 `DbConfig`). Two mechanisms, one for each concern.
- `IDbConnectionFactory` is the sole place the connection string is read; repositories (B12+) resolve connections through it and never see config.
- Next: B12 puts the double-entry postings engine in Domain and its transactional persistence behind this connection factory.
