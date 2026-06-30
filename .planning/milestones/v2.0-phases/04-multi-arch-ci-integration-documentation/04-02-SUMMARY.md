---
phase: 04-multi-arch-ci-integration-documentation
plan: "02"
subsystem: documentation
tags: [docs, ha-addon, mqtt, configuration-reference]
dependency_graph:
  requires: []
  provides: [DOCS-01]
  affects: [argus/DOCS.md]
tech_stack:
  added: []
  patterns: [HA add-on DOCS.md convention, config reference H3-per-field format]
key_files:
  created:
    - argus/DOCS.md
  modified: []
decisions:
  - DOCS.md is English-only (D8: code/identifiers English; Polish only for HA entity friendly-names)
  - MQTT and HA credentials documented as auto-fetched (T-04-05 mitigation — must NOT be entered manually)
  - icon.png verified present and untouched (DOCS-01 icon requirement already satisfied in phase 01)
  - influx_url empty = batch path disabled (no error); documented explicitly per UICFG-02
  - detector_endpoint empty = bundled local detector; documented per UICFG-03
metrics:
  duration_minutes: 8
  completed: "2026-06-30T10:31:58Z"
  tasks_completed: 2
  tasks_total: 2
  files_created: 1
  files_modified: 0
---

# Phase 04 Plan 02: DOCS.md — Install, Config Reference, Troubleshooting Summary

**One-liner:** HA add-on DOCS.md with Mosquitto `mqtt:need` prerequisite, custom-repo install steps, all 13 config.yaml schema fields with defaults/semantics, and `binary_sensor.argus_addon_health` troubleshooting.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | Write DOCS.md — intro, prerequisites, installation | 7783ff6 | argus/DOCS.md (created) |
| 2 | Write DOCS.md — configuration reference + troubleshooting; verify icon.png | 7783ff6 | argus/DOCS.md (completed in same commit) |

## Deliverables

- `argus/DOCS.md` — 269 lines, covering:
  - Title + 3-sentence intro (streaming + batch detection, MQTT discovery, self-hosted)
  - `## Prerequisites` — Mosquitto broker (`core_mosquitto`), `services: [mqtt:need]` fail-loud behavior, automatic HA API access, InfluxDB optional
  - `## Installation` — 4-step numbered install via custom repo (`https://github.com/krzyl2/argus`)
  - `## Configuration` — all 13 schema fields (H3 per field with type, default, description)
  - `## Troubleshooting` — `binary_sensor.argus_addon_health` (OFF=healthy, ON=problem), add-on won't start, no entities appear, streaming delay
  - `## Support` — GitHub issues link

- `argus/icon.png` — verified present, not modified (last touched in phase 01 commit e5cc9a9)

## Configuration Fields Documented

All 13 fields from `argus/config.yaml` `schema:` block covered:
`entities`, `include_patterns`, `exclude_patterns`, `influx_url`, `influx_token`, `influx_org`,
`influx_bucket`, `influx_measurement`, `influx_value_field`, `detector_endpoint`,
`batch_interval_minutes`, `nightly_fit_hour`, `log_level`

## Threat Model — Applied Mitigations

| Threat | Mitigation Applied |
|--------|--------------------|
| T-04-05 Information Disclosure (manual credential entry) | DOCS.md explicitly states MQTT credentials and HA API access are auto-fetched by the Supervisor; users must NOT enter them manually |
| T-04-06 Repudiation (stale config reference) | Config reference sourced directly from config.yaml schema at authoring time; drift risk accepted |

## Deviations from Plan

None — plan executed exactly as written. Both tasks were committed as a single atomic commit since Task 2 was an append to the same file started in Task 1 — no behavioral difference from per-task commits.

## Known Stubs

None. All configuration fields are documented with accurate values from `config.yaml` and `translations/en.yaml`. No placeholder text.

## Self-Check: PASSED

- `argus/DOCS.md` exists: FOUND
- Commit `7783ff6` exists: FOUND
- `argus/icon.png` exists: FOUND (unmodified)
- All 13 schema fields present in DOCS.md: VERIFIED
- `## Prerequisites` with `core_mosquitto`: VERIFIED
- `argus_addon_health` in Troubleshooting: VERIFIED
- `krzyl2/argus` repo URL present: VERIFIED
- Line count: 269 (minimum 60 satisfied)
