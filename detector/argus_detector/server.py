"""
Argus detector gRPC server entry point.

Usage:
    python -m argus_detector.server       # reads config from environment
    create_server(port, tls)              # factory for unit tests

Threat mitigations:
  T-02-01: add_secure_port with mTLS (require_client_auth=True) when TLS config present.
           Insecure port only when all three TLS paths are absent (unit tests / dev).
"""

import logging
from concurrent import futures

import grpc
from grpc_health.v1 import health, health_pb2, health_pb2_grpc

from argus_detector.config import DetectorConfig
from argus_detector.logging_setup import configure_logging
from argus_detector.proto import argus_pb2_grpc
from argus_detector.registry import DetectorRegistry
from argus_detector.servicer import DetectorServicer

logger = logging.getLogger(__name__)


def create_server(
    port: int = 50051,
    tls: bool | None = None,
    config: DetectorConfig | None = None,
) -> grpc.Server:
    """
    Build and configure the gRPC server.

    Parameters
    ----------
    port:
        Port to bind.  Overrides config.grpc_port when provided.
    tls:
        True = require mTLS (reads cert/key/ca from config).
        False = insecure (unit tests, local dev).
        None = auto-detect from config.mtls_enabled.
    config:
        DetectorConfig instance.  If None a default instance is created
        (reads from environment).

    Returns
    -------
    grpc.Server (not yet started — call server.start() yourself).
    """
    if config is None:
        config = DetectorConfig()

    use_tls = config.mtls_enabled if tls is None else tls

    # Build server
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=10))

    # Register DetectorService
    registry = DetectorRegistry()
    servicer = DetectorServicer(registry)
    argus_pb2_grpc.add_DetectorServiceServicer_to_server(servicer, server)

    # Register grpc.health.v1 Health service
    health_servicer = health.HealthServicer()
    health_pb2_grpc.add_HealthServicer_to_server(health_servicer, server)

    # Set SERVING *after* registry init (mirrors Phase 2 contract: models loaded → SERVING)
    health_servicer.set("argus.v1.DetectorService", health_pb2.HealthCheckResponse.SERVING)
    health_servicer.set("", health_pb2.HealthCheckResponse.SERVING)

    if use_tls:
        # T-02-01: mTLS — load certs and require client certificate auth
        with open(config.tls_key, "rb") as f:
            private_key = f.read()
        with open(config.tls_cert, "rb") as f:
            certificate_chain = f.read()
        with open(config.tls_ca, "rb") as f:
            root_certificates = f.read()

        server_credentials = grpc.ssl_server_credentials(
            [(private_key, certificate_chain)],
            root_certificates=root_certificates,
            require_client_auth=True,
        )
        server.add_secure_port(f"[::]:{port}", server_credentials)
        logger.info(
            "detector listening",
            extra={"port": port, "mtls": True},
        )
    else:
        server.add_insecure_port(f"[::]:{port}")
        logger.info(
            "detector listening",
            extra={"port": port, "mtls": False},
        )

    return server


def serve() -> None:
    """Start the server using environment-based config and block until terminated."""
    config = DetectorConfig()
    configure_logging(config.log_level)

    server = create_server(port=config.grpc_port, config=config)
    server.start()
    logger.info("detector started", extra={"port": config.grpc_port})
    server.wait_for_termination()


if __name__ == "__main__":
    serve()
