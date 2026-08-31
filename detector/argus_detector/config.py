"""
Argus detector configuration.

Reads from environment variables; provides typed defaults.
No credentials are hard-coded (CONF-03 alignment).

Environment variables:
  ARGUS_GRPC_PORT    gRPC listen port          (default: 50051)
  ARGUS_GRPC_BIND    gRPC bind address         (default: [::] — all interfaces, v1 default)
  ARGUS_TLS_CERT     Path to server certificate (default: None — insecure in unit tests)
  ARGUS_TLS_KEY      Path to server private key  (default: None)
  ARGUS_TLS_CA       Path to CA cert for mTLS client auth (default: None)
  ARGUS_LOG_LEVEL    Logging level              (default: INFO)
  ARGUS_MODEL_ROOT   Root directory for model storage (default: /var/argus/models)
  ARGUS_CHECKPOINT_INTERVAL_SEC  Streaming checkpoint sweep interval, seconds
                                 (default: 300; 0 disables the writer, D-05)
  ARGUS_CHECKPOINT_ENABLED       Enable the streaming checkpoint writer
                                 (default: true, D-05)
  ARGUS_GRPC_MAX_WORKERS  gRPC server thread-pool size (default: 64)

Note: ARGUS_BACKFILL_ENABLED/ARGUS_BACKFILL_LOOKBACK are orchestrator-side
(.NET ConnectionSettings) knobs, not detector-side — see 15-RESEARCH.md
Pitfall 3. They deliberately do NOT appear here.
"""

import os


class DetectorConfig:
    """Typed config loaded from environment variables."""

    def __init__(self) -> None:
        self.grpc_port: int = int(os.environ.get("ARGUS_GRPC_PORT", "50051"))
        self.grpc_bind: str = os.environ.get("ARGUS_GRPC_BIND", "[::]")
        self.tls_cert: str | None = os.environ.get("ARGUS_TLS_CERT") or None
        self.tls_key: str | None = os.environ.get("ARGUS_TLS_KEY") or None
        self.tls_ca: str | None = os.environ.get("ARGUS_TLS_CA") or None
        self.log_level: str = os.environ.get("ARGUS_LOG_LEVEL", "INFO")
        self.model_root: str = os.environ.get("ARGUS_MODEL_ROOT", "/var/argus/models")
        self.checkpoint_interval_sec: int = int(
            os.environ.get("ARGUS_CHECKPOINT_INTERVAL_SEC", "300")
        )
        self.checkpoint_enabled: bool = (
            os.environ.get("ARGUS_CHECKPOINT_ENABLED", "true").lower() == "true"
        )
        # One long-lived ScoreStream call permanently occupies one thread of the gRPC
        # server pool, so the pool must be larger than the number of watched entities —
        # otherwise Health/Check (and every new stream) starves behind them.
        self.grpc_max_workers: int = int(os.environ.get("ARGUS_GRPC_MAX_WORKERS", "64"))

    @property
    def mtls_enabled(self) -> bool:
        """True when all three TLS paths are configured."""
        return bool(self.tls_cert and self.tls_key and self.tls_ca)
