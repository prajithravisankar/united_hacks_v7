"""Real-time beats: the live NAV tick stream through the nginx edge, and the "you-drive" divergence (a user
bet moves the USER-mode curve off the AUTO baseline)."""
import json
import uuid

import grpc
from websockets.sync.client import connect as ws_connect

from helpers import BRAIN_GRPC, ENGINE_GRPC, WS

from boys.brain.v1 import quant_pb2, quant_pb2_grpc  # noqa: E402  (path set in helpers)
from boys.common.v1 import money_pb2  # noqa: E402
from boys.engine.v1 import engine_pb2, engine_pb2_grpc  # noqa: E402

WINDOW = dict(start_date="2021-08-13", end_date="2024-05-19", principal_cents=10000)


def _engine():
    return engine_pb2_grpc.EngineServiceStub(grpc.insecure_channel(ENGINE_GRPC))


def _quant():
    return quant_pb2_grpc.QuantServiceStub(grpc.insecure_channel(BRAIN_GRPC))


def test_live_ws_ticks_stream_through_nginx():
    eng = _engine()
    with ws_connect(f"{WS}?goal=1", open_timeout=10) as ws:
        snap = json.loads(ws.recv(timeout=5))
        assert snap["type"] == "snapshot"
        assert isinstance(snap["navCents"], int)          # NAV always integer cents

        eng.StartReplay(engine_pb2.StartReplayRequest(commitment_id="1", speed=30))
        ticks = []
        for _ in range(120):
            m = json.loads(ws.recv(timeout=5))
            if m["type"] != "tick":
                continue
            assert isinstance(m["navCents"], int)          # never a float on the wire
            ticks.append(m["position"])
            if m.get("terminal"):                            # loop so we always collect enough
                eng.StartReplay(engine_pb2.StartReplayRequest(commitment_id="1", speed=30))
            if len(ticks) >= 8:
                break
        assert len(ticks) >= 8, f"only saw {len(ticks)} ticks through nginx"
    eng.Pause(engine_pb2.PauseRequest(commitment_id="1"))


def test_you_drive_user_bet_diverges_from_auto():
    # Use a fresh commitment key so the brain has no prior bet state (its user P&L is per-commitment, ephemeral)
    # — keeps this test re-runnable without a brain restart.
    gid = "e2e-drive-" + uuid.uuid4().hex[:8]
    q = _quant()

    def curve(mode):
        resp = q.GetNavCurve(quant_pb2.GetNavCurveRequest(commitment_id=gid, drive_mode=mode, **WINDOW))
        return [p.nav.cents for p in resp.points]

    auto = curve(quant_pb2.DriveMode.DRIVE_MODE_AUTO)
    assert auto, "empty nav curve — is the warehouse seeded?"
    assert curve(quant_pb2.DriveMode.DRIVE_MODE_USER) == auto   # no bet yet → USER == AUTO

    markets = q.ListOpenMarkets(quant_pb2.ListOpenMarketsRequest(as_of="2021-08-13"))
    assert markets.markets, "no open markets to bet on"
    ack = q.PlaceUserBet(quant_pb2.PlaceUserBetRequest(
        commitment_id=gid, market_id=markets.markets[0].market_id, side="yes",
        stake=money_pb2.Money(cents=2000)))
    assert ack.accepted, ack.reason

    assert curve(quant_pb2.DriveMode.DRIVE_MODE_USER) != auto   # the bet moved the USER curve off AUTO
