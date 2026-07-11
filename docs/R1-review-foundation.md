# R1 — Foundation Review (Epic 0)

*A plain-English map of everything Epic 0 built, plus what an adversarial review found and how it was resolved. Written to be read cold — if you're new to the repo, start here.*

---

## 1. What Epic 0 built (the foundation)

BOYS is a polyglot microservices backend: three services in three languages, two databases, one shared contract. Epic 0 laid the rails; no product features yet.

**B01 — Monorepo & test harnesses.** One git repo, three services:
- `services/ledger` — **.NET 9 + Dapper** — the financial core (escrow, double-entry ledger, state machine, settlement). Owns SQL Server.
- `services/brain` — **Python 3.12 + FastAPI** — the quant fund + AI referee. Owns Oracle.
- `services/engine` — **Go 1.25** — the deterministic 30× replay + WebSocket NAV streamer.
Each has a red→green test harness. `scripts/verify.sh` runs all three suites (the gate); `scripts/lint.sh` runs the formatters/linters.

**B02 — Databases on Apple Silicon.** `docker compose up` brings up both DBs healthy on an M-series Mac: **SQL Server** (real `mssql/server:2022` under amd64 emulation — azure-sql-edge core-dumps) and **Oracle** (`gvenzl/oracle-free:23-slim`, ARM-native). Non-default host ports (SQL `14333`, Oracle `15211`, now loopback-only). `scripts/check_dbs.sh` proves each answers a live `SELECT 1` via pure-Python clients. This was the project's biggest risk — retired first.

**B03 — gRPC contracts, one source, three languages.** Every inter-service call is defined once in `protos/` and compiled to typed stubs for all three languages via `scripts/gen_protos.sh`. Three services / 11 RPCs: `QuantService` + `RefereeService` (brain), `EngineService` (engine). **One money type everywhere** — `common.v1.Money { int64 cents; string currency }` — no float ever touches a balance. Go + Python stubs are committed (fresh clone builds without protoc); C# regenerates at build. `protos/CHANGELOG.md` locks the wire rule (add fields, never renumber). A smoke test per language proves the generated code compiles.

**B04 — Offline-first seed pipeline.** `scripts/fetch_data.sh` downloads real sports data once into `data/raw/` (3 EPL seasons of football-data.co.uk results+odds + FiveThirtyEight NBA Elo), validates by parsing, and checksum-verifies the static files. After that, nothing on the critical path needs the network. `app/data/parsers.py` has three strict, tested parsers.

---

## 2. Clean-clone rebuild (the headline check)

A fresh `git clone` from GitHub → `./scripts/verify.sh` passes **green** with **no protoc and no docker needed** (generated code is committed):
- 81 tracked files, **0** build/cache/data artifacts.
- ledger 2 tests · brain 11 · engine 2 packages → **ALL GREEN**.
The docker DB boot is proven separately in B02 (a `down`/`up` cycle with a surviving marker row). A second live stack isn't spun up in review because it collides on container names/ports with the running one.

---

## 3. What the review found & did

An adversarial audit (5 independent read-only lenses + synthesis) ran over the whole foundation. Verdict: **strong and honest — zero secret leakage, clean hygiene, full contract parity — nothing blocking.** But it found real "cheap-now" debt, all **fixed during R1**:

| # | Issue found | Fix applied |
|---|---|---|
| 1 | **Identity fragmentation** — engine used `goal_id`, quant `commitment_id`, referee `milestone_id`, unlinked (yet one goal = one commitment) | Renamed engine `goal_id → commitment_id`; documented `milestone_id ↔ commitment_id`. Cheap now, expensive after service code lands. |
| 2 | **"You drive" mode had no home** on the quant read path | Added `DriveMode` enum + `drive_mode` to the three quant read requests. |
| 3 | **Deadline hard-gate had no clock** — `ReplayState` exposed only an integer position | Added `current_sim_date` (ISO) to `ReplayState`. |
| 4 | **BOM test was a no-op** — the BOM sat on a column the parser never reads | Moved the BOM onto the `Date` column; removing `utf-8-sig` now fails the test. |
| 5 | **probs range test never exercised the filter** | Added an out-of-range prob row to the fixture; without the filter the test now fails. |
| 6 | **Python smoke test compiled only 1 of 3 service stubs** | Now imports + touches all three (`Quant`/`Referee`/`Engine`) stubs. |
| 7 | **`fetch_data.sh` skip-guard poisoned reruns** on a partial download | Download to `.part`, `mv` into place only on success. |
| 8 | **DB ports bound to `0.0.0.0`** with committed default creds | Bound to `127.0.0.1`; noted local-only in `.env.example`. |

Plus cleanups: removed 3 dead `.gitkeep` placeholders; made the Polymarket fetch idempotent (skip-if-present); `protos/CHANGELOG.md` records all the contract edits.

**Deferred (with a home), not dropped:** deeper parser error-branch coverage → E1-B05's loader tests; `Valuation` failure/bonus semantics + `PlaceUserBet` idempotency key → E2-B09; a canonical shared home for the 15% carry rate → E2-B09/E3-B15; gen_protos toolchain-hardening → later dev-tooling. Each is written into this doc so nothing lives only in a reviewer's head.

---

## 4. How to verify it yourself

```bash
./scripts/verify.sh        # all three suites → ALL GREEN
./scripts/lint.sh          # → LINT OK
docker compose up -d && ./scripts/check_dbs.sh   # both DBs answer SELECT 1
./scripts/gen_protos.sh    # regenerate all stubs, 0 errors
./scripts/fetch_data.sh    # idempotent seed + checksum verify
```

## 5. Health verdict
Epic 0 is a green, network-free, three-language foundation with a single money-safe contract and both databases proven — and now the identity/mode/clock gaps in the contract are closed *before* any service code depends on them. **Ready for E1 (persistence).**
