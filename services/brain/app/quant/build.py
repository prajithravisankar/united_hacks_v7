"""Build the deterministic NAV curve from the seeded data and persist it to Oracle.

The precomputed curve is the locked decision — runtime (B09) serves it, never recomputes.
"""

from __future__ import annotations

from datetime import date

from app.data.parsers import MatchOdds, load_odds
from app.data.seed_oracle import RAW, SEASONS, apply_ddl, connect
from app.quant.backtest import BacktestResult, run_backtest

STARTING_POOL_CENTS = 100_000  # $1,000.00 action pool


def _all_matches() -> list[MatchOdds]:
    matches: list[MatchOdds] = []
    for fname in SEASONS:
        matches.extend(load_odds(RAW / fname))
    return matches


def build_nav_curve() -> BacktestResult:
    """Run the backtest and rewrite fact_nav_curve (idempotent — delete + insert)."""
    result = run_backtest(_all_matches(), STARTING_POOL_CENTS)
    conn = connect()
    try:
        cur = conn.cursor()
        apply_ddl(cur)  # ensure fact_nav_curve exists
        cur.execute("DELETE FROM fact_nav_curve")
        cur.executemany(
            "INSERT INTO fact_nav_curve (curve_date, nav_cents) VALUES (:1, :2)",
            [(date.fromisoformat(p.date), p.nav_cents) for p in result.curve],
        )
        conn.commit()
    finally:
        conn.close()
    return result


if __name__ == "__main__":
    r = build_nav_curve()
    hit = (r.bets_won / r.bets_placed) if r.bets_placed else 0.0
    ret = (r.final_pool_cents - r.starting_pool_cents) / r.starting_pool_cents
    print(
        f"  curve points: {len(r.curve)}  bets: {r.bets_placed}  "
        f"hit rate: {hit:.1%}  final: ${r.final_pool_cents / 100:,.2f}  return: {ret:+.1%}"
    )
