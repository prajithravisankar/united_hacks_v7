# E1-B05 — Oracle OLAP Star Schema + Seed Loader

## What we built (plain English)
The Oracle side of polyglot persistence — the "library" the quant model reads. A small **star schema** of historical football matches, plus an **idempotent Python loader** that parses the seeded CSVs and fills it. This is the analytical (OLAP) half of the story: heavy reads over history, never live cash.

## The schema (star)
```mermaid
erDiagram
    DIM_SEASON ||--o{ FACT_MATCH : has
    DIM_TEAM   ||--o{ FACT_MATCH : "home / away"
    DIM_SEASON { number season_id PK; varchar label UK }
    DIM_TEAM   { number team_id PK; varchar name UK }
    FACT_MATCH {
        number match_id PK
        number season_id FK
        number home_team_id FK
        number away_team_id FK
        date   match_date
        varchar result "H|D|A|null"
        number home_odds
        number draw_odds
        number away_odds
    }
```
Two dimensions (`dim_season`, `dim_team`) and one fact (`fact_match`) with a `CHECK (result IN 'H','D','A')`, a unique key on `(season, date, home, away)`, three FKs, and indexes on season + date for the analytical GROUP BYs.

## Key decisions
- **Only the tables we populate.** The plan listed `dim_market`, `fact_model_prob`, `fact_price_tick` too, but we have no data for them yet (NBA 538 probs are a different shape; Polymarket is best-effort). Per the R1 lesson (no dead scaffolding), those tables land in **E2/E5** when their data is wired — we don't create empty schema now.
- **Opening odds only** (`B365H/D/A`). Closing odds (the edge signal) get added with the parser in **E2-B08**; the columns are here, ready.
- **Idempotent by construction**: DDL creation swallows "already exists" (ORA-00955); dimensions `MERGE` on their natural key; facts `MERGE` on `(season, date, home, away)` with `UPDATE`-on-match. Re-running never duplicates (proven by test).
- **Connects as the `boys` app user** (not `system`), reading credentials from the repo `.env` without shell evaluation (the R1 fix for password-mangling).

## How it works
- DDL lives once in `docker/oracle/init/01_warehouse.sql` — it runs automatically on a **fresh** container, and the loader applies the *same* file idempotently against an already-initialized one.
- `app/data/seed_oracle.py`: `connect()` → `apply_ddl()` → parse 3 CSVs via B04's `load_odds` → upsert dims → `MERGE` facts (batched `executemany`) → return counts.

## How to run / verify it
```bash
cd services/brain && uv run python -m app.data.seed_oracle
#   dim_season: 3  dim_team: 25  fact_match: 1140
uv run pytest tests/data/test_seed_oracle.py    # 6 integration tests
```
Verified load: **3 seasons · 25 teams · 1140 matches** (3 × 380 = a full EPL season each). The base-rate query — home-win rate ≈ 0.45 — is the exact analytical shape the quant model will use.
The 6 tests cover: expected counts, referential integrity (0 orphans), no-nulls in required columns, the CHECK constraint rejecting a bad `result`, **idempotency** (load twice → identical counts), and a sane base-rate GROUP BY.

## Gotchas / follow-ups
- The Oracle tests **skip cleanly when Oracle is down** (the `oracle_conn` fixture), so a clean clone / CI without a database still passes the unit suite — they only run when the container is up.
- DDL is split on `;` after stripping full-line comments (an early bug skipped the first `CREATE` because the comment block rode with it).
- Next: E2-B08 extends `load_odds` to capture closing odds and adds the model-probability facts, so the quant edge (closing-line movement) can be backtested off this warehouse.
