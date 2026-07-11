# E0-B02 — Docker Compose: Both Databases Green on Apple Silicon

## What we built (plain English)
One `docker compose up` brings up both backing databases — SQL Server (ledger's OLTP store) and Oracle (brain's OLAP warehouse) — healthy on this Apple Silicon Mac, each proven to accept a real query. This was the single biggest risk of the whole project, so we retired it first.

## Key decisions
- **SQL Server: switched from `azure-sql-edge` to real `mssql/server:2022-latest` under `platform: linux/amd64` emulation.** This is a **change from `tech.md`.** azure-sql-edge is the ARM-native image, but it is effectively deprecated (last build Jan 2023) and **core-dumps on startup** on the current Docker Desktop VM — it copied the system databases, printed its version banner, then crashed (exit 1, core dump). RAM was not the issue (Docker had 7.7 GB). The reliable path on Apple Silicon today is emulating the real image via Rosetta/qemu. Boots in ~30–60s; slightly slower + heavier than native, but stable.
- **Oracle: `gvenzl/oracle-free:23-slim`** (ARM-native) — worked first try. First boot initializes the DB (~1–2 min); later boots ~20s. Ships `healthcheck.sh` and auto-creates an app user via `APP_USER`/`APP_USER_PASSWORD`.
- **Non-default host ports** — SQL Server `14333`, Oracle `15211` — so local DBs never collide.
- **Connectivity check uses pure-Python clients** (`python-tds` + `oracledb` thin) run ephemerally via `uv run --with`. No host-side ODBC/Instant Client needed, and azure-sql-edge shipped no `sqlcmd` anyway.

## How it works
- `docker-compose.yml` defines `mssql` + `oracle` with named volumes, healthchecks, and generous `start_period`s (Oracle's first init is slow).
- Healthchecks: Oracle uses the image's `healthcheck.sh`; SQL Server probes the TDS port with `bash /dev/tcp` (image-agnostic). The *real* readiness proof is `scripts/check_dbs.sh`, which runs `SELECT 1` against each.
- `.env` (gitignored) supplies passwords; `.env.example` documents them. SQL Server's SA password must meet complexity rules (upper+lower+digit+symbol, 8+).
- Oracle init scripts mount at `/container-entrypoint-initdb.d` (empty now; E1-B05 fills it). SQL Server has no equivalent auto-init in this image — its schema is applied by the .NET migration runner in E1-B06 (documented so no one expects `docker/mssql/init` to auto-run).

## How to run / verify it
```bash
cp .env.example .env
docker compose up -d          # first run pulls ~3GB (SQL Server amd64 layers included)
./scripts/check_dbs.sh        # → OK SQL Server ... / OK Oracle ...  / Both databases reachable.
```
Verified acceptance:
- `docker compose up -d` → both containers reach `healthy`.
- `check_dbs.sh` → live `SELECT 1` returns from both.
- **Volume persistence**: wrote a marker row into each DB, `docker compose down` (no `-v`), `up`, both healthy again in ~45s, marker (`id=42`) survived in both, then cleaned up.

## Gotchas / follow-ups
- **Emulation caveat**: SQL Server runs amd64 under Rosetta/qemu — fine for dev, but noticeably slower than native. Acceptable for the demo (single node, low load).
- If SQL Server ever exits 1 with a core dump again, it's the engine, not our config — check `docker logs boys-mssql | head` for the banner-then-crash signature.
- `docker compose down` **without** `-v` keeps data; `down -v` wipes the volumes (that's the intended "clean slate" for a fresh seed in E1/E5).
- `tech.md`'s Apple-Silicon note has been updated to record the azure-sql-edge → mssql-2022-emulated switch.
