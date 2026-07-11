"""Carry + floor valuation math (pure). The numbers must agree to the cent with
ledger's settlement (E3-B15): 15% carry on gains only, floor at the principal.
"""

from __future__ import annotations

from dataclasses import dataclass

from app.quant.settle import round_half_even_cents

CARRY_RATE = 0.15


@dataclass(frozen=True)
class Valuation:
    nav_cents: int
    principal_cents: int
    gain_cents: int
    carry_cents: int
    take_home_cents: int


def compute_valuation(principal_cents: int, nav_cents: int) -> Valuation:
    """principal + gain - 15%-carry-on-gain, floored at principal (never less)."""
    gain = nav_cents - principal_cents
    if gain > 0:
        carry = round_half_even_cents(CARRY_RATE * gain)
        take_home = principal_cents + gain - carry
    else:
        carry = 0
        take_home = principal_cents  # the floor absorbs a below-principal NAV
    return Valuation(
        nav_cents=nav_cents,
        principal_cents=principal_cents,
        gain_cents=gain,
        carry_cents=carry,
        take_home_cents=take_home,
    )
