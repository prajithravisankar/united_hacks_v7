"""gRPC servicers for the brain. B07 = stubs (UNIMPLEMENTED); real logic in B09/B10.

Unoverridden methods inherit the generated base which aborts with UNIMPLEMENTED,
so this skeleton already routes every RPC correctly.
"""

from __future__ import annotations

from boys.brain.v1 import quant_pb2_grpc, referee_pb2_grpc


class QuantServicer(quant_pb2_grpc.QuantServiceServicer):  # type: ignore[misc]
    """Implemented in B09 (GetNavCurve, GetValuation, ProjectOutcomes, ListOpenMarkets, PlaceUserBet)."""


class RefereeServicer(referee_pb2_grpc.RefereeServiceServicer):  # type: ignore[misc]
    """Implemented in B10 (ValidateGoal, CheckProof)."""
