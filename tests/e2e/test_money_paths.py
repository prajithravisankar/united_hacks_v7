"""The three money stories the demo tells, plus the deadline path — every settlement asserted to the cent,
reconciliation clean after each. Drives the seeded demo commitment (id 1) through the API at the nginx edge."""
from decimal import Decimal

import httpx
import pytest

from helpers import (
    Api, account_balance, carry_of, charity_of, clear_leg, reconcile, round_half_even, sql,
)

PRINCIPAL = 10000


def test_golden_success_path(demo_reset, api: Api):
    ms = api.milestones()
    assert [m["state"] for m in ms] == ["pending", "pending", "pending"]

    # Three legs: clear + ride, clear + ride, clear + succeed.
    assert clear_leg(api, ms[0]["milestoneId"], "l1") == "milestone_cleared"
    assert api.ride()["state"] == "riding"
    assert clear_leg(api, ms[1]["milestoneId"], "l2") == "milestone_cleared"
    assert api.ride()["state"] == "riding"
    assert clear_leg(api, ms[2]["milestoneId"], "l3") == "milestone_cleared"
    assert api.succeed()["state"] == "succeeded"

    pool_before = account_balance("WINNERS_BONUS_POOL")
    r = api.settle()
    gain = r["navCents"] - PRINCIPAL
    carry = carry_of(gain)
    bonus_target = round_half_even(Decimal(PRINCIPAL) * Decimal("0.10"))
    bonus = min(bonus_target, max(0, pool_before))  # never over-draws the pool

    assert r["type"] == "Success"
    assert r["principalCents"] == PRINCIPAL
    assert r["gainCents"] == gain
    assert r["carryCents"] == carry
    assert r["charityCents"] == 0
    assert r["bonusCents"] == bonus
    assert r["takeHomeCents"] == PRINCIPAL + gain - carry + bonus
    assert api.receipt() == r          # the receipt is stable
    assert api.state() == "settled"
    reconcile()


def test_cashout_after_leg_one(demo_reset, api: Api):
    ms = api.milestones()
    assert clear_leg(api, ms[0]["milestoneId"], "l1") == "milestone_cleared"
    assert api.cashout()["state"] == "cashed_out"

    r = api.settle()
    gain = r["navCents"] - PRINCIPAL
    carry = carry_of(gain)
    take = max(PRINCIPAL, PRINCIPAL + gain - carry)  # floored at principal

    assert r["type"] == "CashOut"
    assert r["principalCents"] == PRINCIPAL
    assert r["gainCents"] == gain
    assert r["carryCents"] == carry
    assert r["charityCents"] == 0
    assert r["bonusCents"] == 0
    assert r["takeHomeCents"] == take
    assert api.state() == "settled"
    assert account_balance("USER_ESCROW") == 0   # escrow fully released
    reconcile()


def test_failure_path_referee_reject(demo_reset, api: Api):
    ms = api.milestones()
    api.proof(ms[0]["milestoneId"], key="pf")
    assert api.decide(ms[0]["milestoneId"], "reject", key="df")["commitmentState"] == "failed"

    # A dead commitment rejects further milestone actions.
    with pytest.raises(httpx.HTTPStatusError):
        api.proof(ms[1]["milestoneId"], key="pf2")

    r = api.settle()
    charity = charity_of(PRINCIPAL)
    assert r["type"] == "Failure"
    assert r["principalCents"] == PRINCIPAL
    assert r["charityCents"] == charity                 # 10% of principal
    assert r["takeHomeCents"] == PRINCIPAL - charity     # user keeps the exact remainder (90%)
    assert r["carryCents"] == 0
    assert r["bonusCents"] == 0
    assert account_balance("CHARITY_PAYABLE") == charity
    reconcile()


def test_cannot_succeed_before_final_leg(demo_reset, api: Api):
    # Clearing one of three milestones must NOT let a client jump to the winning terminal (and draw the
    # winners-pool bonus). Only /succeed after the final leg is allowed.
    ms = api.milestones()
    assert clear_leg(api, ms[0]["milestoneId"], "l1") == "milestone_cleared"
    with pytest.raises(httpx.HTTPStatusError) as exc:
        api.succeed()
    assert exc.value.response.status_code == 422
    assert api.state() == "milestone_cleared"   # unchanged — no early win


def test_deadline_expiry_is_a_failure(demo_reset, api: Api):
    # commitment 1 is active. Move it into the past (created_at too, to satisfy the deadline CHECK), then the
    # lazy gate trips it to failed on the next read — the faithful deadline miss, no clock hook needed.
    sql("UPDATE commitments SET created_at = DATEADD(DAY,-30,SYSUTCDATETIME()), "
        "deadline = DATEADD(DAY,-1,SYSUTCDATETIME()) WHERE commitment_id = 1")
    assert api.state() == "failed"                      # GET triggers the deadline gate
    assert any(e["command"] == "deadline_gate" for e in api.get_goal()["timeline"])

    r = api.settle()
    charity = charity_of(PRINCIPAL)
    assert r["type"] == "Failure"                        # identical settlement to a referee-confirmed miss
    assert r["charityCents"] == charity
    assert r["takeHomeCents"] == PRINCIPAL - charity
    reconcile()
