"""Settlement — turn a bet + the real result into exact cents of P&L.

Pure. Float profit is converted to integer cents with round-half-even (banker's
rounding) so no representation drift can ever leak into a balance.
"""

from __future__ import annotations

from decimal import ROUND_HALF_EVEN, Decimal

from app.quant.selector import Bet


def round_half_even_cents(amount: float) -> int:
    return int(Decimal(str(amount)).quantize(Decimal("1"), rounding=ROUND_HALF_EVEN))


def settle_bet(bet: Bet, result: str | None) -> int:
    """Net P&L in cents. Win: stake*(odds-1). Loss: -stake. Void (no result): 0."""
    if result is None:
        return 0
    if result == bet.outcome:
        return round_half_even_cents(bet.stake_cents * (bet.odds - 1.0))
    return -bet.stake_cents
