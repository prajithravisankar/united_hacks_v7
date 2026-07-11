"""gRPC server wiring for the brain."""

from __future__ import annotations

from concurrent import futures

import grpc

from app.grpc.servicers import QuantServicer, RefereeServicer
from boys.brain.v1 import quant_pb2_grpc, referee_pb2_grpc


def create_server(port: int) -> tuple[grpc.Server, int]:
    """Build a gRPC server with both brain servicers. Returns (server, bound_port).

    Pass port=0 for an OS-assigned ephemeral port (used by tests).
    """
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=8))
    quant_pb2_grpc.add_QuantServiceServicer_to_server(QuantServicer(), server)
    referee_pb2_grpc.add_RefereeServiceServicer_to_server(RefereeServicer(), server)
    bound = server.add_insecure_port(f"[::]:{port}")
    return server, bound
