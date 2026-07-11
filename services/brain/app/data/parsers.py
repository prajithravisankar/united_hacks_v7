"""Offline parsers for the seeded sports datasets.

All three return typed, validated records and never touch the network. They are
strict about the things that bite later (date formats, missing columns, junk
odds, out-of-order/duplicate ticks) so data bugs surface here, not at hour 17.
"""

from __future__ import annotations

import csv
import json
import logging
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

log = logging.getLogger(__name__)


@dataclass(frozen=True)
class MatchOdds:
    date: str  # ISO-8601 (YYYY-MM-DD)
    home: str
    away: str
    result: str | None  # "H" | "D" | "A" | None
    home_odds: float | None
    draw_odds: float | None
    away_odds: float | None


@dataclass(frozen=True)
class ModelProb:
    date: str  # ISO-8601
    home: str
    away: str
    prob_home: float
    prob_draw: float
    prob_away: float


@dataclass(frozen=True)
class PriceTick:
    t: int  # epoch seconds
    p: float  # probability in [0, 1]


def _iso_date(raw: str) -> str | None:
    """football-data uses DD/MM/YYYY (newer seasons) and DD/MM/YY (older)."""
    raw = raw.strip()
    for fmt in ("%d/%m/%Y", "%d/%m/%y"):
        try:
            return datetime.strptime(raw, fmt).strftime("%Y-%m-%d")
        except ValueError:
            continue
    return None


def _decimal_odds(row: dict[str, str], key: str) -> float | None:
    """Decimal odds are always > 1.0; anything missing/invalid becomes None."""
    val = (row.get(key) or "").strip()
    if not val:
        return None
    try:
        odds = float(val)
    except ValueError:
        return None
    return odds if odds > 1.0 else None


def _rows(path: Path) -> list[dict[str, str]]:
    """Read a CSV with UTF-8 BOM tolerance; raise on an empty/headerless file."""
    with path.open("r", encoding="utf-8-sig", newline="") as fh:
        reader = csv.DictReader(fh)
        if reader.fieldnames is None:
            raise ValueError(f"{path.name}: empty file (no header row)")
        rows = [r for r in reader]
    if not rows:
        raise ValueError(f"{path.name}: no data rows")
    return rows


def load_odds(path: Path) -> list[MatchOdds]:
    """Parse a football-data.co.uk results+odds CSV. Deduplicates on (date, home, away)."""
    out: list[MatchOdds] = []
    seen: set[tuple[str, str, str]] = set()
    for row in _rows(path):
        iso = _iso_date(row.get("Date", ""))
        home = (row.get("HomeTeam") or "").strip()
        away = (row.get("AwayTeam") or "").strip()
        if iso is None or not home or not away:
            log.warning("%s: skipping row with bad date/teams: %r", path.name, row.get("Date"))
            continue
        key = (iso, home, away)
        if key in seen:
            continue
        seen.add(key)
        result = (row.get("FTR") or "").strip() or None
        out.append(
            MatchOdds(
                date=iso,
                home=home,
                away=away,
                result=result,
                home_odds=_decimal_odds(row, "B365H"),
                draw_odds=_decimal_odds(row, "B365D"),
                away_odds=_decimal_odds(row, "B365A"),
            )
        )
    if not out:
        raise ValueError(f"{path.name}: no usable rows after parsing")
    return out


def load_probs(path: Path) -> list[ModelProb]:
    """Parse a FiveThirtyEight SPI (soccer) match file with explicit win/draw/loss probs."""
    out: list[ModelProb] = []
    for row in _rows(path):
        date = (row.get("date") or "").strip()
        home = (row.get("team1") or "").strip()
        away = (row.get("team2") or "").strip()
        try:
            ph = float(row["prob1"])
            pd = float(row["probtie"])
            pa = float(row["prob2"])
        except (KeyError, ValueError):
            log.warning("%s: skipping row with missing/invalid probs: %r", path.name, home)
            continue
        if not date or not home or not away:
            continue
        if not all(0.0 <= p <= 1.0 for p in (ph, pd, pa)):
            log.warning("%s: skipping out-of-range probs for %s", path.name, home)
            continue
        out.append(
            ModelProb(date=date, home=home, away=away, prob_home=ph, prob_draw=pd, prob_away=pa)
        )
    if not out:
        raise ValueError(f"{path.name}: no usable probability rows")
    return out


def load_price_history(path: Path) -> list[PriceTick]:
    """Parse a Polymarket price-history JSON. Sorts by time, dedupes (keeping the later value)."""
    with path.open("r", encoding="utf-8") as fh:
        doc = json.load(fh)
    raw = doc["history"] if isinstance(doc, dict) else doc
    by_time: dict[int, float] = {}
    for item in raw:
        try:
            t = int(item["t"])
            p = float(item["p"])
        except (KeyError, TypeError, ValueError):
            continue
        if not 0.0 <= p <= 1.0:
            log.warning("%s: dropping out-of-range price %r at t=%s", path.name, p, item.get("t"))
            continue
        by_time[t] = p  # later occurrence wins
    if not by_time:
        raise ValueError(f"{path.name}: no valid price ticks")
    return [PriceTick(t=t, p=by_time[t]) for t in sorted(by_time)]
