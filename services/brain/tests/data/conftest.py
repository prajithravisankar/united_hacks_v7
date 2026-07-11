"""Shared fixtures for data tests. Oracle fixtures skip cleanly when Oracle is down,
so the pure-unit parser tests never require a database."""

import pytest

from app.data import seed_oracle


@pytest.fixture(scope="module")
def oracle_conn():  # type: ignore[no-untyped-def]
    """A live Oracle connection, or skip the whole module if unreachable."""
    try:
        conn = seed_oracle.connect()
    except Exception as exc:  # noqa: BLE001 - any connect failure means "no Oracle here"
        pytest.skip(f"Oracle not reachable: {exc}")
    yield conn
    conn.close()
