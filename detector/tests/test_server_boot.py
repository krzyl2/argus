"""
Test: ScoreStream yields placeholder Verdict with correct fields.

Uses in-process insecure channel (same server fixture pattern as test_health.py).
"""

import threading
import time

import grpc
import pytest
from google.protobuf import wrappers_pb2

from argus_detector.proto import argus_pb2, argus_pb2_grpc


def _find_free_port() -> int:
    import socket
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("", 0))
        return s.getsockname()[1]


@pytest.fixture(scope="module")
def running_server():
    """Start the detector server on an ephemeral port; yield channel; stop after."""
    from argus_detector.server import create_server

    port = _find_free_port()
    server = create_server(port=port, tls=False)
    server.start()

    channel = grpc.insecure_channel(f"localhost:{port}")
    time.sleep(0.1)

    yield channel

    channel.close()
    server.stop(grace=0)


class TestScoreStream:
    def test_score_stream_yields_verdict_with_entity_id(self, running_server):
        """ScoreStream must yield at least one Verdict with the input entity_id."""
        channel = running_server
        stub = argus_pb2_grpc.DetectorServiceStub(channel)

        points = iter([
            argus_pb2.Point(
                entity_id="sensor.test",
                value=wrappers_pb2.DoubleValue(value=21.0),
            )
        ])

        verdicts = list(stub.ScoreStream(points))
        assert len(verdicts) >= 1
        assert verdicts[0].entity_id == "sensor.test"

    def test_score_stream_verdict_detector_is_hst(self, running_server):
        """ScoreStream Verdict.detector must be 'hst' (placeholder)."""
        channel = running_server
        stub = argus_pb2_grpc.DetectorServiceStub(channel)

        points = iter([
            argus_pb2.Point(
                entity_id="sensor.test",
                value=wrappers_pb2.DoubleValue(value=21.0),
            )
        ])

        verdicts = list(stub.ScoreStream(points))
        assert verdicts[0].detector == "hst"

    def test_score_stream_verdict_is_anomaly_false(self, running_server):
        """ScoreStream placeholder Verdict must have is_anomaly=False."""
        channel = running_server
        stub = argus_pb2_grpc.DetectorServiceStub(channel)

        points = iter([
            argus_pb2.Point(
                entity_id="sensor.test",
                value=wrappers_pb2.DoubleValue(value=21.0),
            )
        ])

        verdicts = list(stub.ScoreStream(points))
        assert verdicts[0].is_anomaly is False

    def test_score_stream_multiple_points(self, running_server):
        """ScoreStream must yield one Verdict per input Point."""
        channel = running_server
        stub = argus_pb2_grpc.DetectorServiceStub(channel)

        points = iter([
            argus_pb2.Point(entity_id="sensor.test", value=wrappers_pb2.DoubleValue(value=float(v)))
            for v in range(5)
        ])

        verdicts = list(stub.ScoreStream(points))
        assert len(verdicts) == 5
        for v in verdicts:
            assert v.entity_id == "sensor.test"
