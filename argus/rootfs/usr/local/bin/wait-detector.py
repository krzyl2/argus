#!/usr/bin/env python3
"""
wait-detector.py — synchronous gRPC health poller for the Argus detector.

Usage (from s6 run script):
    python3 /usr/local/bin/wait-detector.py [addr] [service]

Defaults:
    addr    = 127.0.0.1:50051
    service = argus.v1.DetectorService

Exits 0 once the named service reports SERVING; loops with 1-second interval.
Module is importable without starting the poll loop (if __name__ guard).
"""

import sys
import time

import grpc
from grpc_health.v1 import health_pb2, health_pb2_grpc


def check_serving(addr: str, service: str, timeout: float = 2.0) -> bool:
    """
    Return True iff the gRPC health service at addr reports SERVING for service.

    Opens a fresh insecure channel, calls HealthStub.Check, and closes the
    channel regardless of the outcome.  Returns False on any exception or on a
    non-SERVING status.
    """
    channel = grpc.insecure_channel(addr)
    try:
        stub = health_pb2_grpc.HealthStub(channel)
        response = stub.Check(
            health_pb2.HealthCheckRequest(service=service),
            timeout=timeout,
        )
        return response.status == health_pb2.HealthCheckResponse.SERVING
    except Exception:  # noqa: BLE001
        return False
    finally:
        channel.close()


def wait_until_serving(
    addr: str,
    service: str,
    interval: float = 1.0,
    max_attempts: int | None = None,
) -> None:
    """
    Block until check_serving returns True.

    Sleeps *interval* seconds between attempts.  If max_attempts is given,
    raises RuntimeError after that many failures.
    """
    attempt = 0
    while True:
        if check_serving(addr, service):
            return
        attempt += 1
        if max_attempts is not None and attempt >= max_attempts:
            raise RuntimeError(
                f"Detector at {addr!r} not SERVING after {max_attempts} attempts"
            )
        time.sleep(interval)


def main(argv: list[str]) -> int:
    """Entry point; reads addr and service from argv."""
    addr = argv[1] if len(argv) > 1 else "127.0.0.1:50051"
    service = argv[2] if len(argv) > 2 else "argus.v1.DetectorService"
    wait_until_serving(addr, service)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
