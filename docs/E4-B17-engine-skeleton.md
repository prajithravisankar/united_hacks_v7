# E4-B17 — engine Service Skeleton (Go + gRPC Client + Config)

## What we built (plain English)
The rails for the showman. A running Go service that: loads and **validates config** from the environment (missing brain address → fail fast at boot); logs **structured JSON** (slog); dials brain and **fetches the demo NAV curve at startup** (the timeline the replay will walk); serves **health probes**; hosts the **EngineService gRPC** server (stub now, ticker-backed in B18); and **shuts down gracefully** on SIGTERM with **zero leaked goroutines**. Go's concurrency-correctness tooling — `-race` and `goleak` — is wired in from the very first test.

## Layout
```
cmd/engine/main.go        wiring: config -> logger -> brain fetch -> HTTP + gRPC servers -> graceful shutdown
internal/config           env parse + validation (fail fast)
internal/brainclient      brain QuantService client; FetchNavCurve (the B20 degradation-cache seam)
internal/httpapi          health router (/health/live, /health/ready); WebSocket lives here in B19
internal/enginesvc        EngineService gRPC (stub; B18 backs it with the ticker)
internal/replay, internal/hub   (arrive in B18 / B19)
```

## Concurrency correctness from day one
- **`goleak` in every package's `TestMain`** — every test asserts it leaks no goroutines. The fake brain gRPC server and the HTTP server in tests are stopped on cleanup, so a leak fails the suite.
- **`go test -race ./...` is green.**
- **Graceful shutdown, tested under load:** `TestServerShutsDownUnderLoadWithNoLeaks` fires 25 concurrent requests at a live server, then `Shutdown`s it — goleak confirms nothing leaks. `main` drains the HTTP server (`Shutdown`) and the gRPC server (`GracefulStop`) on SIGTERM/SIGINT.

## Config (fail fast)
`BRAIN_GRPC_ADDRESS` is **required** (missing → error at boot). `BRAIN_TIMEOUT_MS` must be 100–60000. Everything else has a default: `ENGINE_HTTP_ADDR=:8090`, `ENGINE_GRPC_ADDR=:50071`, `DEMO_COMMITMENT_ID=1`, `LOG_LEVEL=info`. Tested: defaults applied, missing brain address rejected, bad/non-numeric timeout rejected.

## Startup curve fetch
On boot the engine calls brain's `GetNavCurve(demo commitment, principal=$100, fund window 2021-08-13..2024-05-19)` and logs the point count. A brain outage here is **non-fatal** — the engine logs a warning and runs degraded (readiness stays false); B20 makes the cached curve authoritative and adds recovery. Readiness (`/health/ready`) reflects "curve loaded," so compose only marks the engine healthy once it has its timeline.

## How to run / verify it
```bash
cd services/engine && go test -race ./...      # all green, goleak clean
docker compose up -d engine                    # boots healthy, gated on brain
docker compose logs engine                     # -> {"msg":"fetched demo curve","commitment":"1","points":360}
curl -s localhost:8090/health/ready            # {"status":"ready"}
```
Verified live: the engine container fetches the **360-point** curve from brain over the compose network at startup and reports healthy.

## Gotchas / follow-ups
- The runtime image is `alpine` (not distroless) so the compose healthcheck can `wget` `/health/ready`; the binary is static (`CGO_ENABLED=0`), ARM-native.
- `brainclient.Dial` is lazy (gRPC `NewClient`) — construction never blocks; the first RPC surfaces an unavailable brain as an error the caller degrades on.
- Next: B18 puts the deterministic replay ticker behind EngineService; B19 adds the WebSocket hub in `internal/httpapi`/`internal/hub`.
