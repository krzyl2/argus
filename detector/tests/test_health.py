"""
Test: Health RPC returns SERVING for argus.v1.DetectorService.

Uses an in-process insecure channel on an ephemeral port (no mTLS needed for unit tests).
"""

import threading
import time

import grpc
import pytest
from grpc_health.v1 import health_pb2, health_pb2_grpc

from argus_detector.proto import argus_pb2_grpc


def _find_free_port() -> int:
    import socket
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("", 0))
        return s.getsockname()[1]


@pytest.fixture(scope="module")
def running_server():
    """Start the detector server on an ephemeral port; yield (channel, port); stop after."""
    from argus_detector.server import create_server

    port = _find_free_port()
    server = create_server(port=port, tls=False)
    server.start()

    channel = grpc.insecure_channel(f"localhost:{port}")
    # Brief wait for server to be ready
    time.sleep(0.1)

    yield channel, port

    channel.close()
    server.stop(grace=0)


class TestHealthRpc:
    def test_health_check_returns_serving(self, running_server):
        """Health/Check for argus.v1.DetectorService must return SERVING."""
        channel, _ = running_server
        stub = health_pb2_grpc.HealthStub(channel)
        response = stub.Check(
            health_pb2.HealthCheckRequest(service="argus.v1.DetectorService")
        )
        assert response.status == health_pb2.HealthCheckResponse.SERVING

    def test_health_check_empty_service_returns_serving(self, running_server):
        """Health/Check with empty service name (overall health) must return SERVING."""
        channel, _ = running_server
        stub = health_pb2_grpc.HealthStub(channel)
        response = stub.Check(health_pb2.HealthCheckRequest(service=""))
        assert response.status == health_pb2.HealthCheckResponse.SERVING
