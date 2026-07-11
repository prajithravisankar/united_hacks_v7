"""B04 parser edge-case tests. Fixtures only — no network."""

from pathlib import Path

import pytest

from app.data.parsers import load_odds, load_price_history, load_probs

FIX = Path(__file__).parent / "fixtures"


# ---- load_odds (football-data.co.uk) ----


def test_odds_new_format_iso_dates_odds_and_dedupe() -> None:
    rows = load_odds(FIX / "football_new.csv")
    assert len(rows) == 3  # 4 data rows, 1 exact duplicate removed
    first = rows[0]
    assert first.date == "2023-08-12"
    assert first.home == "Arsenal"
    assert first.away == "Nottingham Forest"
    assert first.result == "H"
    assert first.home_odds == 1.30
    assert first.away_odds == 10.00


def test_odds_extreme_values_preserved() -> None:
    rows = load_odds(FIX / "football_new.csv")
    extreme = next(r for r in rows if r.home == "Man City")
    assert extreme.home_odds == 1.01
    assert extreme.away_odds == 101.00


def test_odds_old_ddmmyy_and_missing_bookmaker_columns() -> None:
    rows = load_odds(FIX / "football_old.csv")
    assert len(rows) == 2
    assert rows[0].date == "2000-08-19"  # DD/MM/YY interpreted as 20YY
    assert rows[0].home_odds is None  # file has no B365 columns
    assert rows[0].result == "H"


def test_odds_handles_utf8_bom() -> None:
    rows = load_odds(FIX / "football_bom.csv")
    assert len(rows) == 1
    assert rows[0].home == "Tottenham"  # BOM did not mangle the first column
    assert rows[0].date == "2023-08-20"


def test_odds_empty_file_raises_clean_error() -> None:
    with pytest.raises(ValueError):
        load_odds(FIX / "football_empty.csv")


# ---- load_probs (FiveThirtyEight SPI) ----


def test_probs_spi_parses_and_validates_range() -> None:
    rows = load_probs(FIX / "spi_matches.csv")
    assert len(rows) == 2
    assert rows[0].home == "Arsenal"
    assert rows[0].prob_home == 0.72
    for r in rows:
        for p in (r.prob_home, r.prob_draw, r.prob_away):
            assert 0.0 <= p <= 1.0


def test_probs_empty_file_raises() -> None:
    with pytest.raises(ValueError):
        load_probs(FIX / "football_empty.csv")


# ---- load_price_history (Polymarket) ----


def test_price_history_sorted_deduped_and_range_filtered() -> None:
    ticks = load_price_history(FIX / "polymarket.json")
    ts = [t.t for t in ticks]
    assert ts == sorted(ts)  # out-of-order input sorted ascending
    assert len(ts) == len(set(ts))  # duplicate timestamp removed
    assert len(ticks) == 4  # p=1.5 out-of-range tick dropped
    dup = next(t for t in ticks if t.t == 1700000200)
    assert dup.p == 0.49  # dedupe keeps the later value


def test_price_history_all_probs_in_unit_range() -> None:
    for t in load_price_history(FIX / "polymarket.json"):
        assert 0.0 <= t.p <= 1.0
