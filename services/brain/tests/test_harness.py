"""B01: proves the pytest harness runs against the app package."""

from app.harness import add


def test_add_returns_sum() -> None:
    assert add(2, 3) == 5
