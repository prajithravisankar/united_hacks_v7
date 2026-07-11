# E2-B08 — Quant Engine: De-Vig, Edge, Backtest → Deterministic NAV Curve

## What we built (plain English)
The honest core of the "smart fund." Pure functions that turn historical odds into a backtested, day-by-day NAV curve for the action pool, plus a build step that persists that curve to Oracle. Same input → byte-identical curve. This is also where float → integer-cents conversion happens (with banker's rounding), so no money drift can leak in.

## The edge, in one paragraph (and why it isn't rigged)
The signal is **closing-line value (CLV)** — the single most-cited predictor of long-term betting profit:
1. **De-vig**: `implied_probs(h,d,a)` normalizes `1/odds` so the three outcome probabilities sum to 1 (removing the bookmaker margin).
2. **Select**: for each match, compare the market's implied probability at the **opening** line vs the **closing** line. The outcome whose probability *drifted up the most* (above a **1% gate**) is where sharp money moved. Bet that outcome, **at the opening price**, sized by the drift (proportional between the 1% gate and a prudent **2% cap** of the pool).
3. **Settle**: by the **real result** — win pays `stake × (odds − 1)`, loss pays `−stake`, no-result voids to 0.

### Two honesty claims, stated with equal weight

**(a) No result look-ahead** — the cheap "isn't it rigged?" answer. `select_bet` reads only *pre-game odds*, **never the result**. A test proves the selector makes identical picks whether the result is present or stripped (`test_selector_ignores_result_lookahead_safe`). The result is used *only* to settle a bet already chosen — exactly what a backtest is. The quant modules are 100% pure (a grep test forbids `datetime.now` and unseeded `random`), so the curve is byte-reproducible.

**(b) The real limitation — selection consumes the closing line.** This is the claim that actually matters, and (a) does *not* answer it. The bet is *chosen* from the opening→closing drift, but the closing line is **not observable at the moment the opening price is available**. So the backtest is an **upper bound**: it measures the return you'd earn *if you could predict the closing move* and act on it at the opening price. It is **not** a claim that this exact strategy is live-tradable. Turning it into a real edge needs a model that *forecasts* the closing drift from information available pre-open; here the realized closing line stands in as that forecast's proxy. Everything is play-money/simulated per the product. Quote (b) alongside the return figure, never the return alone.

## Backtest results (the honest numbers to quote)
Over **3 EPL seasons** from a $1,000 action pool, drift-proportional sizing (1% gate → 2% cap):
- **850 bets**, **40.8% strike rate**, **360 curve points** (one per match-day).
- **$1,000 → $1,477.47 (+47.7%)** — a credible fund return for a selective CLV strategy, not a moonshot.
- Always paired with limitation **(b)** above: this is the upper-bound return of a strategy that consumes the closing line, not a live-tradable +47.7%.

## How it works
- `app/quant/devig.py`, `selector.py`, `settle.py`, `backtest.py` — **pure, no I/O**.
- `app/quant/build.py` — the only I/O: loads matches via the B04 parser (extended for closing odds `B365CH/CD/CA`), runs the backtest, and rewrites Oracle `fact_nav_curve` (commitment-agnostic per R2 — the analytical *source* of NAV).

## How to run / verify it
```bash
./scripts/build_nav_curve.sh    # -> curve points: 360  bets: 850  hit rate: 40.8%  final: $1,477.47  return: +47.7%
cd services/brain && uv run pytest tests/quant/                     # pure-function + engine + gRPC tests
uv run pytest tests/data/test_build_nav_curve.py                    # curve rebuilds identically in Oracle
```
Determinism is proven end-to-end: `build_nav_curve()` twice → identical `fact_nav_curve` rows.

## Gotchas / follow-ups
- Money is integer cents; float profit → cents via `round_half_even_cents` (Decimal, ROUND_HALF_EVEN) — tested at the 0.5¢ boundary.
- The pool can never go negative: `stake = fraction × pool ≤ pool`, so a loss leaves `pool ≥ 0`.
- Backtest **input** is read from the validated CSV parser (which now carries closing odds); a future refinement could store closing odds in `fact_match` so Oracle is the sole source.
- Next: B09 serves this curve over gRPC (`GetNavCurve`) and layers the carry/floor valuation math on top.
