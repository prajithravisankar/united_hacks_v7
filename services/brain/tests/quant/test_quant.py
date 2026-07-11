"""B08 quant-engine edge-case tests. Pure functions, synthetic inputs, no I/O."""

from __future__ import annotations

import pathlib

from app.data.parsers import MatchOdds
from app.quant.backtest import run_backtest
from app.quant.devig import implied_probs
from app.quant.selector import Bet, select_bet
from app.quant.settle import round_half_even_cents, settle_bet


def mk(
    date: str = "2023-08-12",
    home: str = "Home",
    away: str = "Away",
    result: str | None = "H",
    oh: float | None = 3.0,
    od: float | None = 3.0,
    oa: float | None = 3.0,
    ch: float | None = None,
    cd: float | None = None,
    ca: float | None = None,
) -> MatchOdds:
    return MatchOdds(date, home, away, result, oh, od, oa, ch, cd, ca)


# ---- de-vig ----


def test_implied_probs_sum_to_one() -> None:
    p = implied_probs(2.0, 3.4, 3.6)
    assert p is not None
    assert abs(sum(p) - 1.0) < 1e-9


def test_implied_probs_extreme_odds_ok() -> None:
    p = implied_probs(1.01, 101.0, 101.0)
    assert p is not None and p[0] > 0.9  # heavy favorite


def test_implied_probs_missing_or_invalid_returns_none() -> None:
    assert implied_probs(None, 3.0, 3.0) is None
    assert implied_probs(1.0, 3.0, 3.0) is None  # odds must be > 1.0
    assert implied_probs(0.0, 3.0, 3.0) is None


# ---- selector (edge) ----


def test_no_drift_no_bet() -> None:
    # closing == opening -> zero drift -> below threshold
    assert select_bet(mk(oh=3.0, od=3.0, oa=3.0, ch=3.0, cd=3.0, ca=3.0), 100_000) is None


def test_missing_closing_no_bet() -> None:
    assert select_bet(mk(ch=None, cd=None, ca=None), 100_000) is None


def test_positive_home_drift_bets_home_at_opening_odds() -> None:
    bet = select_bet(mk(oh=3.0, od=3.0, oa=3.0, ch=2.0, cd=3.5, ca=4.0), 100_000)
    assert bet is not None
    assert bet.outcome == "H"
    assert bet.odds == 3.0  # bet at the OPENING price


def test_stake_respects_max_fraction_cap() -> None:
    # drift here is ~0.15 but the cap wins
    bet = select_bet(mk(oh=3.0, od=3.0, oa=3.0, ch=2.0, cd=3.5, ca=4.0), 100_000, max_fraction=0.05)
    assert bet is not None
    assert bet.stake_cents == 5000  # 5% of 100_000, capped by max_fraction


def test_selector_ignores_result_lookahead_safe() -> None:
    m = mk(oh=3.0, od=3.0, oa=3.0, ch=2.0, cd=3.5, ca=4.0)
    with_result = select_bet(m, 100_000)
    without_result = select_bet(
        mk(result=None, oh=3.0, od=3.0, oa=3.0, ch=2.0, cd=3.5, ca=4.0), 100_000
    )
    assert with_result == without_result


# ---- settlement ----


def test_settle_win_loss_void() -> None:
    bet = Bet(outcome="H", odds=3.0, stake_cents=5000)
    assert settle_bet(bet, "H") == 10000  # 5000 * (3.0 - 1)
    assert settle_bet(bet, "A") == -5000
    assert settle_bet(bet, None) == 0  # void


def test_round_half_even_boundary() -> None:
    assert round_half_even_cents(0.5) == 0  # banker's rounding
    assert round_half_even_cents(1.5) == 2
    assert round_half_even_cents(2.5) == 2
    assert settle_bet(Bet("H", 1.5, 1), "H") == 0  # 1 * 0.5 -> 0
    assert settle_bet(Bet("H", 1.5, 3), "H") == 2  # 3 * 0.5 -> 2


# ---- backtest ----

_MATCHES = [
    mk(
        date="2023-08-13",
        home="C",
        away="D",
        result="H",
        oh=3.0,
        od=3.0,
        oa=3.0,
        ch=2.0,
        cd=3.5,
        ca=4.0,
    ),
    mk(
        date="2023-08-12",
        home="A",
        away="B",
        result="A",
        oh=3.0,
        od=3.0,
        oa=3.0,
        ch=4.0,
        cd=3.5,
        ca=2.0,
    ),
    mk(
        date="2023-08-12",
        home="E",
        away="F",
        result="H",
        oh=2.5,
        od=3.4,
        oa=3.6,
        ch=2.2,
        cd=3.4,
        ca=3.8,
    ),
]


def test_backtest_deterministic_and_order_independent() -> None:
    a = run_backtest(_MATCHES, 100_000)
    b = run_backtest(list(reversed(_MATCHES)), 100_000)
    assert a.curve == b.curve  # sorted internally -> input order irrelevant
    assert a == b


def test_backtest_conservation() -> None:
    r = run_backtest(_MATCHES, 100_000)
    assert r.total_pnl_cents == r.final_pool_cents - r.starting_pool_cents


def test_backtest_nav_shape() -> None:
    r = run_backtest(_MATCHES, 100_000)
    dates = [p.date for p in r.curve]
    assert dates == sorted(set(dates))  # strictly increasing, one point per date
    assert all(p.nav_cents >= 0 for p in r.curve)  # pool never goes negative


def test_backtest_empty_is_empty_curve() -> None:
    r = run_backtest([], 100_000)
    assert r.curve == []
    assert r.final_pool_cents == 100_000


# ---- purity guard ----


def test_quant_modules_have_no_clock_or_unseeded_randomness() -> None:
    quant_dir = pathlib.Path(__file__).resolve().parents[2] / "app" / "quant"
    for path in quant_dir.glob("*.py"):
        src = path.read_text()
        assert "datetime.now" not in src, f"{path.name} uses wall-clock"
        assert "random." not in src, f"{path.name} uses randomness"
