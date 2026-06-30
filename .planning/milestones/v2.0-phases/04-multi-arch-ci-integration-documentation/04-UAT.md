---
status: passed
phase: 04-multi-arch-ci-integration-documentation
source: [04-VERIFICATION.md]
started: 2026-06-30
updated: 2026-06-30
---

## Current Test

number: 1
name: Release tag triggers CI and publishes multi-arch manifest to GHCR (ADDON-02)
expected: |
  Pushing a v* tag (e.g. v2.0.0) triggers .github/workflows/build.yml; both
  build-and-push and image-facts jobs pass with no manual intervention; the
  multi-arch manifest (amd64 + aarch64) appears at ghcr.io/krzyl2/argus and the
  package is set to Public visibility.
awaiting: none — live-verified passed

## Tests

### 1. Release tag triggers CI and publishes to GHCR (ADDON-02)
expected: Push tag v2.0.0 → workflow runs both jobs green → manifest with amd64+aarch64 at ghcr.io/krzyl2/argus; compressed image < 2 GB and `import torch` fails in both arch images (image-facts gates pass). Set GHCR package visibility to Public after first push.
result: [pass]

### 2. aarch64 HA OS install with no Python wheel source-compilation (ADDON-04)
expected: Installing the add-on on an aarch64 HA OS host starts successfully; install logs show NO "Building wheel for ..." lines (all deps resolve to manylinux aarch64 binary wheels via --prefer-binary); add-on reaches Running state; Argus health entity appears OFF.
result: [pass]

### 3. End-to-end 2-second anomaly detection on live HA OS (success criterion 3)
expected: On a live HA OS install from the custom repo, an anomalous monitored-sensor state_changed event produces a binary_sensor (flag) and a score sensor entity in HA within 2 seconds, via MQTT discovery.
result: [pass]

### 4. DOCS.md + icon.png render in HA Documentation tab (DOCS-01)
expected: The add-on's Documentation tab shows DOCS.md (install steps, full configuration reference, Mosquitto prerequisite, troubleshooting) and the store listing shows icon.png.
result: [pass]

## Summary

total: 4
passed: 4
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps
