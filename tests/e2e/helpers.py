"""Shared helpers for the BOYS E2E suite: the API client, exact-cent settlement math (mirroring the ledger's
banker's rounding), gRPC stubs, and SQL access to the ledger DB."""
from __future__ import annotations

import base64
import os
import pathlib
import subprocess
import sys
from decimal import ROUND_HALF_EVEN, Decimal

import httpx

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "services" / "brain" / "gen"))  # brain + engine gRPC stubs

EDGE = os.environ.get("BOYS_EDGE", "http://127.0.0.1:8888")
API = EDGE + "/api"
WS = EDGE.replace("http", "ws", 1) + "/ws/live"
ENGINE_GRPC = os.environ.get("BOYS_ENGINE_GRPC", "127.0.0.1:50071")
BRAIN_GRPC = os.environ.get("BOYS_BRAIN_GRPC", "127.0.0.1:50061")
DEMO = 1  # the seeded demo commitment the engine replays

CARRY_RATE = Decimal("0.15")
CHARITY_RATE = Decimal("0.10")

# Milestone proof fixtures (seeded AI mode checks bytes only): the good one has no "cropped" marker → approve;
# the cropped one embeds the marker → insufficient.
_PNG = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")
GOOD_EVIDENCE = base64.b64encode(_PNG).decode()
CROPPED_EVIDENCE = base64.b64encode(_PNG + b"cropped").decode()


def round_half_even(value: Decimal) -> int:
    return int(value.quantize(Decimal("1"), rounding=ROUND_HALF_EVEN))


def carry_of(gain: int) -> int:
    """15% carry on positive gain, banker's-rounded; 0 if the gain isn't positive. Mirrors SettlementCalculator."""
    return round_half_even(Decimal(gain) * CARRY_RATE) if gain > 0 else 0


def charity_of(principal: int) -> int:
    return round_half_even(Decimal(principal) * CHARITY_RATE)


class Api:
    """Thin client for the public REST API through nginx. httpx frames body-less POSTs with Content-Length: 0."""

    def __init__(self) -> None:
        self._c = httpx.Client(base_url=API, timeout=20)

    def close(self) -> None:
        self._c.close()

    def _json(self, r: httpx.Response) -> dict:
        r.raise_for_status()
        return r.json()

    def create_goal(self, goal_text: str, drive_mode: str = "AUTO", stake: int = 10000,
                    charity: int = 1, deadline: str = None, milestones: list[dict] = None) -> dict:
        body = {"goalText": goal_text, "stakeCents": stake, "charityId": charity,
                "driveMode": drive_mode, "deadline": deadline, "milestones": milestones}
        return self._json(self._c.post("/goals", json=body))

    def get_goal(self, gid: int = DEMO) -> dict:
        return self._json(self._c.get(f"/goals/{gid}"))

    def milestones(self, gid: int = DEMO) -> list[dict]:
        return self.get_goal(gid)["milestones"]

    def state(self, gid: int = DEMO) -> str:
        return self.get_goal(gid)["state"]

    def activate(self, gid: int = DEMO) -> dict:
        return self._json(self._c.post(f"/goals/{gid}/activate"))

    def proof(self, milestone_id: int, key: str, evidence: str = GOOD_EVIDENCE, claim: str = "on track",
              gid: int = DEMO) -> dict:
        body = {"milestoneId": milestone_id, "claim": claim, "evidenceBase64": evidence,
                "mime": "image/png", "idempotencyKey": key}
        return self._json(self._c.post(f"/goals/{gid}/proof", json=body))

    def decide(self, milestone_id: int, decision: str, key: str) -> dict:
        body = {"decision": decision, "idempotencyKey": key}
        return self._json(self._c.post(f"/milestones/{milestone_id}/decision", json=body,
                                       headers={"X-User-Id": "2"}))

    def ride(self, gid: int = DEMO) -> dict:
        return self._json(self._c.post(f"/goals/{gid}/ride"))

    def cashout(self, gid: int = DEMO) -> dict:
        return self._json(self._c.post(f"/goals/{gid}/cashout"))

    def succeed(self, gid: int = DEMO) -> dict:
        return self._json(self._c.post(f"/goals/{gid}/succeed"))

    def settle(self, gid: int = DEMO) -> dict:
        return self._json(self._c.post(f"/goals/{gid}/settle"))

    def receipt(self, gid: int = DEMO) -> dict:
        return self._json(self._c.get(f"/goals/{gid}/receipt"))

    def valuation(self, gid: int = DEMO) -> dict:
        return self._json(self._c.get(f"/goals/{gid}/valuation"))

    def raw_post(self, path: str, **kw) -> httpx.Response:
        return self._c.post(path, **kw)


# --- one leg of the milestone dance: proof (good) → referee approve. Returns the resulting commitment state.
def clear_leg(api: Api, milestone_id: int, tag: str) -> str:
    api.proof(milestone_id, key=f"proof-{tag}")
    return api.decide(milestone_id, "approve", key=f"dec-{tag}")["commitmentState"]


# --- ledger DB access (for the deadline gate and pool/escrow snapshots) ---
def _sa_password() -> str:
    for line in (ROOT / ".env").read_text().splitlines():
        if line.startswith("MSSQL_SA_PASSWORD="):
            return line.split("=", 1)[1].strip()
    raise RuntimeError("MSSQL_SA_PASSWORD not in .env")


def sql(query: str) -> str:
    """Run a query in the boys-mssql container and return the trimmed scalar/first output."""
    out = subprocess.run(
        ["docker", "exec", "-i", "boys-mssql", "/opt/mssql-tools18/bin/sqlcmd",
         "-S", "localhost", "-U", "sa", "-P", _sa_password(), "-C", "-d", "boys",
         "-h", "-1", "-W", "-Q", f"SET NOCOUNT ON; {query}"],
        capture_output=True, text=True, check=True)
    return out.stdout.strip()


def account_balance(account: str) -> int:
    return int(sql(f"SELECT ISNULL(SUM(delta_cents),0) FROM ledger_postings WHERE account = '{account}'") or "0")


def run_script(name: str, env: dict | None = None) -> subprocess.CompletedProcess:
    return subprocess.run([str(ROOT / "scripts" / name)], capture_output=True, text=True, env=env)


def reconcile() -> None:
    r = run_script("reconcile.sh")
    assert "RECONCILE OK" in r.stdout, f"reconcile failed:\n{r.stdout}\n{r.stderr}"
