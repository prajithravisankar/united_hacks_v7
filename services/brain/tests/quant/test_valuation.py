"""B09 valuation math — must agree to the cent with ledger settlement (B15)."""

from __future__ import annotations

from app.quant.valuation import compute_valuation


def test_canonical_100_to_155_take_home_146_75() -> None:
    v = compute_valuation(10_000, 15_500)  # $100 stake, NAV $155
    assert v.gain_cents == 5_500
    assert v.carry_cents == 825  # 15% of the $55 gain
    assert v.take_home_cents == 14_675  # $146.75


def test_floor_when_nav_below_principal() -> None:
    v = compute_valuation(10_000, 8_000)
    assert v.gain_cents == -2_000
    assert v.carry_cents == 0
    assert v.take_home_cents == 10_000  # never below the principal


def test_zero_gain_zero_carry() -> None:
    v = compute_valuation(10_000, 10_000)
    assert v.carry_cents == 0
    assert v.take_home_cents == 10_000
