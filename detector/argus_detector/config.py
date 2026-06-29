"""
Argus detector configuration.

Reads from environment variables; provides typed defaults.
No credentials are hard-coded (CONF-03 alignment).

Environment variables:
  ARGUS_GRPC_PORT    gRPC listen port          (default: 50051)
  ARGUS_TLS_CERT     Path to server certificate (default: None — insecure in unit tests)
  ARGUS_TLS_KEY      Path to server private key  (default: None)
  ARGUS_TLS_CA       Path to CA cert for mTLS client auth (default: None)
  ARGUS_LOG_LEVEL    Logging level              (default: INFO)
"""

import os


class DetectorConfig:
    """Typed config loaded from environment variables."""

    def __init__(self) -> None:
        self.grpc_port: int = int(os.environ.get("ARGUS_GRPC_PORT", "50051"))
        self.tls_cert: str | None = os.environ.get("ARGUS_TLS_CERT") or None
        self.tls_key: str | None = os.environ.get("ARGUS_TLS_KEY") or None
        self.tls_ca: str | None = os.environ.get("ARGUS_TLS_CA") or None
        self.log_level: str = os.environ.get("ARGUS_LOG_LEVEL", "INFO")

    @property
    def mtls_enabled(self) -> bool:
        """True when all three TLS paths are configured."""
        return bool(self.tls_cert and self.tls_key and self.tls_ca)
