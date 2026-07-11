# R2 — Data Platform Review (Epic 1)

*The polyglot-persistence audit: does each database own a disjoint slice, and does the whole thing rebuild from zero? Written to be read cold.*

---

## 1. Headline result

**The two schemas are literally disjoint — the polyglot no-duplication rule holds.** A grep across both confirms: no `user`/`money`/`cents`/`commitment` in Oracle, and no `team`/`odds`/`match` in SQL Server. The one *intentional* cross-DB value (NAV) is a CQRS read-model copy at a different grain, documented below. Money is `BIGINT`/`int64` cents everywhere (zero float touches a balance); server timestamps are UTC (`SYSUTCDATETIME`). Both loaders are transactionally correct and idempotent.

## 2. Which database owns what, and why (no overlaps)

| Table | DB | OLTP/OLAP | Why it lives there |
|---|---|---|---|
| `dim_season`, `dim_team` | Oracle | OLAP | Star dimensions — generic, read-only, no money/user. |
| `fact_match` | Oracle | OLAP | 1140 finished matches (results + opening odds) — the sole input to base-rate/backtest analytics. No cash, no identity. |
| *(planned)* `fact_model_prob`, `fact_price_tick`, `fact_nav_curve` | Oracle | OLAP | E2/E5 — not created yet (no dead schema). `fact_nav_curve` stays **commitment-agnostic** (the analytical *source* of NAV). |
| `users` | SQL Server | OLTP | Account identity; sole owner of `user_id`. |
| `charities` | SQL Server | OLTP | Payout targets. |
| `commitments` | SQL Server | OLTP | The staked goal; sole owner of `commitment_id` + `stake_cents`. |
| `milestones` | SQL Server | OLTP | Per-commitment checkpoints (ordinal 1–5). |
| `verifications` | SQL Server | OLTP | Proof + referee decision; the *only* persistence of brain's `ai_verdict` (brain is stateless — no duplication). |
| `ledger_accounts`, `ledger_transactions`, `ledger_postings` | SQL Server | OLTP | Double-entry: chart of accounts + idempotent headers + append-only balanced lines. `SUM(postings)` is the authoritative balance. |
| `community_pool_stats` | SQL Server | OLTP | Single-row seeded **display** backdrop — deliberately *not* derived from postings. |
| `nav_snapshots` | SQL Server | OLTP | Per-(commitment, day) **cash-truth** NAV — see the boundary below. |

## 3. The one intentional cross-DB value: NAV

This is the exact spot where a legitimate copy could silently rot into duplication, so the ruling is explicit:
- **Brain (Oracle) is the sole *producer* of the NAV number** — a deterministic fund curve, commitment-agnostic, keyed by date/market.
- **Ledger (SQL Server) `nav_snapshots` is the sole *auditable record* of that number against real money** — a materialized copy at a *different grain* (one row per commitment per day) and a *different role* (cash truth for `GetValuation`/settlement).
- Rule for E2: `fact_nav_curve` must **never** be keyed on `commitment_id` — that would pull the ledger's transactional identity into the warehouse and manufacture real duplication. Commitment identity + the per-commitment snapshot stay in SQL Server.

## 4. Destroy-and-rebuild (the "reset between demo takes" guarantee)

One command sequence rebuilds *both* databases from nothing and passes every test:
```bash
docker compose down -v && docker compose up -d      # wipe + fresh boot (~40s to healthy)
cd services/brain  && uv run python -m app.data.seed_oracle          # 3 seasons · 25 teams · 1140 matches
cd services/ledger && dotnet run --project src/Boys.Ledger.Migrations  # migrations applied: 3
./scripts/verify.sh                                  # ledger 13 · brain 18 · engine 2 → ALL GREEN
```
Proven twice (before and after the R2 hardening). On fresh boot, the Oracle init script creates the warehouse schema *and* the idempotent loader coexists with it — no conflict.

## 5. Findings & resolutions

**Fixed in this review:**
| Finding | Fix |
|---|---|
| Unindexed access paths (SQL Server doesn't auto-index FKs) | New migration `003`: indexes on `ledger_postings(account)`, `ledger_transactions(commitment_id)`, `commitments(user_id)`, `verifications(milestone_id)`. |
| Missing by-team index (the speced base-rate path) | Oracle `ix_fm_home` / `ix_fm_away`. |
| `fact_match` allowed a team vs itself | `CHECK (home_team_id <> away_team_id)` + a test. |
| NAV nav/pool could go negative | `CHECK (nav_cents >= 0)`, `CHECK (pool_cents >= 0 AND committed_people >= 0)` + a test. |
| The NAV cross-DB boundary was undocumented | §3 above (the required cross-check, done). |

**Deferred (with a home):** DB-level zero-sum enforcement + header/postings atomicity + idempotency-from-the-balanced-group → **E3-B12**; UTC-at-the-API-boundary for caller-supplied `DATETIME2` (deadline/due_date) + an edge test → **E3**; keep `fact_nav_curve` commitment-agnostic + a provenance column on `nav_snapshots` → **E2-B08/B09**; Oracle `batcherrors` diagnostics + odds `NUMBER(8,3)`/`CHECK(>1.0)` → **E2 odds boundary**.

## 6. Poke each DB by hand

Ports/creds from `.env` (loopback only): SQL Server `127.0.0.1,14333` (sa), Oracle `127.0.0.1:15211/FREEPDB1` (boys).

**Oracle — the base-rate GROUP BY (the OLAP shape):**
```sql
SELECT s.label, ROUND(AVG(CASE WHEN f.result='H' THEN 1 ELSE 0 END), 3) AS home_win_rate
FROM fact_match f JOIN dim_season s ON s.season_id = f.season_id
GROUP BY s.label ORDER BY s.label;
```
Run it: `docker exec -it boys-oracle sqlplus boys/Boys_Dev_Passw0rd@localhost/FREEPDB1`

**SQL Server — the OLTP truth (balance = sum of postings):**
```sql
SELECT account, SUM(delta_cents) AS balance_cents FROM ledger_postings GROUP BY account;
SELECT * FROM community_pool_stats;   -- 1204 people · 4,730,000 cents
```
(Connect with any SQL client to `127.0.0.1,14333`, db `boys`, or the pure-python `pytds` used in `scripts/check_dbs.py`.)

## 7. Verdict
Ship E1. Both databases are sound, disjoint, money-safe, and reconstruct from zero. The only architecture-level risk (the NAV copy) is now documented and guard-railed for E2. Ready for **E2 — the brain (quant + AI referee)**.
