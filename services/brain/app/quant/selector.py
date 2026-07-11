"""Bet selection — the edge. Pure; uses only pre-game odds (never the result).

Edge = closing-line value: the market's implied probability from OPENING to CLOSING
odds drifts toward the outcome sharp money favors. We bet the outcome with the largest
positive drift (above a threshold), at the OPENING price, sized by the drift (capped).
"""

from __future__ import annotations

from dataclasses import dataclass

from app.data.parsers import MatchOdds
from app.quant.devig import implied_probs

EDGE_THRESHOLD = 0.01  # min closing-line drift to bet (the gate)
MAX_STAKE_FRACTION = 0.02  # cap; between the gate and the cap, bigger drift -> bigger stake

_OUTCOMES = ("H", "D", "A")


@dataclass(frozen=True)
class Bet:
    outcome: str  # "H" | "D" | "A"
    odds: float  # opening decimal odds we bet at
    stake_cents: int


def select_bet(
    match: MatchOdds,
    pool_cents: int,
    *,
    threshold: float = EDGE_THRESHOLD,
    max_fraction: float = MAX_STAKE_FRACTION,
) -> Bet | None:
    """Pick a bet for one match, or None. Deterministic; result is never read."""
    opening = implied_probs(match.home_odds, match.draw_odds, match.away_odds)
    closing = implied_probs(match.home_odds_close, match.draw_odds_close, match.away_odds_close)
    if opening is None or closing is None or pool_cents <= 0:
        return None

    drifts = [(_OUTCOMES[i], closing[i] - opening[i]) for i in range(3)]
    outcome, drift = max(drifts, key=lambda pair: pair[1])
    if drift < threshold:
        return None

    fraction = min(max_fraction, drift)  # bigger drift -> bigger stake, capped
    stake = int(pool_cents * fraction)
    if stake <= 0:
        return None

    opening_odds = {"H": match.home_odds, "D": match.draw_odds, "A": match.away_odds}[outcome]
    assert opening_odds is not None  # implied_probs already guaranteed all present
    return Bet(outcome=outcome, odds=opening_odds, stake_cents=stake)
