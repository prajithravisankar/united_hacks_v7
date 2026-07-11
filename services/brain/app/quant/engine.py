"""QuantEngine — serves the precomputed fund curve as per-commitment valuations,
projections, and (you-drive) user bets. Deterministic; Oracle-independent (takes an
already-loaded curve + markets), so it unit-tests without a database.
"""

from __future__ import annotations

import bisect
from dataclasses import dataclass

from app.quant.settle import round_half_even_cents
from app.quant.valuation import Valuation, compute_valuation

AUTO = "AUTO"
USER = "USER"


class CommitmentNotFound(Exception):
    pass


class InvalidRequest(Exception):
    pass


class MarketNotFound(Exception):
    pass


class EngineUnavailable(Exception):
    """The fund data store (Oracle) could not be loaded."""


@dataclass(frozen=True)
class Market:
    market_id: str
    description: str
    implied_prob: float  # de-vigged closing prob for the "yes" outcome
    decimal_odds: float  # 1 / implied_prob (payout multiple)
    resolved_yes: bool  # did the "yes" outcome actually happen (historical)


@dataclass(frozen=True)
class Projection:
    cash_now_cents: int
    ride_p10_cents: int
    ride_p50_cents: int
    ride_p90_cents: int


class QuantEngine:
    def __init__(self, curve: list[tuple[str, int]], markets: list[Market] | None = None):
        self._curve = sorted(curve)
        self._dates = [d for d, _ in self._curve]
        self._nav_by_date = dict(self._curve)
        self._markets = {m.market_id: m for m in (markets or [])}
        self._user_pnl: dict[str, list[int]] = {}

    # ---- fund helpers ----

    def _fund_nav_at(self, iso_date: str) -> int | None:
        """Last known fund NAV on or before iso_date (step function), or None."""
        idx = bisect.bisect_right(self._dates, iso_date) - 1
        return None if idx < 0 else self._nav_by_date[self._dates[idx]]

    def _commitment_nav(
        self, commitment_id: str, principal_cents: int, start_date: str, as_of: str, drive_mode: str
    ) -> int:
        base = self._fund_nav_at(start_date)
        current = self._fund_nav_at(as_of)
        if base is None or current is None or base == 0:
            raise CommitmentNotFound(f"no fund data for window {start_date}..{as_of}")
        nav = round_half_even_cents(principal_cents * current / base)
        if drive_mode == USER:
            nav = max(0, nav + sum(self._user_pnl.get(commitment_id, [])))  # never below zero
        return nav

    # ---- RPCs ----

    def get_valuation(
        self, commitment_id: str, principal_cents: int, start_date: str, as_of: str, drive_mode: str
    ) -> Valuation:
        if as_of < start_date:
            raise InvalidRequest("as_of is before the commitment start")
        nav = self._commitment_nav(commitment_id, principal_cents, start_date, as_of, drive_mode)
        return compute_valuation(principal_cents, nav)

    def get_nav_curve(
        self,
        commitment_id: str,
        principal_cents: int,
        start_date: str,
        end_date: str,
        drive_mode: str,
    ) -> list[tuple[str, int]]:
        base = self._fund_nav_at(start_date)
        if base is None or base == 0:
            raise CommitmentNotFound(f"no fund data at start {start_date}")
        user_delta = sum(self._user_pnl.get(commitment_id, [])) if drive_mode == USER else 0
        points: list[tuple[str, int]] = []
        for date, fund_nav in self._curve:
            if date < start_date or date > end_date:  # clip to the window (never pad)
                continue
            base_nav = round_half_even_cents(principal_cents * fund_nav / base)
            points.append((date, max(0, base_nav + user_delta)))  # never below zero
        return points

    def project_outcomes(
        self, commitment_id: str, principal_cents: int, start_date: str, as_of: str, drive_mode: str
    ) -> Projection:
        current = self.get_valuation(commitment_id, principal_cents, start_date, as_of, drive_mode)
        ratios = self._horizon_ratios(horizon=30)
        if not ratios:
            same = current.take_home_cents
            return Projection(current.take_home_cents, same, same, same)

        def take_home_at(ratio: float) -> int:
            return compute_valuation(
                principal_cents, round(current.nav_cents * ratio)
            ).take_home_cents

        return Projection(
            cash_now_cents=current.take_home_cents,
            ride_p10_cents=take_home_at(_percentile(ratios, 0.10)),
            ride_p50_cents=take_home_at(_percentile(ratios, 0.50)),
            ride_p90_cents=take_home_at(_percentile(ratios, 0.90)),
        )

    def _horizon_ratios(self, horizon: int) -> list[float]:
        """Historical fund growth over `horizon` curve-steps — the ride distribution."""
        out: list[float] = []
        for i in range(len(self._curve) - horizon):
            start_nav = self._curve[i][1]
            if start_nav > 0:
                out.append(self._curve[i + horizon][1] / start_nav)
        return out

    def list_open_markets(self, as_of: str, limit: int = 5) -> list[Market]:
        return list(self._markets.values())[:limit]

    def place_user_bet(
        self, commitment_id: str, market_id: str, side: str, stake_cents: int, pool_cents: int
    ) -> int:
        """Settle a user-driven bet deterministically by the market's real outcome.
        Returns the P&L; records it so USER-mode valuations diverge from AUTO."""
        market = self._markets.get(market_id)
        if market is None:
            raise MarketNotFound(market_id)
        if side not in ("yes", "no"):
            raise InvalidRequest("side must be 'yes' or 'no'")
        if stake_cents <= 0 or stake_cents > pool_cents:
            raise InvalidRequest("stake must be > 0 and <= available pool")
        won = market.resolved_yes if side == "yes" else not market.resolved_yes
        from app.quant.settle import round_half_even_cents

        pnl = (
            round_half_even_cents(stake_cents * (market.decimal_odds - 1.0))
            if won
            else -stake_cents
        )
        self._user_pnl.setdefault(commitment_id, []).append(pnl)
        return pnl


def _percentile(values: list[float], q: float) -> float:
    ordered = sorted(values)
    idx = min(len(ordered) - 1, max(0, round(q * (len(ordered) - 1))))
    return ordered[idx]
