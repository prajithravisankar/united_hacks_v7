"""B09 gRPC-level tests — status codes + the canonical valuation over the wire (fake engine, no Oracle)."""

from __future__ import annotations

from collections.abc import Iterator
from concurrent import futures

import grpc
import pytest

from app.grpc.servicers import QuantServicer
from app.quant.engine import QuantEngine
from boys.brain.v1 import quant_pb2, quant_pb2_grpc

CURVE = [("2023-01-01", 100_000), ("2023-06-01", 155_000)]


def _stub(engine: QuantEngine) -> Iterator[quant_pb2_grpc.QuantServiceStub]:
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    quant_pb2_grpc.add_QuantServiceServicer_to_server(QuantServicer(engine), server)
    port = server.add_insecure_port("[::]:0")
    server.start()
    channel = grpc.insecure_channel(f"localhost:{port}")
    try:
        yield quant_pb2_grpc.QuantServiceStub(channel)
    finally:
        channel.close()
        server.stop(None)


def test_valuation_over_grpc_canonical() -> None:
    for stub in _stub(QuantEngine(CURVE)):
        resp = stub.GetValuation(
            quant_pb2.GetValuationRequest(
                commitment_id="c1",
                principal_cents=10_000,
                start_date="2023-01-01",
                as_of="2023-06-01",
            )
        )
        assert resp.nav.cents == 15_500
        assert resp.user_take_home.cents == 14_675


def test_grpc_invalid_argument_when_as_of_before_start() -> None:
    for stub in _stub(QuantEngine(CURVE)):
        with pytest.raises(grpc.RpcError) as exc:
            stub.GetValuation(
                quant_pb2.GetValuationRequest(
                    commitment_id="c1",
                    principal_cents=10_000,
                    start_date="2023-06-01",
                    as_of="2023-01-01",
                )
            )
        assert exc.value.code() == grpc.StatusCode.INVALID_ARGUMENT


def test_grpc_not_found_when_before_data() -> None:
    for stub in _stub(QuantEngine(CURVE)):
        with pytest.raises(grpc.RpcError) as exc:
            stub.GetValuation(
                quant_pb2.GetValuationRequest(
                    commitment_id="c1",
                    principal_cents=10_000,
                    start_date="2020-01-01",
                    as_of="2020-02-01",
                )
            )
        assert exc.value.code() == grpc.StatusCode.NOT_FOUND


def _stub_lazy() -> Iterator[quant_pb2_grpc.QuantServiceStub]:
    # No injected engine -> the servicer lazy-loads from Oracle on first call.
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    quant_pb2_grpc.add_QuantServiceServicer_to_server(QuantServicer(), server)
    port = server.add_insecure_port("[::]:0")
    server.start()
    channel = grpc.insecure_channel(f"localhost:{port}")
    try:
        yield quant_pb2_grpc.QuantServiceStub(channel)
    finally:
        channel.close()
        server.stop(None)


def test_grpc_unavailable_when_oracle_down(monkeypatch: pytest.MonkeyPatch) -> None:
    # R3: Oracle failure must map to a generic UNAVAILABLE, never leak the driver/DSN string.
    from app.quant import repo

    def _boom() -> QuantEngine:
        raise RuntimeError("DPY-6005: cannot connect to 127.0.0.1:15211/FREEPDB1")

    monkeypatch.setattr(repo, "load_engine", _boom)  # servicers holds this same module
    for stub in _stub_lazy():
        with pytest.raises(grpc.RpcError) as exc:
            stub.GetValuation(
                quant_pb2.GetValuationRequest(
                    commitment_id="c1",
                    principal_cents=10_000,
                    start_date="2023-01-01",
                    as_of="2023-06-01",
                )
            )
        assert exc.value.code() == grpc.StatusCode.UNAVAILABLE
        assert "DPY-6005" not in exc.value.details()  # driver string never surfaced
        assert "15211" not in exc.value.details()
