# R3 — Brain Review (Epic 2)

*The quant-and-referee audit: is the fund edge honestly stated, does money math hold to the cent, is it deterministic, and does it degrade gracefully when Oracle is down? Written to be read cold — and to be the money-math cheat-sheet for the pitch.*

---

## 1. Headline result

**The brain is honest, deterministic to the byte, and correct to the cent.** The audit found four fix-now issues (below) — all resolved. The fund's edge is a real, documented signal (closing-line value) with **no result look-ahead**, and its one genuine limitation is now stated with equal prominence to the return figure. The play-money math — 15% carry on gains only, take-home floored at principal — was cross-examined live over gRPC in three scenarios and holds exactly. 74 brain tests green; `ruff`/`black`/`mypy --strict` clean.

## 2. The four fix-now findings (all resolved)

| # | Finding | Severity | Fix |
|---|---|---|---|
| 1 | **Dead stake-sizing.** `EDGE_THRESHOLD == MAX_STAKE_FRACTION == 0.02`, so `min(0.02, drift)` was *always* 0.02 — "sized by drift" was inert; every qualifying bet took the 2% cap. | High (claim vs. reality) | Lowered the gate to **1%** (`EDGE_THRESHOLD = 0.01`). Drift now scales the stake proportionally between the 1% gate and the 2% cap. Guarded by `test_stake_scales_with_drift_at_production_defaults`. |
| 2 | **You-drive NAV could go negative.** A user-driven losing bet larger than the fund NAV made `_commitment_nav` return a negative number; the per-commitment NAV also used raw float `round()`. | High (money invariant) | `nav = max(0, nav + user_pnl)` in both `_commitment_nav` and `get_nav_curve`; switched to `round_half_even_cents` (Decimal, banker's) so no raw float lands in a balance. Guarded by `test_you_drive_loss_clamps_nav_at_zero`. |
| 3 | **Oracle failure leaked the driver string / had no guard.** `_eng()` raised the raw `oracledb` error (DSN, host:port) to the gRPC caller; `ListOpenMarkets`/`PlaceUserBet` had no try/except at all. | Medium (info leak + UX) | `_eng()` now wraps any load failure in `EngineUnavailable`; every quant RPC maps it to a **generic** `UNAVAILABLE` ("fund data temporarily unavailable") — no DSN, no host, no port. `seed_oracle.connect` got a `tcp_connect_timeout=5` so a dead Oracle fails fast instead of hanging the demo. Guarded by `test_grpc_unavailable_when_oracle_down` (asserts `DPY-6005` and `15211` are absent from the details). |
| 4 | **Non-deterministic market ordering.** `repo.load_engine` ordered markets by `match_date DESC` only — ties (same-day matches) could reorder run-to-run, changing which markets surface. | Low (determinism) | Added `home_team_id, away_team_id` tiebreak to the `ORDER BY`. |

## 3. The edge, stated honestly (the "isn't it rigged?" answer)

The signal is **closing-line value (CLV)** — de-vig `1/odds` to strip the bookmaker margin, then bet the outcome whose implied probability drifted up the most from the **opening** to the **closing** line, **at the opening price**, sized by the drift. Two honesty claims, given equal weight:

- **(a) No result look-ahead.** `select_bet` reads only pre-game odds, never the result; `test_selector_ignores_result_lookahead_safe` proves it picks identically whether the result is present or stripped. The quant modules are 100% pure (a grep test forbids `datetime.now` and unseeded `random`).
- **(b) The real limitation.** Selection *consumes the closing line*, which isn't observable when the opening price is available. So the backtest is an **upper bound** — the return you'd earn *if you could predict the closing move* — not a claim of live tradability. Always quote (b) with the number, never the number alone.

## 4. Money-math cross-exam (the pitch cheat-sheet)

Live `GetValuation` over gRPC (port 50061) against the freshly-built curve, principal $100.00 (10 000¢). Every row: `principal + gain == nav` (conservation) and `take_home ≥ principal` (floor).

| Scenario | Window | NAV | Gain | Carry (15% of gain) | Take-home | What it proves |
|---|---|---|---|---|---|---|
| **GAIN** | full run `2021-08-13 → 2024-05-19` | **14 960** | +4 960 | **744** (= ⌊0.15 × 4 960⌋) | **14 216** (= 14 960 − 744) | Carry is taken **only on the gain**, so you keep the principal plus 85% of the upside. |
| **ZERO** | `2021-08-13 → 2021-08-13` (start == as-of) | 10 000 | 0 | 0 | 10 000 | No movement → nothing in, nothing out; no phantom carry. |
| **BELOW** | drawdown `2024-03-16 → 2024-05-03` | 7 530 | −2 470 | 0 | **10 000** | A real −24.7% loss, but take-home is **floored at principal** — "lock your floor, gamble your gains," and no carry on a loss. |

*(Reproduce: `cd services/brain && PYTHONPATH="$PWD:$PWD/gen" uv run python <cross-exam client>` — see §7.)*

## 5. Determinism (the "reset between demo takes" guarantee)

The backtest is a pure function of the seeded CSVs. Two independent runs produce a **byte-identical** curve:

```
run1 sha256 38d651207951da40…  points 360  final 147747
run2 sha256 38d651207951da40…  → IDENTICAL
```

`build_nav_curve()` rewrites `fact_nav_curve` idempotently (delete + insert), so re-running never drifts or duplicates.

## 6. The honest backtest number (recomputed after fix #1)

Lowering the gate from 2% → 1% admits more, weaker-signal bets — the number went **down**, which is the honest direction:

| | Before (gate == cap, inert sizing) | After (1% gate → 2% cap, live sizing) |
|---|---|---|
| Bets placed | 491 | **850** |
| Hit rate | 44.8% | **40.8%** |
| $1,000 → | $1,747.53 (**+74.8%**) | **$1,477.47 (+47.7%)** |

+47.7% over 3 EPL seasons is a credible selective-CLV return, not a moonshot — and it's the **upper bound** of §3(b), not a live-tradable claim.

## 7. Reproduce every claim

```bash
# 1. all brain tests + strict lint
cd services/brain && uv run pytest -q                 # 74 passed
uv run ruff check . && uv run black --check . && uv run mypy .   # clean

# 2. honest backtest number + determinism
uv run python -c "from app.quant.build import _all_matches, STARTING_POOL_CENTS; \
from app.quant.backtest import run_backtest; r=run_backtest(_all_matches(), STARTING_POOL_CENTS); \
print(r.bets_placed, r.bets_won, r.final_pool_cents)"     # 850 347 147747

# 3. rebuild the persisted curve (idempotent)
./scripts/build_nav_curve.sh                          # +47.7%, 360 points

# 4. live money-math cross-exam over gRPC (container must be up)
PYTHONPATH="$PWD:$PWD/gen" uv run python scratchpad/xexam.py   # ALL CROSS-EXAM CHECKS PASS
```

## 8. Coverage added this review

- `tests/quant/test_quant.py::test_stake_scales_with_drift_at_production_defaults` — fix #1 regression guard.
- `tests/quant/test_engine.py::test_you_drive_loss_clamps_nav_at_zero` — fix #2 money invariant.
- `tests/quant/test_quant_grpc.py::test_grpc_unavailable_when_oracle_down` — fix #3 (asserts the driver/DSN string never surfaces).
- (Pre-existing this epic) `tests/data/test_repo.py`, `tests/referee/test_providers.py` closed the last repo/`check_proof`-fallback gaps.

## 9. Verdict

Epic 2 passes review. The fund is honest (real signal, no look-ahead, limitation stated), the money math is provably correct to the cent with the floor and carry enforced, the whole thing is deterministic, and it fails safe when Oracle is down. Cleared to build the engine (Epic 3).
