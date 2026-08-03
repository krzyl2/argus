"""
CheckpointWriter — periodic dirty-checkpoint sweep thread for streaming detectors.

No existing Python precedent for a periodic background daemon thread exists in
this repo (15-PATTERNS.md "No Analog Found" — the detector process is purely
request-driven plus a one-shot startup load). Built directly on stdlib
threading.Event.wait(timeout), which is interruptible — required so stop() and
the SIGTERM-triggered flush() do not have to wait out a remaining interval.

D-05: interval_sec <= 0 disables the interval thread entirely (start() is a no-op).
D-08: flush() is the SIGTERM synchronous path — calls checkpoint_dirty directly,
      independent of whether the interval thread is running.
"""

from __future__ import annotations

import logging
import threading

logger = logging.getLogger(__name__)


class CheckpointWriter:
    """Runs registry.checkpoint_dirty(model_store) on an interruptible interval.

    Usage::

        writer = CheckpointWriter(registry, model_store, interval_sec=300)
        writer.start()
        ...
        writer.flush()   # synchronous — e.g. from a SIGTERM handler
        writer.stop()
    """

    def __init__(self, registry: object, model_store: object, interval_sec: float) -> None:
        self._registry = registry
        self._model_store = model_store
        self._interval_sec = interval_sec
        self._stop_event = threading.Event()
        self._thread: threading.Thread | None = None

    @property
    def is_running(self) -> bool:
        return self._thread is not None and self._thread.is_alive()

    def start(self) -> None:
        """Start the interval thread. No-op when interval_sec <= 0 (D-05)."""
        if self._interval_sec <= 0:
            return
        if self.is_running:
            return
        self._stop_event.clear()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def stop(self, timeout: float = 5.0) -> None:
        """Signal the interval thread to stop and join with a bounded timeout."""
        self._stop_event.set()
        if self._thread is not None:
            self._thread.join(timeout=timeout)

    def flush(self) -> int:
        """Synchronously run one checkpoint_dirty sweep (the SIGTERM path, D-08).

        Independent of whether the interval thread is running.
        """
        return self._registry.checkpoint_dirty(self._model_store)

    def _run(self) -> None:
        # Event.wait(timeout) is interruptible — stop()/flush() do not have to
        # wait out a remaining time.sleep(interval).
        while not self._stop_event.wait(self._interval_sec):
            try:
                self._registry.checkpoint_dirty(self._model_store)
            except Exception:
                logger.warning(
                    "checkpoint tick failed; will retry next interval", exc_info=True
                )
