# Phase 4: Multi-Arch CI + Integration + Documentation - Context

**Gathered:** 2026-06-30
**Status:** Ready for planning
**Mode:** Smart discuss (autonomous) — scope confirmed with user: build automatable artifacts (CI, multi-arch build, docs); live validation deferred to UAT.

<domain>
## Phase Boundary

Deliver the automatable artifacts that make Argus a publishable multi-arch HA add-on:
a GitHub Actions workflow that builds and publishes an amd64 + aarch64 image to GHCR with
image-facts gates, and install documentation (DOCS.md) covering the Mosquitto prerequisite.

**Explicitly in scope (automatable this session):**
- `.github/workflows/` CI: multi-arch build + GHCR publish + image-facts gates
- `argus/DOCS.md` (English): install, configuration reference, Mosquitto prerequisite
- Any `build.yaml` / build args needed for per-arch base image resolution

**Explicitly OUT of scope (deferred to live UAT — cannot run from this environment):**
- ADDON-04: aarch64 HA OS install validation (no Python wheel source-compile) — needs aarch64 HA OS host
- End-to-end anomaly test (binary_sensor + score within 2s) on a live HA OS install — needs live HA
- Actual GHCR release/publish on a real tag — needs repo push + release event

These deferred items become Phase 4 UAT, mirroring Phase 3's live-runtime UAT.
</domain>

<decisions>
## Implementation Decisions

### CI — Multi-arch Build & Publish
- aarch64 build via QEMU emulation using `docker buildx` (`docker/setup-qemu-action` + `docker/build-push-action`) — no dependency on ARM runner availability (research-flag fallback). Use `pip install --prefer-binary` and an extended build timeout so manylinux aarch64 wheels are pulled rather than source-compiled.
- Publish a single multi-arch manifest to GHCR at `ghcr.io/krzyl2/argus` (amd64 + aarch64).
- Trigger on release tag `v*` plus manual `workflow_dispatch`.
- CI image-facts gates per arch: compressed image < 2 GB, `python -c "import torch"` fails (torch-free), and the published manifest contains both amd64 and aarch64.

### Documentation & Live E2E Delivery
- `argus/DOCS.md` in English (HA docs-tab convention; D8 keeps code/identifiers English, HA entity friendly-names Polish). Sections: install from custom repo, configuration reference (all options), Mosquitto add-on prerequisite, troubleshooting pointer to the Argus health entity.
- The live E2E anomaly test is delivered as a documented UAT checklist (and a small helper script if practical) and recorded as Phase 4 UAT — it cannot be executed without a live HA OS.
- Reuse the existing `argus/icon.png` (already present) — no regeneration. DOCS-01's icon requirement is already satisfied.

### Claude's Discretion
- Exact CI job/step structure, action versions, caching strategy.
- DOCS.md wording, section ordering, and depth.
- Whether a separate `build.yaml` is needed (the Dockerfile already defaults `BUILD_FROM` to a multi-arch HA base image; add `build.yaml` only if per-arch base pinning is required).
</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- `argus/config.yaml` — add-on manifest; already declares `arch: [amd64, aarch64]`, `version: "2.0.0"`, `slug: argus`, `url: https://github.com/krzyl2/argus`, `services: [mqtt:need]`, `homeassistant_api: true`, `watchdog`.
- `argus/Dockerfile` — already HA-add-on shaped: `ARG BUILD_FROM=ghcr.io/home-assistant/base-debian:bookworm`, `FROM ${BUILD_FROM}`, and `ARG BUILD_ARCH/BUILD_DATE/BUILD_REF/BUILD_VERSION`. Single-container add-on bundling orchestrator + detector.
- `argus/icon.png`, `argus/logo.png` — present.
- `argus/translations/en.yaml`, `argus/translations/pl.yaml` — config field labels.
- Phase 1 established an "image-facts gate" pattern (Dockerfile build asserts torch-free, glibc-linked, size) — CI gates should mirror it.

### Established Patterns
- Mono-repo: `proto/`, `orchestrator/`, `detector/`, `deploy/`, `argus/` (the add-on).
- `deploy/Dockerfile.detector` and `deploy/Dockerfile.orchestrator` are the v1 separate-image builds; the add-on (`argus/Dockerfile`) is the single-container v2 image — CI builds the add-on image.
- No `.github/workflows/` exists yet — CI is greenfield.

### Integration Points
- CI consumes `argus/Dockerfile` + `argus/config.yaml`; publishes to GHCR under the repo owner.
- DOCS.md lives at `argus/DOCS.md` so it appears in the HA add-on Documentation tab.
- Image-facts gates reuse the assertions Phase 1 baked into the Dockerfile build.
</code_context>

<specifics>
## Specific Ideas

- Research flag (ROADMAP): confirm native ARM64 GHA runner availability before the CI matrix; resolved by choosing QEMU emulation as the portable default with `--prefer-binary` + extended timeout fallback.
- Image-facts gates must assert both arches in the final manifest and the torch-free / <2GB facts per arch (success criterion 1).
</specifics>

<deferred>
## Deferred Ideas

- Native ARM64 runner optimization (faster builds) — switchable later if the repo's plan offers `ubuntu-*-arm` runners.
- Bilingual (PL) DOCS.md — English-only for now.
- Live-HA validation items (aarch64 install, 2-second E2E anomaly, real GHCR release) — tracked as Phase 4 UAT, not implementable this session.
</deferred>
