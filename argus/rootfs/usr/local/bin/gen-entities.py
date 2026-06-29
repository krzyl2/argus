#!/usr/bin/env python3
"""
Converts /data/options.json entity list to /data/entities.yaml.

Input:  options.json   { "entities": ["sensor.foo", "sensor.bar"], ... }
Output: entities.yaml  matching EntitiesConfigLoader expected structure.

All entities get the HST streaming detector with default params (params: {}).
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
    print("entities: []")
    sys.exit(0)

config = {
    "entities": [
        {
            "entity_id": eid,
            "friendly_name": "",
            "detectors": [
                {"name": "hst", "params": {}}
            ],
        }
        for eid in entity_ids
    ]
}

print(yaml.dump(config, default_flow_style=False, allow_unicode=True, sort_keys=False))
