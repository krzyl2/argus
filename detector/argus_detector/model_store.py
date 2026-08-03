"""
ModelStore — versioned per-entity model persistence.

Directory layout:
  models/{entity_slug}/{detector}/v{N}/model.joblib   (PyOD models: MAD)
  models/{entity_slug}/{detector}/v{N}/model.pkl      (River HST models)
  models/{entity_slug}/{detector}/v{N}/version.json   (sidecar metadata)
  models/{entity_slug}/{detector}/latest              (contains int N, atomic write)

entity_slug = entity_id.replace('.', '_')   — caller responsibility
Retention: 3 most recent versions per (entity_slug, detector) — prune on save.

Serialization:
  PyOD (MAD): joblib.dump / joblib.load
  River HST:  pickle.dump / pickle.load
  PITFALL 3 (RESEARCH.md): River to_dict() does NOT exist — use pickle only.
  Group bundle (GroupMultivariateDetector): joblib.dump / joblib.load of the
    {"scaler": ..., "detector": ..., "name": ...} dict. Keyed under the
    group_ namespace slug (see group_slug()) — same versioned directory
    layout as PyOD models, reusing the identical *entity_slug*/{detector}/v{N}
    shape so save/load/prune/latest-pointer code paths are unchanged.

Atomic latest pointer: write to .tmp file then os.rename (guaranteed atomic on POSIX;
  on Windows, pathlib.Path.replace() uses MoveFileExW which is also atomic for same-volume).
"""

from __future__ import annotations

import json
import logging
import pathlib
import pickle
import shutil
from datetime import datetime, timezone

import grpc
import joblib
import pyod
import river

logger = logging.getLogger(__name__)

MODEL_ROOT = pathlib.Path("/var/argus/models")
_KEEP_VERSIONS = 3


def group_slug(group_id: str) -> str:
    """Build the group_ namespaced directory slug for a group_id.

    This is the SOLE collision-avoidance mechanism between group model keys
    and per-entity model keys (RESEARCH.md Pitfall 5) — never string-format
    the "group_" prefix ad hoc anywhere else; always call this helper.
    """
    return f"group_{group_id}"


class ModelStore:
    """Versioned model persistence for PyOD and River models.

    Usage::

        store = ModelStore()  # uses /var/argus/models
        # or:
        store = ModelStore(root=pathlib.Path("/tmp/test_models"))

        v = store.next_version("sensor_salon_temp", "mad")  # 1 on first call
        store.save_pyod("sensor_salon_temp", "mad", v, fitted_model)
        loaded = store.load_pyod("sensor_salon_temp", "mad")  # version=None → latest
    """

    def __init__(self, root: pathlib.Path = MODEL_ROOT) -> None:
        self._root = root

    # -------------------------------------------------------------------------
    # Public save / load API
    # -------------------------------------------------------------------------

    def save_pyod(
        self,
        entity_slug: str,
        detector: str,
        version: int,
        model: object,
        entity_id: str | None = None,
    ) -> None:
        """Persist a PyOD model (joblib format) with version sidecar.

        Creates the versioned directory, dumps the model, writes version.json,
        writes entity_id.txt sidecar (for unambiguous key reconstruction in
        load_all_into — CR-02), updates the latest pointer atomically, and prunes
        old versions.

        Args:
            entity_slug: entity_id with '.' replaced by '_'.
            detector: Detector name (e.g. "mad").
            version: Integer version number (monotonically increasing).
            model: Fitted PyOD model instance.
            entity_id: Original entity_id (dots intact). When provided, written as
                       a sidecar so load_all_into can reconstruct the correct key.
                       Falls back to entity_slug when None (backwards compatibility).
        """
        d = self._model_dir(entity_slug, detector, version)
        d.mkdir(parents=True, exist_ok=True)
        joblib.dump(model, d / "model.joblib")
        self._write_version_json(d, entity_slug, detector, version)
        self._write_entity_id(d, entity_id if entity_id is not None else entity_slug)
        self._update_latest(entity_slug, detector, version)
        self._prune(entity_slug, detector)

    def save_river(
        self,
        entity_slug: str,
        detector: str,
        version: int,
        model: object,
        entity_id: str | None = None,
    ) -> None:
        """Persist a River model (pickle format) with version sidecar.

        Args:
            entity_slug: entity_id with '.' replaced by '_'.
            detector: Detector name (e.g. "hst").
            version: Integer version number.
            model: River model instance (e.g. HalfSpaceTrees).
            entity_id: Original entity_id (dots intact). When provided, written as
                       a sidecar so load_all_into can reconstruct the correct key.
                       Falls back to entity_slug when None (backwards compatibility).

        Note:
            River to_dict()/from_dict() do NOT exist for anomaly detectors (RESEARCH.md
            Pitfall 3). Pickle is the canonical River serialization method (riverml.xyz/faq).
        """
        d = self._model_dir(entity_slug, detector, version)
        d.mkdir(parents=True, exist_ok=True)
        with open(d / "model.pkl", "wb") as f:
            pickle.dump(model, f)
        self._write_version_json(d, entity_slug, detector, version)
        self._write_entity_id(d, entity_id if entity_id is not None else entity_slug)
        self._update_latest(entity_slug, detector, version)
        self._prune(entity_slug, detector)

    def save_group_bundle(
        self,
        group_id: str,
        detector: str,
        version: int,
        bundle: dict,
    ) -> None:
        """Persist a group multivariate model bundle (joblib format) with version sidecar.

        Mirrors save_pyod exactly, but keys the versioned directory under the
        group_ namespace slug (group_slug()) instead of a raw entity_slug —
        the sole mechanism guaranteeing no collision with per-entity model
        keys (RESEARCH.md Pitfall 5).

        Args:
            group_id: Group identifier (not yet namespaced with "group_").
            detector: Detector name (e.g. "ecod").
            version: Integer version number (monotonically increasing).
            bundle: dict from GroupMultivariateDetector.bundle()
                    ({"scaler": ..., "detector": ..., "name": ...}).
        """
        slug = group_slug(group_id)
        d = self._model_dir(slug, detector, version)
        d.mkdir(parents=True, exist_ok=True)
        joblib.dump(bundle, d / "model.joblib")
        self._write_version_json(d, slug, detector, version)
        self._write_entity_id(d, slug)
        self._update_latest(slug, detector, version)
        self._prune(slug, detector)

    def load_group_bundle(
        self,
        group_id: str,
        detector: str,
        version: int | None = None,
    ) -> dict:
        """Load a group multivariate model bundle from disk.

        Args:
            group_id: Group identifier (not yet namespaced with "group_").
            detector: Detector name.
            version: Specific version to load. None (default) → load latest.

        Returns:
            The persisted {"scaler": ..., "detector": ..., "name": ...} dict.
        """
        slug = group_slug(group_id)
        v = version if version is not None else self._read_latest(slug, detector)
        return joblib.load(self._model_dir(slug, detector, v) / "model.joblib")

    def load_pyod(
        self,
        entity_slug: str,
        detector: str,
        version: int | None = None,
    ) -> object:
        """Load a PyOD model from disk.

        Args:
            entity_slug: Directory slug.
            detector: Detector name.
            version: Specific version to load. None (default) → load latest.

        Returns:
            Deserialized model object.
        """
        v = version if version is not None else self._read_latest(entity_slug, detector)
        return joblib.load(self._model_dir(entity_slug, detector, v) / "model.joblib")

    def load_river(
        self,
        entity_slug: str,
        detector: str,
        version: int | None = None,
    ) -> object:
        """Load a River model from disk (pickle).

        Args:
            entity_slug: Directory slug.
            detector: Detector name.
            version: Specific version to load. None (default) → load latest.

        Returns:
            Deserialized River model object.
        """
        v = version if version is not None else self._read_latest(entity_slug, detector)
        with open(self._model_dir(entity_slug, detector, v) / "model.pkl", "rb") as f:
            # T-02-03-01 (accepted risk): pickle.load executes arbitrary Python embedded in
            # the file. This is accepted for the single-operator self-hosted deployment where
            # /var/argus/models is writable only by the detector process. See threat model.
            return pickle.load(f)

    def save_checkpoint(
        self,
        entity_slug: str,
        detector: str,
        model: object,
        entity_id: str,
        n_seen: int,
    ) -> None:
        """Persist a streaming detector's checkpoint (pickle) with a JSON sidecar.

        Lives OUTSIDE the versioned v{N} layout (D-02) — a single overwritten
        checkpoint.pkl/checkpoint.json pair per (slug, detector): no
        next_version, no version.json, no _prune. Both files are written via
        .tmp + Path.replace() (D-07), mirroring _update_latest; the two
        renames are independent — a mismatched pair is caught by
        river_version validation on load, so cross-file atomicity is not
        needed (D-03).

        Args:
            entity_slug: entity_id with '.' replaced by '_'.
            detector: Detector name (e.g. "hst").
            model: The (already deep-copied) detector instance to pickle.
            entity_id: Original entity_id (dots intact) — written into sidecar.
            n_seen: Current reading count — written into sidecar.
        """
        d = self._checkpoint_dir(entity_slug, detector)
        d.mkdir(parents=True, exist_ok=True)

        tmp_pkl = d / "checkpoint.pkl.tmp"
        with open(tmp_pkl, "wb") as f:
            pickle.dump(model, f)
        tmp_pkl.replace(d / "checkpoint.pkl")  # atomic on POSIX; MoveFileExW on Windows

        sidecar = {
            "entity_id": entity_id,
            "detector": detector,
            "n_seen": n_seen,
            "river_version": river.__version__,
            "saved_at": datetime.now(timezone.utc).isoformat(),
        }
        tmp_json = d / "checkpoint.json.tmp"
        tmp_json.write_text(json.dumps(sidecar))
        tmp_json.replace(d / "checkpoint.json")

    def load_checkpoint(
        self,
        entity_slug: str,
        detector: str,
    ) -> tuple[object, dict] | None:
        """Load a streaming detector's checkpoint (pickle + sidecar) from disk.

        Returns None (not an exception) when no checkpoint exists for
        (entity_slug, detector), or when the sidecar's river_version does not
        match the installed river version (D-03) — a version mismatch
        discards this one checkpoint with a WARN; other entities/checkpoints
        are unaffected.

        Args:
            entity_slug: Directory slug.
            detector: Detector name.

        Returns:
            (model, sidecar) tuple, or None.
        """
        d = self._checkpoint_dir(entity_slug, detector)
        pkl_path = d / "checkpoint.pkl"
        json_path = d / "checkpoint.json"
        if not pkl_path.exists() or not json_path.exists():
            return None

        sidecar = json.loads(json_path.read_text())
        if sidecar.get("river_version") != river.__version__:
            logger.warning(
                "Checkpoint river_version mismatch for slug=%s detector=%s "
                "(checkpoint=%s, installed=%s); discarding checkpoint",
                entity_slug, detector, sidecar.get("river_version"), river.__version__,
            )
            return None

        with open(pkl_path, "rb") as f:
            # T-02-03-01 (accepted risk): see load_river for rationale.
            model = pickle.load(f)
        return model, sidecar

    def next_version(self, entity_slug: str, detector: str) -> int:
        """Return the next version number for (entity_slug, detector).

        Returns 1 if no model exists yet, or latest + 1 otherwise.
        """
        latest_file = self._root / entity_slug / detector / "latest"
        if not latest_file.exists():
            return 1
        return self._read_latest(entity_slug, detector) + 1

    def load_all_into(self, registry: object) -> None:
        """Load all persisted models from disk into the registry (MDL-03).

        Scans MODEL_ROOT for */*/latest files. For each found (slug, detector)
        pair, loads the latest model and calls registry.register(slug, detector, model).

        Non-fatal: logs a warning on load failure and continues with other models.

        Args:
            registry: Object with a register(entity_slug, detector, model) method.
                      Plan 05 implements registry.register() to set the registry entry
                      directly without re-fitting.
        """
        if not self._root.exists():
            logger.info("MODEL_ROOT %s does not exist; no models to load", self._root)
            return

        for latest_file in self._root.glob("*/*/latest"):
            slug = latest_file.parent.parent.name
            detector = latest_file.parent.name
            try:
                version = int(latest_file.read_text().strip())
                model_dir = self._model_dir(slug, detector, version)

                # CR-02: read the entity_id sidecar to get the unambiguous registry key.
                # Falls back to slug for models saved before this sidecar was introduced.
                entity_id_file = model_dir / "entity_id.txt"
                entity_id = entity_id_file.read_text().strip() if entity_id_file.exists() else slug

                if (model_dir / "model.joblib").exists():
                    model = joblib.load(model_dir / "model.joblib")
                elif (model_dir / "model.pkl").exists():
                    with open(model_dir / "model.pkl", "rb") as f:
                        # T-02-03-01 (accepted risk): see load_river for rationale.
                        model = pickle.load(f)
                else:
                    logger.warning(
                        "No model file found in %s; skipping (slug=%s, detector=%s, v=%d)",
                        model_dir, slug, detector, version,
                    )
                    continue

                registry.register(entity_id, detector, model)
                logger.info(
                    "Loaded model: entity_id=%s detector=%s version=%d", entity_id, detector, version
                )
            except Exception:
                logger.warning(
                    "Failed to load model for slug=%s detector=%s; skipping",
                    slug, detector,
                    exc_info=True,
                )

        # D-09 / RESEARCH.md Pitfall 2: this checkpoint glob pass MUST run
        # strictly AFTER the "latest" glob pass above. registry.register()/
        # register_checkpoint() unconditionally overwrite the dict entry, so
        # running this pass second is the ordering mechanism that makes a
        # checkpoint (D-01's source of truth for streaming state) always win
        # over any stale versioned artifact for the same (slug, detector) key
        # — do not leave this implicit in glob call order elsewhere.
        for checkpoint_file in self._root.glob("*/*/checkpoint.pkl"):
            slug = checkpoint_file.parent.parent.name
            detector = checkpoint_file.parent.name
            try:
                result = self.load_checkpoint(slug, detector)
                if result is None:
                    continue
                model, sidecar = result
                entity_id = sidecar.get("entity_id", slug)
                n_seen = sidecar.get("n_seen", 0)
                registry.register_checkpoint(entity_id, detector, model, n_seen)
                logger.info(
                    "Loaded checkpoint: entity_id=%s detector=%s n_seen=%d",
                    entity_id, detector, n_seen,
                )
            except Exception:
                logger.warning(
                    "Failed to load checkpoint for slug=%s detector=%s; skipping",
                    slug, detector,
                    exc_info=True,
                )

    # -------------------------------------------------------------------------
    # Private helpers
    # -------------------------------------------------------------------------

    def _model_dir(self, slug: str, detector: str, version: int) -> pathlib.Path:
        return self._root / slug / detector / f"v{version}"

    def _checkpoint_dir(self, slug: str, detector: str) -> pathlib.Path:
        """Directory for a streaming checkpoint — deliberately OUTSIDE the
        versioned v{N} layout (D-02): no next_version, no version.json,
        no _prune ever runs on this path.
        """
        return self._root / slug / detector

    def _write_entity_id(self, d: pathlib.Path, entity_id: str) -> None:
        """Write entity_id sidecar for unambiguous key reconstruction on load (CR-02)."""
        (d / "entity_id.txt").write_text(entity_id)

    def _write_version_json(
        self,
        d: pathlib.Path,
        entity_slug: str,
        detector: str,
        version: int,
    ) -> None:
        meta = {
            "version": version,
            "entity_id": entity_slug,   # slug form; caller maps entity_id → slug
            "detector": detector,
            "created_at": datetime.now(timezone.utc).isoformat(),
            "grpcio_version": grpc.__version__,
            "pyod_version": pyod.__version__,
            "river_version": river.__version__,
        }
        (d / "version.json").write_text(json.dumps(meta))

    def _update_latest(self, slug: str, detector: str, version: int) -> None:
        """Atomically update the latest pointer file.

        Writes to a .tmp file then renames, ensuring no reader sees a partial
        state even if the process is interrupted mid-write.
        """
        latest = self._root / slug / detector / "latest"
        tmp = latest.with_suffix(".tmp")
        tmp.write_text(str(version))
        tmp.replace(latest)  # atomic on POSIX; MoveFileExW on Windows

    def _read_latest(self, slug: str, detector: str) -> int:
        latest = self._root / slug / detector / "latest"
        return int(latest.read_text().strip())

    def _prune(self, slug: str, detector: str) -> None:
        """Remove old version directories, keeping the _KEEP_VERSIONS most recent."""
        base = self._root / slug / detector
        versions = sorted(
            [
                int(d.name[1:])
                for d in base.iterdir()
                if d.is_dir() and d.name.startswith("v")
            ],
            reverse=True,
        )
        for old_v in versions[_KEEP_VERSIONS:]:
            shutil.rmtree(base / f"v{old_v}", ignore_errors=True)
