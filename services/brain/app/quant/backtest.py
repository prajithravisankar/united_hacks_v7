"""The backtest — replay matches in date order, accumulating the action pool's P&L
into a daily NAV curve. Pure and deterministic: same input -> byte-identical curve.
"""

from __future__ import annotations

from dataclasses import dataclass
from itertools import groupby

from app.data.parsers import MatchOdds
from app.quant.selector import EDGE_THRESHOLD, MAX_STAKE_FRACTION, select_bet
from app.quant.settle import settle_bet


@dataclass(frozen=True)
class NavPoint:
    date: str  # ISO-8601
    nav_cents: int


@dataclass(frozen=True)
class BacktestResult:
    curve: list[NavPoint]
    bets_placed: int
    bets_won: int
    starting_pool_cents: int
    final_pool_cents: int
    total_pnl_cents: int


def run_backtest(
    matches: list[MatchOdds],
    starting_pool_cents: int,
    *,
    threshold: float = EDGE_THRESHOLD,
    max_fraction: float = MAX_STAKE_FRACTION,
) -> BacktestResult:
    # Deterministic order: date, then home, then away (stable regardless of input order).
    ordered = sorted(matches, key=lambda m: (m.date, m.home, m.away))

    pool = starting_pool_cents
    curve: list[NavPoint] = []
    bets_placed = 0
    bets_won = 0
    total_pnl = 0

    for date, group in groupby(ordered, key=lambda m: m.date):
        for match in group:
            bet = select_bet(match, pool, threshold=threshold, max_fraction=max_fraction)
            if bet is None:
                continue
            pnl = settle_bet(bet, match.result)  # net change to the pool
            pool += pnl
            total_pnl += pnl
            bets_placed += 1
            if match.result == bet.outcome:
                bets_won += 1
        curve.append(NavPoint(date=date, nav_cents=pool))

    return BacktestResult(
        curve=curve,
        bets_placed=bets_placed,
        bets_won=bets_won,
        starting_pool_cents=starting_pool_cents,
        final_pool_cents=pool,
        total_pnl_cents=total_pnl,
    )
