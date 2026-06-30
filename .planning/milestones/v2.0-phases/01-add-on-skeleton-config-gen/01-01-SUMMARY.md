---
phase: 01-add-on-skeleton-config-gen
plan: "01"
subsystem: addon-metadata
tags: [ha-addon, config-yaml, repository-yaml, translations, icons, schema]
status: complete

dependency_graph:
  requires: []
  provides:
    - repository.yaml (HA custom repo manifest)
    - argus/config.yaml (add-on manifest + options/schema)
    - argus/translations/en.yaml
    - argus/translations/pl.yaml
    - argus/icon.png
    - argus/logo.png
    - tests/test_config_schema.py
  affects:
    - plan 01-02 (config-gen reads argus/config.yaml schema for env-var mapping)
    - plan 01-03 (Dockerfile COPYs argus/ subtree including icon/logo)

tech_stack:
  added:
    - HA add-on config.yaml manifest format
    - HA translations YAML format (configuration: mapping)
    - repository.yaml custom add-on repository manifest
  patterns:
    - password? schema type for Supervisor UI masking
    - int(min,max) bounds enforced by Supervisor before container starts
    - [str] list type with [] default for optional filter lists

key_files:
  created:
    - repository.yaml
    - argus/config.yaml
    - argus/translations/en.yaml
    - argus/translations/pl.yaml
    - argus/icon.png
    - argus/logo.png
    - tests/test_config_schema.py
  modified: []

decisions:
  - "influx_url through influx_bucket absent from options block (truly optional via schema ?-suffix)"
  - "detector_endpoint absent from options block for same reason"
  - "include_patterns/exclude_patterns use [str] schema with [] default — [str]? not valid HA schema"
  - "influx_token typed password? for Supervisor UI masking and log redaction (T-1-01)"
  - "batch_interval_minutes int(1,1440) and nightly_fit_hour int(0,23) enforce WR-04 bounds at Supervisor level (T-1-02)"
  - "Field grouping order: entities, patterns, influx, detector, schedule, log_level"

metrics:
  duration: "106s (~2m)"
  completed: "2026-06-29"
  tasks_completed: 3
  tasks_total: 3
  files_created: 7
  files_modified: 0
---

# Phase 01 Plan 01: Add-on Skeleton Config + Schema Summary

HA add-on metadata surface authored: repository manifest, config.yaml with 13-field schema, EN/PL translations, and PNG placeholder icons.

## What Was Built

**repository.yaml** declares the Argus custom add-on repository with name, url, and maintainer, enabling HA users to add the repo URL and see "Argus Anomaly Detection" in the store (ADDON-01).

**argus/config.yaml** is the full add-on manifest. Manifest block sets slug=argus, arch=[amd64,aarch64], startup=application, boot=auto, init=false, homeassistant_api=true (SUPV-01), services=[mqtt:need] (SUPV-02), map=[{type:data}]. Options defaults cover the eight fields with sensible values; four influx fields and detector_endpoint are absent from options (truly optional via schema ?-suffix). Schema exposes all 13 UI-configurable fields with precise types (UICFG-01/02/03/04/06).

**argus/translations/en.yaml** and **pl.yaml** provide name+description for all 13 fields. Polish copy meets D8 (UICFG-07). Per-item labels for [str] list fields are not added — confirmed unsupported by HA translation system (research A3).

**argus/icon.png** (250x250) and **argus/logo.png** (250x100) are valid PNG files with correct 8-byte PNG signatures. Visual artwork deferred per CONTEXT.

**tests/test_config_schema.py** provides two deterministic pytest functions:
- `test_config_yaml_valid`: asserts all structural invariants on config.yaml plus PNG signatures (stdlib only, no Pillow runtime dep)
- `test_schema_translation_parity`: bidirectional coverage check — every schema field has EN+PL name+description, and no translation defines a field absent from the schema

## Threat Mitigations Applied

| Threat | Mitigation |
|--------|-----------|
| T-1-01: influx_token disclosure | Typed as `password?` — Supervisor masks in UI and redacts from debug logs |
| T-1-02: schedule bounds tampering | `int(1,1440)` and `int(0,23)` enforce WR-04 startup bounds at Supervisor level before container starts |

## Deviations from Plan

None — plan executed exactly as written. All three tasks completed without deviation.

## Verification

```
python -m pytest tests/test_config_schema.py -x -q
2 passed in 0.04s
```

Structural YAML assertion (from plan verify block) also passes.

Live Supervisor validation (`ha addon validate`) is deferred to Phase 3/4 — tooling unavailable in dev env (Windows, no HA CLI/Docker with Supervisor). Documented as planned deferral, not a deviation.

## Known Stubs

None — all files are complete production artifacts. Icon/logo are intentionally minimal placeholders (documented in CONTEXT.md under Claude's discretion; visual polish deferred).

## Threat Flags

None — no new security surface beyond what is defined in the plan threat model.

## Self-Check: PASSED

Files exist:
- FOUND: repository.yaml
- FOUND: argus/config.yaml
- FOUND: argus/translations/en.yaml
- FOUND: argus/translations/pl.yaml
- FOUND: argus/icon.png
- FOUND: argus/logo.png
- FOUND: tests/test_config_schema.py

Commits exist:
- FOUND: 2665b0d feat(01): add repository.yaml and argus/config.yaml add-on manifest
- FOUND: 103bf1f feat(01): add EN and PL translations for all 13 config fields
- FOUND: e5cc9a9 feat(01): add PNG icons and config/translation parity test
