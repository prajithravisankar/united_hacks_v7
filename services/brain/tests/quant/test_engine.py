"""B09 engine logic — synthetic curve, no Oracle."""

from __future__ import annotations

from datetime import date, timedelta

import pytest

from app.quant.engine import (
    AUTO,
    USER,
    CommitmentNotFound,
    InvalidRequest,
    Market,
    MarketNotFound,
    QuantEngine,
)

CURVE = [("2023-01-01", 100_000), ("2023-03-01", 120_000), ("2023-06-01", 155_000)]


def _long_curve(n: int = 60) -> list[tuple[str, int]]:
    d0 = date(2023, 1, 1)
    return [((d0 + timedelta(days=i)).isoformat(), 100_000 + i * 500) for i in range(n)]


def test_valuation_scales_by_fund_growth() -> None:
    v = QuantEngine(CURVE).get_valuation("c1", 10_000, "2023-01-01", "2023-06-01", AUTO)
    assert v.nav_cents == 15_500  # 10_000 * 155_000/100_000
    assert v.take_home_cents == 14_675


def test_valuation_as_of_before_start_is_invalid() -> None:
    with pytest.raises(InvalidRequest):
        QuantEngine(CURVE).get_valuation("c1", 10_000, "2023-06-01", "2023-01-01", AUTO)


def test_valuation_before_any_data_not_found() -> None:
    with pytest.raises(CommitmentNotFound):
        QuantEngine(CURVE).get_valuation("c1", 10_000, "2020-01-01", "2020-02-01", AUTO)


def test_nav_curve_clips_to_window() -> None:
    pts = QuantEngine(CURVE).get_nav_curve("c1", 10_000, "2023-01-01", "2023-04-01", AUTO)
    assert [d for d, _ in pts] == ["2023-01-01", "2023-03-01"]  # 2023-06-01 clipped, not padded


def test_projection_monotonic_and_cash_now_matches() -> None:
    e = QuantEngine(_long_curve())
    val = e.get_valuation("c1", 10_000, "2023-01-01", "2023-02-15", AUTO)
    proj = e.project_outcomes("c1", 10_000, "2023-01-01", "2023-02-15", AUTO)
    assert proj.cash_now_cents == val.take_home_cents
    assert proj.ride_p10_cents <= proj.ride_p50_cents <= proj.ride_p90_cents
    assert proj.ride_p10_cents >= 10_000  # never below principal


def test_you_drive_bet_diverges_from_auto() -> None:
    market = Market("mA", "A win", implied_prob=0.5, decimal_odds=2.0, resolved_yes=True)
    e = QuantEngine(CURVE, [market])
    auto = e.get_valuation("c1", 10_000, "2023-01-01", "2023-06-01", AUTO)
    pnl = e.place_user_bet("c1", "mA", "yes", 1_000, 100_000)  # yes wins at 2.0 -> +1000
    assert pnl == 1_000
    user = e.get_valuation("c1", 10_000, "2023-01-01", "2023-06-01", USER)
    assert user.nav_cents == auto.nav_cents + 1_000  # user-mode curve diverged


def test_you_drive_loss_clamps_nav_at_zero() -> None:
    # R3: a user-driven loss bigger than the fund NAV must floor at 0, never go negative.
    flat = [("2023-01-01", 100_000), ("2023-06-01", 100_000)]  # no fund growth
    market = Market("mA", "A win", implied_prob=0.5, decimal_odds=2.0, resolved_yes=False)
    e = QuantEngine(flat, [market])
    pnl = e.place_user_bet("c1", "mA", "yes", 20_000, 100_000)  # "yes" loses -> -20_000
    assert pnl == -20_000
    val = e.get_valuation("c1", 10_000, "2023-01-01", "2023-06-01", USER)  # base nav 10_000
    assert val.nav_cents == 0  # max(0, 10_000 - 20_000), not -10_000
    curve = e.get_nav_curve("c1", 10_000, "2023-01-01", "2023-06-01", USER)
    assert all(n >= 0 for _, n in curve)  # every point floored


def test_place_bet_unknown_market() -> None:
    with pytest.raises(MarketNotFound):
        QuantEngine(CURVE).place_user_bet("c1", "nope", "yes", 1_000, 100_000)


def test_place_bet_stake_over_pool() -> None:
    market = Market("mA", "A", 0.5, 2.0, True)
    with pytest.raises(InvalidRequest):
        QuantEngine(CURVE, [market]).place_user_bet("c1", "mA", "yes", 200_000, 100_000)
