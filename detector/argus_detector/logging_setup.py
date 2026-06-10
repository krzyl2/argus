"""
Structured JSON logging setup for the Argus detector.

Provides configure_logging(level) which installs a JSON formatter on the root
logger.  Every log record is emitted as a single JSON line with keys:
  ts, level, logger, msg
plus any extra fields passed via the `extra` kwarg to logging calls.
"""

import json
import logging
import time
from typing import Any


class _JsonFormatter(logging.Formatter):
    """Format each log record as a compact JSON line."""

    def format(self, record: logging.LogRecord) -> str:  # noqa: A003
        # ISO-8601 timestamp (UTC)
        ts = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(record.created))

        payload: dict[str, Any] = {
            "ts": ts,
            "level": record.levelname,
            "logger": record.name,
            "msg": record.getMessage(),
        }

        # Merge any extra fields the caller passed in via logging.xxx(extra={...})
        skip = logging.LogRecord.__dict__.keys() | {
            "message", "asctime", "args", "msg", "created", "relativeCreated",
            "thread", "threadName", "process", "processName", "filename",
            "module", "funcName", "lineno", "pathname", "exc_info", "exc_text",
            "stack_info", "name", "levelname", "levelno", "msecs",
        }
        for key, value in record.__dict__.items():
            if key not in skip:
                payload[key] = value

        if record.exc_info:
            payload["exc"] = self.formatException(record.exc_info)

        return json.dumps(payload, ensure_ascii=False)


def configure_logging(level: str = "INFO") -> None:
    """
    Install a JSON formatter on the root logger.

    Call once at startup before any logging is done.  Subsequent calls are
    idempotent (only installs a handler if none is present yet).
    """
    numeric_level = getattr(logging, level.upper(), logging.INFO)

    root = logging.getLogger()
    root.setLevel(numeric_level)

    # Remove pre-existing handlers to avoid duplicate output
    root.handlers.clear()

    handler = logging.StreamHandler()
    handler.setFormatter(_JsonFormatter())
    root.addHandler(handler)
