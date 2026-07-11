"""De-vig: turn decimal odds into a normalized implied-probability triple.

Pure. No I/O, no clock, no randomness.
"""

from __future__ import annotations


def implied_probs(
    home: float | None, draw: float | None, away: float | None
) -> tuple[float, float, float] | None:
    """Normalized (p_home, p_draw, p_away) summing to 1.0, or None if any odds are
    missing/invalid. Decimal odds are always > 1.0; the bookmaker's margin (the "vig")
    is removed by normalizing the raw 1/odds so they sum to one."""
    if home is None or draw is None or away is None:
        return None
    if home <= 1.0 or draw <= 1.0 or away <= 1.0:
        return None
    raw = (1.0 / home, 1.0 / draw, 1.0 / away)
    total = raw[0] + raw[1] + raw[2]
    if total <= 0:
        return None
    return (raw[0] / total, raw[1] / total, raw[2] / total)
