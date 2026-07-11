"""Load a QuantEngine from Oracle: the precomputed NAV curve + a few user-bet markets."""

from __future__ import annotations

from app.data.seed_oracle import connect
from app.quant.engine import Market, QuantEngine


def load_engine() -> QuantEngine:
    conn = connect()
    try:
        cur = conn.cursor()
        cur.execute(
            "SELECT TO_CHAR(curve_date,'YYYY-MM-DD'), nav_cents FROM fact_nav_curve ORDER BY curve_date"
        )
        curve = [(str(d), int(n)) for d, n in cur.fetchall()]

        cur.execute(
            "SELECT TO_CHAR(match_date,'YYYY-MM-DD'), home_odds, result "
            "FROM fact_match WHERE home_odds IS NOT NULL AND result IS NOT NULL "
            "ORDER BY match_date DESC, home_team_id, away_team_id FETCH FIRST 20 ROWS ONLY"
        )
        markets = []
        for i, (date, home_odds, result) in enumerate(cur.fetchall()):
            odds = float(home_odds)
            markets.append(
                Market(
                    market_id=f"m{i}",
                    description=f"Home win ({date})",
                    implied_prob=1.0 / odds,
                    decimal_odds=odds,
                    resolved_yes=(result == "H"),
                )
            )
        return QuantEngine(curve, markets)
    finally:
        conn.close()
