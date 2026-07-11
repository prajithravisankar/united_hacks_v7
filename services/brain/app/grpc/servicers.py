"""gRPC servicers for the brain. QuantServicer is real (B09); RefereeServicer is B10."""

from __future__ import annotations

from typing import Any

import grpc

from app.quant import repo
from app.quant.engine import (
    AUTO,
    USER,
    CommitmentNotFound,
    InvalidRequest,
    MarketNotFound,
    QuantEngine,
)
from boys.brain.v1 import quant_pb2, quant_pb2_grpc, referee_pb2_grpc
from boys.common.v1 import money_pb2

MAX_USER_STAKE_CENTS = 100_000  # demo cap for a user-driven bet


def _money(cents: int) -> money_pb2.Money:
    return money_pb2.Money(cents=cents, currency="USD")


def _drive(mode: int) -> str:
    return USER if mode == quant_pb2.DRIVE_MODE_USER else AUTO


class QuantServicer(quant_pb2_grpc.QuantServiceServicer):  # type: ignore[misc]
    def __init__(self, engine: QuantEngine | None = None):
        self._engine = engine  # injectable for tests; else lazy-loaded from Oracle

    def _eng(self) -> QuantEngine:
        if self._engine is None:
            self._engine = repo.load_engine()
        return self._engine

    def GetValuation(self, request: Any, context: Any) -> Any:
        try:
            v = self._eng().get_valuation(
                request.commitment_id,
                request.principal_cents,
                request.start_date,
                request.as_of,
                _drive(request.drive_mode),
            )
            return quant_pb2.Valuation(
                nav=_money(v.nav_cents),
                principal=_money(v.principal_cents),
                gain=_money(v.gain_cents),
                carry_preview=_money(v.carry_cents),
                user_take_home=_money(v.take_home_cents),
            )
        except InvalidRequest as exc:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, str(exc))
        except CommitmentNotFound as exc:
            context.abort(grpc.StatusCode.NOT_FOUND, str(exc))

    def GetNavCurve(self, request: Any, context: Any) -> Any:
        try:
            pts = self._eng().get_nav_curve(
                request.commitment_id,
                request.principal_cents,
                request.start_date,
                request.end_date,
                _drive(request.drive_mode),
            )
            return quant_pb2.NavCurve(
                commitment_id=request.commitment_id,
                points=[quant_pb2.NavPoint(date=d, nav=_money(n)) for d, n in pts],
            )
        except CommitmentNotFound as exc:
            context.abort(grpc.StatusCode.NOT_FOUND, str(exc))

    def ProjectOutcomes(self, request: Any, context: Any) -> Any:
        try:
            p = self._eng().project_outcomes(
                request.commitment_id,
                request.principal_cents,
                request.start_date,
                request.as_of,
                _drive(request.drive_mode),
            )
            return quant_pb2.Projection(
                cash_now=_money(p.cash_now_cents),
                ride_p10=_money(p.ride_p10_cents),
                ride_p50=_money(p.ride_p50_cents),
                ride_p90=_money(p.ride_p90_cents),
            )
        except InvalidRequest as exc:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, str(exc))
        except CommitmentNotFound as exc:
            context.abort(grpc.StatusCode.NOT_FOUND, str(exc))

    def ListOpenMarkets(self, request: Any, context: Any) -> Any:
        markets = self._eng().list_open_markets(request.as_of)
        return quant_pb2.OpenMarkets(
            markets=[
                quant_pb2.Market(
                    market_id=m.market_id,
                    description=m.description,
                    implied_prob=m.implied_prob,
                    model_prob=m.implied_prob,
                )
                for m in markets
            ]
        )

    def PlaceUserBet(self, request: Any, context: Any) -> Any:
        try:
            self._eng().place_user_bet(
                request.commitment_id,
                request.market_id,
                request.side,
                request.stake.cents,
                MAX_USER_STAKE_CENTS,
            )
            return quant_pb2.BetAck(accepted=True, reason="")
        except MarketNotFound:
            return quant_pb2.BetAck(accepted=False, reason="unknown market")
        except InvalidRequest as exc:
            return quant_pb2.BetAck(accepted=False, reason=str(exc))


class RefereeServicer(referee_pb2_grpc.RefereeServiceServicer):  # type: ignore[misc]
    """Implemented in B10 (ValidateGoal, CheckProof)."""
