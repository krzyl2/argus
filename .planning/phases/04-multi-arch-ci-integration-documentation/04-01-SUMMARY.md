---
phase: 04-multi-arch-ci-integration-documentation
plan: "01"
subsystem: ci
tags: [github-actions, docker-buildx, ghcr, multi-arch, qemu, image-facts]
dependency_graph:
  requires: []
  provides: [".github/workflows/build.yml"]
  affects: ["ghcr.io/krzyl2/argus", "argus/Dockerfile"]
tech_stack:
  added:
    - "docker/build-push-action@v6 (multi-arch single-step build)"
    - "docker/setup-qemu-action@v3 (ARM64 emulation binfmt)"
    - "docker/setup-buildx-action@v3"
    - "docker/login-action@v3 (GHCR auth via GITHUB_TOKEN)"
    - "actions/setup-dotnet@v4 (dotnet 8.0.x)"
  patterns:
    - "dotnet publish before docker build (Dockerfile not multi-stage)"
    - "QEMU single-job multi-platform (linux/amd64,linux/arm64)"
    - "image-facts post-build gate job (manifest inspect + torch-free + size)"
key_files:
  created:
    - ".github/workflows/build.yml"
  modified: []
decisions:
  - "Omit BUILD_ARCH from build-args — cannot be per-arch in a unified build; io.hass.arch label is informational, Supervisor routes on manifest platform entries (RESEARCH Pitfall 4)"
  - "Use imagetools inspect --raw + jq to measure compressed layer sizes, not docker image ls (RESEARCH Pitfall 5)"
  - "Set timeout-minutes: 120 on build step for QEMU aarch64 pip install headroom"
  - "Scoped permissions to contents:read + packages:write only — no id-token:write (Cosign deferred)"
metrics:
  duration: "~10 minutes"
  completed: "2026-06-30"
  tasks_completed: 2
  tasks_total: 2
  files_created: 1
  files_modified: 0
---

# Phase 04 Plan 01: Multi-Arch CI + GHCR Publish Summary

**One-liner:** Two-job GitHub Actions workflow — QEMU single-step `docker/build-push-action@v6` (amd64+arm64) with `dotnet publish` pre-step, followed by an image-facts gate asserting both arches present, torch-free, and compressed size < 2 GB.

## What Was Built

`.github/workflows/build.yml` — a 128-line, two-job workflow:

**Job 1: `build-and-push`** (ubuntu-latest, timeout 150 min)
- Triggers on `v*` tag push and `workflow_dispatch`
- `actions/setup-dotnet@v4` guards against runner .NET version drift
- `dotnet publish orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj -c Release --self-contained false -o orchestrator/publish/` runs before the Docker build, populating the path the Dockerfile COPYs
- QEMU + Buildx setup, GHCR login via `GITHUB_TOKEN`
- `docker/build-push-action@v6` with `platforms: linux/amd64,linux/arm64`, `file: argus/Dockerfile`, push to `ghcr.io/<owner>/argus:<tag>` and `:latest`
- Exposes `image_ref` job output for the downstream gate job

**Job 2: `image-facts`** (ubuntu-latest, timeout 30 min, needs build-and-push)
- Reads `IMG` from upstream `image_ref` output
- **Gate 1:** `docker buildx imagetools inspect --raw | jq` — asserts manifest contains both amd64 and arm64
- **Gate 2:** `docker run --platform linux/amd64` and `linux/arm64` — asserts `import torch` fails (torch-free)
- **Gate 3:** Iterates per-arch digests from OCI index, sums `config.size + layers[].size` via `imagetools inspect --raw`, asserts total < 2147483648 bytes; echoes MB per arch for CI log

## Acceptance Criteria Met

| Criterion | Status |
|-----------|--------|
| `v*` tag and workflow_dispatch triggers | PASS |
| `dotnet publish` before docker build | PASS |
| `docker/build-push-action@v6` with linux/amd64,linux/arm64 | PASS |
| `file: argus/Dockerfile` | PASS |
| Push to `ghcr.io/<owner>/argus` | PASS |
| `permissions: packages: write` | PASS |
| `image_ref` job output exposed | PASS |
| image-facts `needs: build-and-push` | PASS |
| Manifest arch gate (amd64 + arm64) | PASS |
| torch-free gate per arch | PASS |
| Compressed size < 2147483648 bytes gate per arch | PASS |
| `--prefer-binary` in argus/Dockerfile untouched | PASS |
| Workflow file >= 60 lines | PASS (128 lines) |

## UAT Items (NOT executor-verifiable — live HA OS required)

- Real release tag push publishes to GHCR and package is set to Public visibility
- Installing on aarch64 HA OS host starts with no Python wheel source-compilation

## Deviations from Plan

None. Plan executed exactly as written.

## Threat Mitigations Applied

| Threat | Mitigation |
|--------|-----------|
| T-04-01: GITHUB_TOKEN scope | `permissions:` block scoped to `contents: read` + `packages: write` only |
| T-04-02: Tampering via published image | image-facts job re-inspects the *published* manifest (not local cache) |

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| Task 1 + Task 2 | 5915970 | ci(04): multi-arch GHCR publish workflow with image-facts gates |

## Self-Check: PASSED

- `.github/workflows/build.yml` exists (128 lines, YAML valid)
- Commit 5915970 present in git log
- Both plan verify commands return "OK"
- `--prefer-binary` confirmed present in `argus/Dockerfile` (line 32)
