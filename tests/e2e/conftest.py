"""Fixtures for the BOYS E2E suite. Tests drive the demo commitment (id 1) through the public API at the nginx
edge; the `demo_reset` fixture restores the pristine seeded scenario before each test that mutates it."""
import httpx
import pytest

from helpers import API, Api, run_script


@pytest.fixture(scope="session", autouse=True)
def _require_stack():
    """Fail fast with a clear message if the demo stack isn't up."""
    try:
        r = httpx.get(API.rsplit("/api", 1)[0] + "/api/health", timeout=5)
        r.raise_for_status()
    except Exception as exc:  # noqa: BLE001
        pytest.exit(f"demo stack not reachable at the nginx edge ({exc}). Run: ./scripts/demo_up.sh", returncode=2)


@pytest.fixture
def api():
    a = Api()
    yield a
    a.close()


@pytest.fixture
def demo_reset():
    """Reset commitment 1 to the pristine seeded scenario (active, $100 escrowed, 3 pending milestones)."""
    r = run_script("seed_demo.sh")
    assert r.returncode == 0, f"seed_demo.sh failed:\n{r.stdout}\n{r.stderr}"
    yield
