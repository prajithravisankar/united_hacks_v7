"""B05 integration tests for the Oracle warehouse loader.

Runs against the compose Oracle; skips (via the oracle_conn fixture) when Oracle
is down, so this never blocks the unit suite / a clean clone.
"""

import pytest

from app.data import seed_oracle

EXPECTED_MATCHES = 1140  # 3 EPL seasons x 380 matches


@pytest.fixture(scope="module", autouse=True)
def _seeded(oracle_conn):  # type: ignore[no-untyped-def]
    """Load the warehouse once for this module (idempotent)."""
    seed_oracle.load()


def test_load_populates_expected_counts() -> None:
    summary = seed_oracle.load()  # idempotent re-run returns fresh counts
    assert summary["matches"] == EXPECTED_MATCHES
    assert summary["seasons"] == 3
    assert summary["teams"] >= 20  # EPL is 20 teams/season, more across 3 seasons


def test_referential_integrity(oracle_conn) -> None:  # type: ignore[no-untyped-def]
    cur = oracle_conn.cursor()
    cur.execute(
        "SELECT COUNT(*) FROM fact_match f "
        "WHERE NOT EXISTS (SELECT 1 FROM dim_team t WHERE t.team_id = f.home_team_id) "
        "   OR NOT EXISTS (SELECT 1 FROM dim_team t WHERE t.team_id = f.away_team_id) "
        "   OR NOT EXISTS (SELECT 1 FROM dim_season s WHERE s.season_id = f.season_id)"
    )
    assert cur.fetchone()[0] == 0


def test_no_nulls_in_required_columns(oracle_conn) -> None:  # type: ignore[no-untyped-def]
    cur = oracle_conn.cursor()
    cur.execute(
        "SELECT COUNT(*) FROM fact_match WHERE season_id IS NULL OR home_team_id IS NULL "
        "OR away_team_id IS NULL OR match_date IS NULL"
    )
    assert cur.fetchone()[0] == 0


def test_result_check_constraint_rejects_bad_value(oracle_conn) -> None:  # type: ignore[no-untyped-def]
    cur = oracle_conn.cursor()
    with pytest.raises(Exception):
        cur.execute(
            "INSERT INTO fact_match (season_id, home_team_id, away_team_id, match_date, result) "
            "VALUES (1, 1, 1, DATE '2020-01-01', 'X')"
        )


def test_loader_is_idempotent() -> None:
    first = seed_oracle.load()
    second = seed_oracle.load()
    assert first["matches"] == second["matches"] == EXPECTED_MATCHES
    assert first["teams"] == second["teams"]


def test_base_rate_query_is_sane(oracle_conn) -> None:  # type: ignore[no-untyped-def]
    """The analytical GROUP BY the quant model will lean on: home-win base rate."""
    cur = oracle_conn.cursor()
    cur.execute(
        "SELECT AVG(CASE WHEN result = 'H' THEN 1 ELSE 0 END) "
        "FROM fact_match WHERE result IS NOT NULL"
    )
    home_win_rate = float(cur.fetchone()[0])
    assert 0.30 < home_win_rate < 0.60  # EPL home advantage sits ~0.43-0.48
