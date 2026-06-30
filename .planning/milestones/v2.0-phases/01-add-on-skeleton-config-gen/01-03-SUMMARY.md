---
phase: 01-add-on-skeleton-config-gen
plan: "03"
subsystem: dockerfile-image-gate
tags: [dockerfile, image, ADDON-03, ADDON-05, glibc, debian, bookworm]
status: complete

dependency_graph:
  requires: ["01-02"]
  provides: [argus/Dockerfile, deploy/image-facts.sh]
  affects: [Phase 4 CI, image build pipeline]

tech_stack:
  added:
    - "ghcr.io/home-assistant/base-debian:bookworm — HA add-on base image (Debian 12)"
    - "dotnet-runtime-8.0 — from Microsoft Debian 12 package feed (glibc-linked)"
    - "python3.12 + pip3 — Debian bookworm packages"
    - "pyyaml — explicit install (not in detector/requirements.txt); used by gen-entities.py"
  patterns:
    - "ARG BUILD_FROM — composable GHA build-image substitutes arch-specific base tag"
    - "ENV S6_BEHAVIOUR_IF_STAGE2_FAILS=2 — s6 exits container on any service crash"
    - "pip --prefer-binary — mandatory for aarch64 to avoid scipy/statsmodels source builds"
    - "image-facts.sh static + image <tag> modes — reusable CI gate pattern"

key_files:
  created:
    - path: argus/Dockerfile
      purpose: "Single-arch-parametric add-on image on Debian bookworm; folds orchestrator + detector into one image"
    - path: deploy/image-facts.sh
      purpose: "Reusable image-fact gate: static Dockerfile checks + built-image torch-absent/size/glibc assertions"
  modified: []

decisions:
  - "pyyaml installed as a separate explicit RUN (not in requirements.txt) to keep the dependency auditable — gen-entities.py needs it but the detector process does not"
  - "darts comment wording avoids the strings 'torch' and 'darts[' so image-facts.sh static grep-assertions cannot false-positive on comment text"
  - "image-facts.sh size check uses manifest inspect (compressed) with local docker inspect fallback (uncompressed, conservative upper bound)"
  - "glibc check skipped with a clear SKIP notice when Argus.Orchestrator.dll is absent — Phase 1 has no real publish output; full assertion deferred to Phase 4 CI"

metrics:
  duration: "~5 minutes"
  completed: "2026-06-29"
  tasks_completed: 2
  tasks_total: 2
  files_created: 2
  files_modified: 0
---

# Phase 01 Plan 03: Dockerfile + Image-Facts Gate Summary

Single-arch-parametric add-on Dockerfile on Debian bookworm with a reusable static + built-image CI gate.

## What Was Built

**Task 1 — argus/Dockerfile** (`056929c`)

Debian bookworm add-on image that folds the orchestrator and detector into one container:

- `ARG BUILD_FROM=ghcr.io/home-assistant/base-debian:bookworm` — composable GHA action substitutes arch-specific tag
- `ENV S6_BEHAVIOUR_IF_STAGE2_FAILS=2` — s6 exits container loudly on any service crash
- dotnet-runtime-8.0 from Microsoft Debian 12 package feed (glibc-linked, not musl)
- python3.12 + pip3; `pip3 install --prefer-binary` for aarch64 compatibility
- Explicit `pip3 install pyyaml` with explanatory comment (absent from requirements.txt; needed by gen-entities.py)
- `COPY orchestrator/publish/ /opt/argus/orchestrator/` — CI publish output
- `COPY detector/ /opt/argus/detector/` — detector source + proto stubs
- `COPY argus/rootfs/ /` — s6 init scripts, gen-entities.py, Phase 3 service stubs
- io.hass LABEL block (name, description, arch, type, version via ARG)
- No CMD/ENTRYPOINT — base image owns `/init`

**Task 2 — deploy/image-facts.sh** (`89d5fab`)

Two-mode gate script:

- `static` (no Docker, always runnable): grep-asserts bookworm base, S6 env, --prefer-binary, pyyaml, no-torch, no-darts-extras, no CMD/ENTRYPOINT, dotnet-runtime-8.0, rootfs COPY
- `image <tag>` (Docker required, reused by Phase 4 CI): asserts `import torch` fails; compressed size < 2 GB (manifest inspect with local docker inspect fallback); glibc ldd check on Argus.Orchestrator.dll (SKIPPED with clear notice when dll absent)
- Default mode (no argument): static

## Verification Results

| Check | Mode | Result |
|-------|------|--------|
| `bash -n deploy/image-facts.sh` | syntax | PASS |
| `bash deploy/image-facts.sh static` | static | 10/10 PASS |
| `python3` Dockerfile assertion script | automated | OK |
| `docker build` + `image-facts.sh image` | live | DEFERRED — see below |

## Deferred (Docker Unavailable in Dev Env)

The following live-build assertions cannot run on the Windows dev box (Docker not installed). They are encoded in `deploy/image-facts.sh image <tag>` for Phase 4 CI:

- `import torch` fails in built image (ADDON-05 regression gate)
- Compressed image size < 2 GB via `docker manifest inspect` (ADDON-05)
- glibc linkage of Argus.Orchestrator.dll via `ldd` (requires real orchestrator publish output, not the Phase 1 stub)

Phase 4 CI procedure:
```bash
mkdir -p orchestrator/publish && touch orchestrator/publish/.keep  # Phase 1 stub
docker build -f argus/Dockerfile -t argus:test .
bash deploy/image-facts.sh image argus:test
```

## Deviations from Plan

**1. [Rule 1 - Bug] Comment text contained assertion-target strings**

- **Found during:** Task 1 verification
- **Issue:** Dockerfile comment "No darts extras (darts[torch], darts[all]) — prevents 3-5 GB PyTorch inflation" contained the strings `darts[` and `torch`. The `image-facts.sh static` grep assertions (and the plan's own `python -c` verifier) match these strings in comments, producing false negatives.
- **Fix:** Reworded comment to "Core darts only (no ML-framework extras) — keeps image under 2 GB (ADDON-05)" — no forbidden strings remain anywhere in the file.
- **Files modified:** `argus/Dockerfile`

No other deviations. Plan executed as specified.

## Stub Tracking

No user-visible stubs. `orchestrator/publish/` is documented as a Phase 1 placeholder (no real binary yet); this is intentional and noted in both the Dockerfile and `image-facts.sh` header.

## Threat Surface Scan

No new network endpoints, auth paths, file access patterns, or schema changes introduced. The Dockerfile adds package fetch from `packages.microsoft.com` and PyPI — both documented in the plan threat model (T-1-SC: Microsoft signed Debian feed; PyPI via --prefer-binary against pre-vetted requirements.txt). No new threats beyond those in the plan register.

## Self-Check

Files exist:

- argus/Dockerfile: FOUND
- deploy/image-facts.sh: FOUND

Commits:

- 056929c: FOUND
- 89d5fab: FOUND

## Self-Check: PASSED
