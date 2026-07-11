"""R3: the Oracle repo loads a populated engine from fact_nav_curve (integration)."""

from __future__ import annotations

from app.quant import repo


def test_load_engine_populates_from_oracle(oracle_conn) -> None:  # type: ignore[no-untyped-def]
    engine = repo.load_engine()
    assert len(engine._dates) > 0  # curve loaded
    valuation = engine.get_valuation("c", 10_000, engine._dates[0], engine._dates[-1], "AUTO")
    assert valuation.nav_cents > 0
    assert engine.list_open_markets("2024-01-01")  # markets loaded from fact_match
