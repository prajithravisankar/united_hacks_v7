"""B03: proves the generated Python gRPC stubs import and construct."""

from boys.brain.v1 import quant_pb2, quant_pb2_grpc, referee_pb2
from boys.common.v1 import money_pb2
from boys.engine.v1 import engine_pb2


def test_contracts_construct() -> None:
    m = money_pb2.Money(cents=100, currency="USD")
    assert m.cents == 100
    _ = quant_pb2.NavPoint(nav=m)
    _ = quant_pb2_grpc.QuantServiceStub  # service stub generated
    _ = referee_pb2.GoalVerdict(verdict=referee_pb2.VERDICT_ACCEPT)
    _ = engine_pb2.ReplayState(running=True)
