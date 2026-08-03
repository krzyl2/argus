"""
Argus detector gRPC server entry point.

Usage:
    python -m argus_detector.server       # reads config from environment
    create_server(port, tls)              # factory for unit tests

Threat mitigations:
  T-02-01: add_secure_port with mTLS (require_client_auth=True) when TLS config present.
           Insecure port only when all three TLS paths are absent (unit tests / dev).
  MDL-03: health set to NOT_SERVING before model load; SERVING only after load_all_into completes.
  D-08: SIGTERM flushes every dirty streaming checkpoint before server.stop(grace).
"""

import logging
import pathlib
import signal
from concurrent import futures

import grpc
from grpc_health.v1 import health, health_pb2, health_pb2_grpc

from argus_detector.checkpoint_writer import CheckpointWriter
from argus_detector.config import DetectorConfig
from argus_detector.logging_setup import configure_logging
from argus_detector.model_store import MODEL_ROOT, ModelStore
from argus_detector.proto import argus_pb2_grpc
from argus_detector.registry import DetectorRegistry
from argus_detector.servicer import DetectorServicer

logger = logging.getLogger(__name__)

# D-08/Pitfall 5: no timeout-kill/timeout-finish file exists under
# argus/rootfs/etc/services.d/detector/ and no S6_KILL_GRACETIME is set in
# argus/Dockerfile (grepped — RESEARCH.md Assumption A1) — the actual s6
# kill-grace budget is unverified. 5s is chosen because the flush path itself
# is fast (a handful of entities' pickles, sub-second once the per-entity
# yield bounds deepcopy cost — RESEARCH.md Pitfall 1), not because a 5s grace
# window is assumed to exist: the goal is staying comfortably inside whatever
# budget s6 actually allows, not depending on a precise number.
_SIGTERM_GRACE_SEC = 5.0
_SIGTERM_WAIT_SEC = 5.0


def create_server(
    port: int = 50051,
    tls: bool | None = None,
    config: DetectorConfig | None = None,
    model_root: pathlib.Path | None = None,
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
    model_root:
        Override MODEL_ROOT for testing (allows tmp_path injection).
        None = use MODEL_ROOT (/var/argus/models).

    Returns
    -------
    grpc.Server (not yet started — call server.start() yourself).
    """
    if config is None:
        config = DetectorConfig()

    use_tls = config.mtls_enabled if tls is None else tls

    # Build server
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=10))

    # Register grpc.health.v1 Health service
    health_servicer = health.HealthServicer()
    health_pb2_grpc.add_HealthServicer_to_server(health_servicer, server)

    # MDL-03: set NOT_SERVING while loading models from disk
    health_servicer.set("argus.v1.DetectorService", health_pb2.HealthCheckResponse.NOT_SERVING)
    health_servicer.set("", health_pb2.HealthCheckResponse.NOT_SERVING)

    # Load all saved models before accepting traffic (MDL-03 startup gate)
    registry = DetectorRegistry()
    root = model_root if model_root is not None else MODEL_ROOT
    model_store = ModelStore(root=root)
    model_store.load_all_into(registry)  # non-fatal: logs warnings on failure; no-op if root absent

    # D-05: build the checkpoint writer before the SERVING transition; attach
    # for test introspection (mirrors _argus_registry). interval_sec=0 when
    # checkpoint_enabled is False expresses both knobs through one branch —
    # start() is never called here (serve() owns the start/stop lifecycle).
    checkpoint_interval = config.checkpoint_interval_sec if config.checkpoint_enabled else 0
    checkpoint_writer = CheckpointWriter(registry, model_store, checkpoint_interval)
    server._argus_checkpoint_writer = checkpoint_writer

    # Register DetectorService (servicer now receives model_store)
    servicer = DetectorServicer(registry, model_store)
    argus_pb2_grpc.add_DetectorServiceServicer_to_server(servicer, server)

    # MDL-03: set SERVING only after all models are loaded
    health_servicer.set("argus.v1.DetectorService", health_pb2.HealthCheckResponse.SERVING)
    health_servicer.set("", health_pb2.HealthCheckResponse.SERVING)

    # Expose registry for test introspection (RES-02: verify startup model load)
    server._argus_registry = registry

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
        server.add_secure_port(f"{config.grpc_bind}:{port}", server_credentials)
        logger.info(
            "detector listening",
            extra={"port": port, "mtls": True},
        )
    else:
        server.add_insecure_port(f"{config.grpc_bind}:{port}")
        logger.info(
            "detector listening",
            extra={"port": port, "mtls": False},
        )

    return server


def _install_sigterm_handler(server: grpc.Server, writer: CheckpointWriter) -> None:
    """Install a SIGTERM handler that flushes dirty checkpoints before stopping (D-08).

    s6's run script uses `exec python3 -m argus_detector.server` (confirmed in
    argus/rootfs/etc/services.d/detector/run), so this process is the direct
    signal target — main-thread handler registration is sufficient
    (RESEARCH.md Assumption A2).
    """

    def _handle_sigterm(signum, frame) -> None:
        try:
            count = writer.flush()
            logger.info("SIGTERM received: flushed %d dirty checkpoint(s)", count)
        except Exception:
            logger.warning("SIGTERM: checkpoint flush failed", exc_info=True)
        stop_event = server.stop(grace=_SIGTERM_GRACE_SEC)
        stop_event.wait(_SIGTERM_WAIT_SEC)  # bounded — never an unbounded wait

    signal.signal(signal.SIGTERM, _handle_sigterm)


def serve() -> None:
    """Start the server using environment-based config and block until terminated."""
    config = DetectorConfig()
    configure_logging(config.log_level)

    server = create_server(port=config.grpc_port, config=config, model_root=pathlib.Path(config.model_root))
    writer: CheckpointWriter = server._argus_checkpoint_writer

    _install_sigterm_handler(server, writer)

    server.start()
    writer.start()
    logger.info("detector started", extra={"port": config.grpc_port})
    server.wait_for_termination()
    writer.stop()


if __name__ == "__main__":
    serve()
