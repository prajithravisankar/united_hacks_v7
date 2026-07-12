"""Resilience beats: graceful degradation under a brain outage, and the reset property (re-seeding restores a
byte-identical starting state — the demo-retake guarantee)."""
import os

from helpers import Api, account_balance, clear_leg, run_script, sql


def test_degradation_beat():
    # The kill/restart drill: the engine keeps streaming from its cached curve, broadcasts degraded, and
    # recovers — while the ledger degrades its valuation to 200/degraded:true, never a 500.
    r = run_script("test_degradation.sh", env=dict(os.environ, CYCLES="1"))
    assert r.returncode == 0, r.stdout + "\n" + r.stderr


def test_reset_restores_identical_state(api: Api):
    run_script("seed_demo.sh")
    before = _snapshot(api)

    # Mutate: clear the first leg and ride to the next.
    ms = api.milestones()
    clear_leg(api, ms[0]["milestoneId"], "mut")
    api.ride()
    assert _snapshot(api) != before          # sanity: the mutation really changed state

    run_script("seed_demo.sh")               # reset...
    assert _snapshot(api) == before          # ...restores the exact pristine starting state


def _snapshot(api: Api) -> dict:
    """The stable parts of the pristine scenario (dates are clock-relative, so excluded)."""
    goal = api.get_goal(1)
    return {
        "commitmentId": goal["commitmentId"],
        "state": goal["state"],
        "milestones": [(m["ordinal"], m["description"], m["targetMetric"], m["state"]) for m in goal["milestones"]],
        "escrowCents": account_balance("USER_ESCROW"),
        "poolCents": sql("SELECT pool_cents FROM community_pool_stats WHERE id = 1"),
    }
