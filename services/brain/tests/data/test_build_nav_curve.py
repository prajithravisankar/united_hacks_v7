"""B08: the precomputed NAV curve exists in Oracle and rebuilds byte-identically."""

from __future__ import annotations

from app.quant.build import build_nav_curve


def test_curve_rebuilds_identically(oracle_conn) -> None:  # type: ignore[no-untyped-def]
    r1 = build_nav_curve()
    cur = oracle_conn.cursor()
    cur.execute("SELECT curve_date, nav_cents FROM fact_nav_curve ORDER BY curve_date")
    snap1 = cur.fetchall()

    r2 = build_nav_curve()
    cur.execute("SELECT curve_date, nav_cents FROM fact_nav_curve ORDER BY curve_date")
    snap2 = cur.fetchall()

    assert snap1 == snap2  # persisted curve is deterministic
    assert r1.curve == r2.curve
    assert len(snap1) == len(r1.curve) > 0
    assert r1.final_pool_cents == r2.final_pool_cents
