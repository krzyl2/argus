"""
Deterministic tests for gen-entities.py output contract.

Host test dependency: PyYAML — install with: pip install pyyaml
In the add-on image, PyYAML is supplied by darts transitive dependencies (plan 03).

Run: python -m pytest tests/test_gen_entities.py -x -q
"""
import json
import os
import subprocess
import sys
import tempfile

import yaml


GEN_ENTITIES = os.path.join(
    os.path.dirname(__file__),
    "..", "argus", "rootfs", "usr", "local", "bin", "gen-entities.py",
)


def _run(options: dict) -> tuple[str, int]:
    """Write options to a temp file, run gen-entities.py, return (stdout, exit_code)."""
    with tempfile.NamedTemporaryFile(
        mode="w", suffix=".json", delete=False
    ) as f:
        json.dump(options, f)
        tmp_path = f.name
    try:
        result = subprocess.run(
            [sys.executable, GEN_ENTITIES, tmp_path],
            capture_output=True,
            text=True,
        )
        return result.stdout, result.returncode
    finally:
        os.unlink(tmp_path)


def test_gen_entities_minimal():
    """Two entities produce valid EntitiesConfigLoader YAML with hst detector and params == {}."""
    options = {
        "entities": ["sensor.foo", "sensor.bar"],
        "influx_measurement": "homeassistant",
        "influx_value_field": "value",
        "batch_interval_minutes": 10,
        "nightly_fit_hour": 2,
        "include_patterns": [],
        "exclude_patterns": [],
        "log_level": "info",
    }

    stdout, code = _run(options)
    assert code == 0, f"gen-entities.py exited {code}"

    cfg = yaml.safe_load(stdout)
    entities = cfg["entities"]

    assert len(entities) == 2, f"expected 2 entities, got {len(entities)}"

    entity_ids = [e["entity_id"] for e in entities]
    assert "sensor.foo" in entity_ids
    assert "sensor.bar" in entity_ids

    for entity in entities:
        assert len(entity["detectors"]) == 1, (
            f"entity {entity['entity_id']} must have exactly 1 detector"
        )
        det = entity["detectors"][0]
        assert det["name"] == "hst", f"expected detector name 'hst', got {det['name']!r}"
        assert det["params"] == {}, (
            f"params must be empty dict for default HST, got {det['params']!r}"
        )


def test_gen_entities_empty():
    """Empty entity list produces 'entities: []' and exits 0 (orchestrator fails loud on validate)."""
    options = {
        "entities": [],
        "influx_measurement": "homeassistant",
        "influx_value_field": "value",
        "batch_interval_minutes": 10,
        "nightly_fit_hour": 2,
        "include_patterns": [],
        "exclude_patterns": [],
        "log_level": "info",
    }

    stdout, code = _run(options)
    assert code == 0, f"gen-entities.py must exit 0 for empty list, got {code}"

    cfg = yaml.safe_load(stdout)
    # yaml.safe_load("entities: []") -> {"entities": []} or {"entities": None}
    # both represent an empty entity list; EntitiesConfigLoader.Validate() rejects both
    entities = cfg.get("entities") or []
    assert entities == [], f"expected empty list, got {entities!r}"
