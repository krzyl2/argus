#!/usr/bin/env python3
"""
Converts /data/options.json entity list to /data/entities.yaml.

Input:  options.json   { "entities": ["sensor.foo", "sensor.bar"], ... }
Output: entities.yaml  matching EntitiesConfigLoader expected structure.

All entities get the rmad streaming detector with default params (params: {}) —
rmad is the default single-sensor detector (D-A); hst is the opt-in rollback path.

Both branches stamp schema_version: 2. The empty-list branch stamps it too: a
file without the stamp is rewritten by EntitiesSchemaMigrator on EVERY boot, and
every rewrite is a rename, i.e. a config Swap that resets every alert gate.
EntitiesConfigLoader.Validate() requires:
  - entities list non-empty
  - each entity_id non-empty
  - each entity has at least 1 detector

Host test dependency: PyYAML (pip install pyyaml).
In the add-on image, PyYAML is supplied by darts transitive dependencies (plan 03).

Security: yaml.dump() is used exclusively — never string-format YAML — so
untrusted entity_id strings from options.json are quoted/escaped safely (T-1-05).
"""
import json
import sys

import yaml  # PyYAML

options_path = sys.argv[1] if len(sys.argv) > 1 else "/data/options.json"

with open(options_path) as f:
    options = json.load(f)

entity_ids = options.get("entities", [])

if not entity_ids:
    # Empty list: write passthrough YAML that EntitiesConfigLoader.Validate()
    # will reject with a clear "contains no entities" error at startup.
    # gen-entities.py itself exits 0 — the orchestrator owns the hard failure.
    # Still stamped: an unstamped file is migrated (and rewritten) on every boot.
    print(yaml.dump({"schema_version": 2, "entities": []},
                    default_flow_style=False, allow_unicode=True, sort_keys=False))
    sys.exit(0)

config = {
    "schema_version": 2,
    "entities": [
        {
            "entity_id": eid,
            "friendly_name": "",
            "detectors": [
                {"name": "rmad", "params": {}}
            ],
        }
        for eid in entity_ids
    ],
}

print(yaml.dump(config, default_flow_style=False, allow_unicode=True, sort_keys=False))
