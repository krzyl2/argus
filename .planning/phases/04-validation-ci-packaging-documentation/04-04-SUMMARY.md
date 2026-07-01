---
phase: "04"
plan: "04"
subsystem: CI + Documentation
tags: [ci, docs, wwwroot, ingress-ui, warm-up]
dependency_graph:
  requires: []
  provides: [CI wwwroot asset gate, DOCS-02 Ingress UI workflow documentation]
  affects: [.github/workflows/build.yml, argus/DOCS.md]
tech_stack:
  added: []
  patterns: [bash test -f assertion in CI, numbered-step DOCS section]
key_files:
  created: []
  modified:
    - .github/workflows/build.yml
    - argus/DOCS.md
decisions:
  - Single <2 GB per-arch size gate reused; no second gate added (plan prohibition followed)
  - DOCS.md Using the Ingress UI section is additive; existing YAML Configuration section unchanged
  - HST warm-up disclosure states 4 minutes derived from window=250 at ~1 reading/s/entity
metrics:
  duration: "~6 minutes"
  completed: "2026-07-01"
  tasks: 2
  files_modified: 2
---

# Phase 04 Plan 04: CI wwwroot Asset Gate + Ingress UI Docs Summary

**One-liner:** CI `test -f` assertion guards htmx.min.js/argus.css in publish output before Docker build; DOCS.md gains a complete zero-YAML Ingress UI workflow section with HST warm-up disclosure and corrupted-config recovery.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | CI wwwroot presence assertion | 373dc1d | .github/workflows/build.yml |
| 2 | DOCS.md Ingress UI section | df6165f | argus/DOCS.md |

## What Was Built

### Task 1: CI wwwroot Assertion (373dc1d)

Inserted a new `Assert wwwroot assets present in publish output` step in `.github/workflows/build.yml` between the existing `Publish orchestrator` step and `Set up QEMU`. The step uses `shell: bash` and runs two `test -f` checks:

- `test -f orchestrator/publish/wwwroot/js/htmx.min.js`
- `test -f orchestrator/publish/wwwroot/css/argus.css`

Each check prints a `FAIL:` message and exits with code 1 if the file is absent, failing the build fast before any Docker layer is built. The existing `Gate — compressed image size < 2 GB per arch` step in the `image-facts` job is unchanged and remains the single size gate.

### Task 2: DOCS.md Ingress UI Section (df6165f)

Inserted `## Using the Ingress UI` after `## Installation` (line 40) and before `## Configuration` (line 127 post-insert). Subsections:

- **Opening the UI** — Home Assistant add-on page → Open Web UI; Ingress-only (no separate port, HA handles auth)
- **Selecting Entities** — live numeric sensor list, search/filter, checkbox selection; changes take effect immediately on Save
- **Assigning Detectors** — HST/MAD/STL with all parameters documented; inline validation; Save disabled until fields valid
- **Applying Changes (No Restart Required)** — pipeline swap under 1 second; MQTT+gRPC stay alive; HST warm-up ~4 min (window=250 at ~1 reading/s/entity); MAD/STL are batch detectors with no comparable warm-up
- **Recovering a Corrupted Configuration** — Log tab identifies error; Option A fix YAML via SSH; Option B delete `/data/entities.yaml` AND `/data/.ui_config_present` then restart (empty pipeline, re-configure via UI); explains lock-file mechanism

The existing YAML `## Configuration` section is fully preserved (additive-only change).

## Deviations from Plan

None — plan executed exactly as written.

## Verification

All automated checks passed:

```
python -c "...yaml.safe_load...; assert 'Assert wwwroot assets present in publish output' in names; ..."
OK: assertion step positioned correctly

grep -c "compressed image size < 2 GB" build.yml
1

python (section ordering check):
Installation at line 40
Using the Ingress UI at line 57
Configuration at line 127
OK: section ordering correct

All required subsections and content strings present (Opening the UI, Selecting Entities,
Assigning Detectors, Applying Changes (No Restart Required), Recovering a Corrupted
Configuration, 4 minutes, window=250, /data/entities.yaml, /data/.ui_config_present,
MAD and STL are batch detectors)
```

## Known Stubs

None. No UI data sources, no placeholder text introduced.

## Threat Flags

None. No new network endpoints, auth paths, file access patterns, or schema changes introduced.
The CI assertion reduces attack surface (broken images cannot ship); the docs recovery path
exposes only `/data/` paths that the operator already controls.

## Non-Inferable Items (Human Needed)

- **CI passing on a real GitHub Actions run:** dotnet publish emitting wwwroot assets and the resulting multi-arch image staying under 2 GB cannot be verified without an Actions runner. Confirmed as `non_inferable` in plan frontmatter.

## Self-Check: PASSED

- .github/workflows/build.yml: file exists and YAML parses (verified)
- argus/DOCS.md: file exists, section ordering verified, all subsections present (verified)
- 373dc1d commit: present in git log
- df6165f commit: present in git log
