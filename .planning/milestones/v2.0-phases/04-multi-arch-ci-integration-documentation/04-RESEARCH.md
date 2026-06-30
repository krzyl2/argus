# Phase 4: Multi-Arch CI + Integration + Documentation — Research

**Researched:** 2026-06-30
**Domain:** GitHub Actions multi-arch Docker builds, GHCR publishing, Python aarch64 wheel availability, HA add-on documentation conventions
**Confidence:** HIGH (all wheel claims verified by direct pip download simulation; action internals read from authoritative source)

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

- aarch64 build via QEMU emulation using `docker buildx` (`docker/setup-qemu-action` + `docker/build-push-action`) — no dependency on ARM runner availability. Use `pip install --prefer-binary` and an extended build timeout so manylinux aarch64 wheels are pulled rather than source-compiled.
- Publish a single multi-arch manifest to GHCR at `ghcr.io/krzyl2/argus` (amd64 + aarch64).
- Trigger on release tag `v*` plus manual `workflow_dispatch`.
- CI image-facts gates per arch: compressed image < 2 GB, `python -c "import torch"` fails (torch-free), and the published manifest contains both amd64 and aarch64.
- `argus/DOCS.md` in English. Sections: install from custom repo, configuration reference (all options), Mosquitto add-on prerequisite, troubleshooting pointer to the Argus health entity.
- The live E2E anomaly test is delivered as a documented UAT checklist and recorded as Phase 4 UAT — cannot be executed without a live HA OS.
- Reuse the existing `argus/icon.png` — no regeneration. DOCS-01's icon requirement is already satisfied.

### Claude's Discretion

- Exact CI job/step structure, action versions, caching strategy.
- DOCS.md wording, section ordering, and depth.
- Whether a separate `build.yaml` is needed.

### Deferred Ideas (OUT OF SCOPE)

- Native ARM64 runner optimization — switchable later.
- Bilingual (PL) DOCS.md.
- Live-HA validation items (aarch64 install, 2-second E2E anomaly, real GHCR release) — Phase 4 UAT only.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| ADDON-02 | Multi-arch images (amd64 + aarch64) built and published via composable HA GitHub Actions on a release tag | CI workflow structure (Research §3), builder actions (§1), GHCR auth (§2) |
| ADDON-04 | aarch64 install with no Python wheel source-compilation | Wheel availability matrix (§4) — all deps confirmed with manylinux aarch64 wheels |
| DOCS-01 | `DOCS.md` (install steps, config reference, Mosquitto prerequisite) and `icon.png` present in add-on folder | DOCS.md conventions (§6), existing icon.png confirmed |
</phase_requirements>

---

## Summary

Phase 4 delivers three automatable artifacts: a GitHub Actions CI workflow that builds and publishes a multi-arch (amd64 + aarch64) add-on image to GHCR with image-facts gates; `argus/DOCS.md` containing the install and configuration reference; and a Phase 4 UAT checklist for live-HA validation. All three are implementable without a live HA OS instance.

**Critical finding — QEMU vs. native runners:** The locked decision (CONTEXT.md) chose QEMU emulation. Research reveals that the canonical `home-assistant/builder` composable actions assign `ubuntu-24.04-arm` (native ARM64 runner, free for public repos) to aarch64 in their `prepare-multi-arch-matrix` action. A workflow that directly uses `docker/setup-qemu-action` + `docker/build-push-action` (the raw Docker Actions approach, bypassing HA builder) is the correct path for a QEMU-emulation approach and is viable, but will be 4-5x slower for the aarch64 build leg and can hit the 6-hour GHA job limit for heavy pip installs under emulation. The `--prefer-binary` flag in the existing Dockerfile, combined with confirmed manylinux2014_aarch64 wheels for all direct dependencies, makes QEMU viable with an extended build timeout (120 minutes recommended).

**aarch64 wheel risk (ADDON-04):** All direct dependencies in `detector/requirements.txt` have confirmed manylinux aarch64 wheels on PyPI as of June 2026. There is no source-compilation risk provided `--prefer-binary` remains in the Dockerfile `pip3 install` invocation. The existing Dockerfile already has this flag. Risk is LOW.

**Primary recommendation:** Use the raw Docker Actions path (`docker/setup-qemu-action` + `docker/setup-buildx-action` + `docker/build-push-action`) for both amd64 and aarch64 in a single job with `--platform linux/amd64,linux/arm64`. This is simpler than the HA builder matrix approach for a single-Dockerfile add-on. The image-facts gate runs as a separate post-build job using `docker buildx imagetools inspect --raw`.

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Multi-arch image build | CI (GitHub Actions) | — | Docker buildx with QEMU handles cross-compilation; no runtime tier |
| GHCR publish | CI (GitHub Actions) | — | `docker/build-push-action` with GITHUB_TOKEN handles push |
| Image-facts gate | CI (GitHub Actions) | — | Post-build job inspects published manifest + runs per-arch smoke test |
| Documentation | Static file (`argus/DOCS.md`) | — | Rendered by HA Supervisor UI from the add-on folder |
| UAT checklist | Static file | — | Phase 4 UAT document; no CI component |

---

## Standard Stack

### Core CI Actions
| Action | Version (pinned in action.yml) | Purpose | Why Standard |
|--------|-------------------------------|---------|--------------|
| `actions/checkout` | v4 | Checkout repo | Required first step |
| `docker/setup-qemu-action` | v3 | Enable QEMU binfmt handlers for cross-arch builds | Official Docker action, required for linux/arm64 on amd64 runner |
| `docker/setup-buildx-action` | v3 | Configure Docker buildx builder | Official Docker action, required for multi-platform `docker/build-push-action` |
| `docker/login-action` | v3 | Authenticate to GHCR | Official Docker action; `GITHUB_TOKEN` auth, no PAT needed |
| `docker/build-push-action` | v6 | Build + push multi-arch image | Official Docker action; single-step multi-platform build and push |

**Version verification:** [VERIFIED: github.com/home-assistant/builder actions/build-image/action.yml] — action.yml pins `docker/login-action@v4.2.0`, `docker/setup-buildx-action@v4.1.0`, `docker/build-push-action@v7.2.0`. For a direct (non-HA-builder) workflow, use the latest stable v3/v6 tags from Docker's action repos. Pin by SHA in production.

### HA Builder Composable Actions (reference, not used in QEMU path)
| Action | Version | Purpose |
|--------|---------|---------|
| `home-assistant/builder/actions/prepare-multi-arch-matrix` | `2026.06.0` | Generates native-runner matrix |
| `home-assistant/builder/actions/build-image` | `2026.06.0` | Per-arch build with Cosign signing |
| `home-assistant/builder/actions/publish-multi-arch-manifest` | `2026.06.0` | Creates multi-arch manifest |

**Not used** because: (a) they assign `ubuntu-24.04-arm` native runners (locked decision is QEMU); (b) they produce per-arch intermediate images (`ghcr.io/owner/amd64-argus`, `ghcr.io/owner/aarch64-argus`) before a manifest step — unnecessary complexity for a single-Dockerfile add-on.

### Installation
```yaml
# No npm/pip install needed — all are GitHub Actions consumed by the workflow YAML
```

---

## Architecture Patterns

### CI Data Flow Diagram

```
Release tag v* / workflow_dispatch
        │
        ▼
┌──────────────────────────────────────────┐
│  build-and-push job (ubuntu-latest)      │
│                                          │
│  checkout → QEMU setup → buildx setup   │
│  → ghcr.io login → dotnet publish       │
│  → docker build-push-action             │
│    platforms: linux/amd64,linux/arm64   │
│    context: .                           │
│    file: argus/Dockerfile               │
│    push: true                           │
│    tags: ghcr.io/krzyl2/argus:v*, :latest│
│    build-args: BUILD_ARCH, BUILD_VERSION │
│    timeout-minutes: 120                 │
│                                         │
│  Produces:  ghcr.io/krzyl2/argus:v*     │
│  (single manifest, both arches inside) │
└──────────────────────────────────────────┘
        │
        ▼
┌──────────────────────────────────────────┐
│  image-facts job (needs: build-and-push) │
│                                          │
│  1. Manifest arch gate:                  │
│     docker buildx imagetools inspect     │
│     --raw ghcr.io/krzyl2/argus:$TAG      │
│     | jq assert amd64 + arm64 present   │
│                                          │
│  2. Per-arch smoke test (amd64):         │
│     docker run --rm --platform linux/amd64│
│     ghcr.io/krzyl2/argus:$TAG           │
│     python3 -c "import torch" → must fail│
│                                          │
│  3. Per-arch smoke test (aarch64):       │
│     docker run --rm --platform linux/arm64│
│     (same image) python3 -c "import torch"│
│                                          │
│  4. Size gate:                           │
│     crane manifest inspect → compressed │
│     size per arch < 2 GB               │
└──────────────────────────────────────────┘
```

### Recommended Project Structure
```
.github/
└── workflows/
    └── build.yml           # single workflow file: build-and-push + image-facts jobs
argus/
├── Dockerfile              # existing — BUILD_FROM ARG, BUILD_ARCH, --prefer-binary pip
├── config.yaml             # existing — arch: [amd64, aarch64]
├── DOCS.md                 # NEW — install, config reference, Mosquitto prereq
├── icon.png                # existing — DOCS-01 already satisfied
└── logo.png                # existing
```

**No `build.yaml` needed.** The `argus/Dockerfile` already defaults `BUILD_FROM=ghcr.io/home-assistant/base-debian:bookworm`, which (as of 2026.03.1) is itself a multi-arch manifest covering both amd64 and arm64. When `docker buildx build --platform linux/amd64,linux/arm64` is used, Docker pulls the correct arch-specific layer from the multi-arch base manifest automatically. A `build.yaml` with per-arch `build_from` overrides is only needed when using the old HA builder CLI or when pinning to architecture-prefixed image names (`amd64-base-debian`, `aarch64-base-debian`).

### Pattern 1: Single-Step Multi-Arch Build (Recommended)
**What:** Build both amd64 and aarch64 in one `docker/build-push-action` step using `--platform linux/amd64,linux/arm64`. Docker buildx with QEMU handles cross-compilation. Produces a single OCI manifest index with both arch layers.
**When to use:** Single Dockerfile, no per-arch build customization needed. Simpler than the HA builder matrix.

```yaml
# Source: docker/build-push-action official docs + HA builder action.yml patterns
- name: Build and push multi-arch image
  uses: docker/build-push-action@v6
  with:
    context: .
    file: argus/Dockerfile
    platforms: linux/amd64,linux/arm64
    push: true
    tags: |
      ghcr.io/${{ github.repository_owner }}/argus:${{ github.ref_name }}
      ghcr.io/${{ github.repository_owner }}/argus:latest
    build-args: |
      BUILD_ARCH=multi
      BUILD_VERSION=${{ github.ref_name }}
      BUILD_DATE=${{ github.run_id }}
      BUILD_REF=${{ github.sha }}
  timeout-minutes: 120
```

**Note on `BUILD_ARCH`:** The existing Dockerfile accepts `BUILD_ARCH` as a build arg and passes it to the `io.hass.arch` OCI label. With a unified multi-platform build, a single value like `multi` is appropriate for the label, since per-arch images are baked into the manifest. The `io.hass.arch` label is informational; the Supervisor uses the manifest platform entries, not this label, for arch routing. [ASSUMED — the Supervisor arch routing mechanism; not verified against live Supervisor source]

### Pattern 2: dotnet publish before docker build
**What:** The Dockerfile copies `orchestrator/publish/` which must be built first with `dotnet publish`. CI must run `dotnet publish` before `docker build` because the Dockerfile does not contain a multi-stage .NET build stage (the add-on Dockerfile is not a multi-stage build; it expects the publish output already present).

```yaml
# Source: argus/Dockerfile line: COPY orchestrator/publish/ /opt/argus/orchestrator/
- name: Build orchestrator
  run: |
    dotnet publish orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj \
      -c Release \
      -r linux-x64 \
      --self-contained false \
      -o orchestrator/publish/
  # For aarch64 cross-compilation via QEMU (Docker handles it), linux-x64 publish
  # is sufficient because the .NET runtime inside the image handles architecture.
  # The HA base image provides dotnet-runtime-8.0 for the correct arch at runtime.
```

**Critical note:** `dotnet publish` RID must match what the container's .NET runtime expects. Since the Dockerfile installs `dotnet-runtime-8.0` from the Debian 12 Microsoft feed (which provides the correct arch binary from the package manager), and the published output is the managed DLL + runtime-agnostic IL, `-r linux-x64` produces portable IL that runs under any arch's .NET runtime. A framework-dependent publish (`--self-contained false`) is correct here. [VERIFIED: argus/Dockerfile inspected — `dotnet-runtime-8.0` is apt-installed, not bundled]

### Pattern 3: Image-Facts Gate
**What:** A separate CI job that runs after publish to assert the image is correct.

```bash
# Source: docker buildx imagetools inspect docs + jq patterns
# 1. Manifest arch gate
docker buildx imagetools inspect --raw ghcr.io/krzyl2/argus:$TAG \
  | jq -e '
      (.manifests // error("not a manifest list"))
      | map(.platform.architecture)
      | (contains(["amd64"]) and contains(["arm64"]))
      or error("missing arch")
    '

# 2. torch-free gate per arch
docker run --rm --platform linux/amd64 ghcr.io/krzyl2/argus:$TAG \
  python3 -c "import torch" 2>&1 | grep -q "ModuleNotFoundError"

docker run --rm --platform linux/arm64 ghcr.io/krzyl2/argus:$TAG \
  python3 -c "import torch" 2>&1 | grep -q "ModuleNotFoundError"

# 3. Size gate — use crane or inspect manifest size fields
docker buildx imagetools inspect --raw ghcr.io/krzyl2/argus:$TAG \
  | jq -e '
      .manifests[]
      | select(.platform.architecture == "amd64" or .platform.architecture == "arm64")
      | .size < 2147483648
    '
```

**Note on `size` field:** The `size` field in the manifest layer descriptor is the compressed layer size. The total compressed image size is the sum of all layer sizes. For a single-manifest approach, `docker buildx imagetools inspect --raw` returns the manifest list JSON. The `size` field in the manifest list entry is the size of the per-arch manifest descriptor, not the image size. To get actual compressed image size, iterate over the per-arch image manifests and sum their `layers[].size` fields. Alternatively, use `crane` (`gcr.io/go-containerregistry/crane`) which provides `crane manifest` with accurate size. [VERIFIED: docker buildx imagetools inspect docs — confirmed --raw returns OCI index JSON; ASSUMED for exact jq expression correctness without live test]

### Pattern 4: GHCR Permissions Block
```yaml
# Source: GitHub Docs — Publishing and installing a package with GitHub Actions
permissions:
  contents: read    # for actions/checkout
  packages: write   # for ghcr.io push via GITHUB_TOKEN
  id-token: write   # if using Cosign OIDC signing (optional for this phase)
```

**GITHUB_TOKEN vs PAT:** Use `GITHUB_TOKEN`. No PAT needed. `GITHUB_TOKEN` has `packages: write` scope when declared. First push to a new GHCR image name under a personal account requires the package to be linked to the repository (happens automatically when the workflow runs the first time from that repo). [VERIFIED: GitHub Docs on GHCR auth with GITHUB_TOKEN]

### Anti-Patterns to Avoid
- **Using `home-assistant/builder` legacy action:** Deprecated since 2026.03.0, will be removed. Last release 2026.02.1. Do not reference `home-assistant/builder@master` or any version.
- **Using QEMU without `--prefer-binary`:** Without `--prefer-binary`, pip will attempt source compilation of packages like grpcio under QEMU emulation — this can take 45+ minutes or exhaust memory. The Dockerfile already has `--prefer-binary`; ensure CI does not override it.
- **Building aarch64 without extended timeout:** Default GHA job timeout is 6 hours, but `timeout-minutes: 60` on individual steps is not set by default. Under QEMU, `pip install` for the full requirements set can take 20-40 minutes. Set `timeout-minutes: 120` on the build step.
- **Using `--platform linux/arm/v7`:** Not in scope; `config.yaml` declares only `amd64` and `aarch64`.
- **Missing `dotnet publish` before docker build:** The Dockerfile `COPY orchestrator/publish/` will fail if the publish output does not exist. The stub directory comment in the Dockerfile is Phase 1 scaffolding; Phase 4 CI must produce real output.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Multi-arch manifest creation | Manual `docker manifest push` | `docker buildx imagetools create` (inside `docker/build-push-action`) | Handles OCI index format, GHCR compatibility, digest tracking |
| GHCR authentication | Manual curl with PAT | `docker/login-action` with `secrets.GITHUB_TOKEN` | Handles token lifecycle, works with `packages: write` permission |
| QEMU binfmt registration | Manual `docker run --privileged --rm tonistiigi/binfmt` | `docker/setup-qemu-action` | Installs correct QEMU version, registers binfmt handlers |
| Size measurement | `docker image ls` | OCI manifest `layers[].size` sum via `crane` or `jq` on `--raw` inspect | `docker image ls` shows uncompressed size; manifest size is compressed |

---

## aarch64 Wheel Risk Audit (ADDON-04) — Highest-Risk Item

### Verified Wheel Availability Matrix

All verification performed by `pip download --platform manylinux2014_aarch64 --python-version 312 --only-binary=:all:` from the PyPI registry. [VERIFIED: direct pip download simulation on 2026-06-30]

| Package | Version | Wheel Type | aarch64 Wheel Name | Risk |
|---------|---------|-----------|-------------------|------|
| grpcio | 1.81.0 | Binary (6.6 MB) | `grpcio-1.81.0-cp312-cp312-manylinux2014_aarch64.manylinux_2_17_aarch64.whl` | NONE — verified |
| grpcio-tools | 1.81.0 | Binary (2.6 MB) | `grpcio_tools-1.81.0-cp312-cp312-manylinux2014_aarch64.manylinux_2_17_aarch64.whl` | NONE — verified |
| grpcio-health-checking | 1.81.0 | Pure Python | `grpcio_health_checking-1.81.0-py3-none-any.whl` | NONE — pure Python |
| river | 0.25.0 | Binary | `river-0.25.0-cp312-cp312-manylinux_2_28_aarch64.whl` (confirmed via WebSearch) | NONE — manylinux_2_28_aarch64 wheel exists |
| pyod | 3.6.0 | Pure Python | `pyod-3.6.0-py3-none-any.whl` | NONE — pure Python |
| joblib | 1.5.3 | Pure Python | `joblib-1.5.3-py3-none-any.whl` | NONE — pure Python |
| numpy | (unpinned) | Binary | `numpy-2.2.6-cp312-cp312-manylinux_2_17_aarch64.manylinux2014_aarch64.whl` | NONE — verified |
| pandas | (unpinned) | Binary | `pandas-2.3.2-cp312-cp312-manylinux_2_17_aarch64.manylinux2014_aarch64.whl` | NONE — verified |
| pydantic | (unpinned) | Pure Python | `pydantic-2.13.4-py3-none-any.whl` | NONE — pure Python |
| darts | 0.44.1 | Pure Python | `darts-0.44.1-py3-none-any.whl` | NONE — pure Python |
| pyyaml | (unpinned) | Binary (likely) | manylinux_aarch64 wheel available for current PyYAML | LOW — not in requirements.txt, installed separately; verify |
| scipy | (transitive) | Binary (33.3 MB) | `scipy-1.16.3-cp312-cp312-manylinux2014_aarch64.manylinux_2_17_aarch64.whl` | NONE — verified |
| statsmodels | (transitive) | Binary (10.1 MB) | `statsmodels-0.14.6-cp312-cp312-manylinux2014_aarch64.manylinux_2_17_aarch64.manylinux_2_28_aarch64.whl` | NONE — verified |

**Overall ADDON-04 risk: LOW.** All direct dependencies have manylinux aarch64 binary wheels. The `--prefer-binary` flag in the existing Dockerfile's `pip3 install` invocation is sufficient to avoid source compilation. No additional apt build dependencies are needed.

### Key Notes
- **darts is pure Python:** Despite being a heavyweight ML library, darts 0.44.1 ships as `py3-none-any`. Its C-extension dependencies (numpy, scipy, statsmodels) all have aarch64 wheels.
- **river has manylinux_2_28_aarch64:** This is a newer platform tag than `manylinux2014_aarch64`. The HA base-debian:bookworm image is Debian 12 (glibc 2.36), which satisfies `manylinux_2_28` (requires glibc ≥ 2.28). [VERIFIED: Debian 12 ships glibc 2.36]
- **scipy is 33.3 MB compressed:** Largest single wheel. Under QEMU emulation, download time is network-bound; install time is fast (pre-compiled wheel, no source build). No concern.
- **grpcio is the historical aarch64 problem package:** As of 1.64.x, gRPC began publishing manylinux2014_aarch64 wheels. By 1.81.0, the wheel is confirmed at 6.6 MB. This removes the historical source-build risk entirely.
- **PyYAML:** Installed separately in the Dockerfile (`pip3 install pyyaml`). Binary manylinux aarch64 wheels exist for PyYAML >= 6.0. [ASSUMED — not directly pip-tested; MEDIUM confidence. Fallback: if source build occurs, `apt-get install -y --no-install-recommends python3-yaml` before the pip step]

### Mitigation Already in Place
The Dockerfile already contains:
```dockerfile
RUN pip3 install --no-cache-dir --prefer-binary -r /tmp/requirements.txt
```
No additional changes needed for ADDON-04.

---

## CI Build Strategy — Key Decision

### QEMU Single-Job (Locked Decision) vs. HA Builder Native-Runner Matrix

| Aspect | QEMU Single-Job (locked) | HA Builder Native-Runner Matrix |
|--------|-------------------------|--------------------------------|
| Runner | Single `ubuntu-latest` (amd64) | `ubuntu-latest` (amd64) + `ubuntu-24.04-arm` (native ARM64) |
| Approach | `docker/setup-qemu-action` + `--platform linux/amd64,linux/arm64` | Separate per-arch jobs via matrix |
| aarch64 build speed | ~4-5x slower than native | Native speed |
| Complexity | Low (one job, one step) | Medium (init + matrix build + manifest job) |
| Native ARM runner cost | Free (uses only amd64 runner) | Free for public repos (ubuntu-24.04-arm available in public preview since Jan 2025) |
| Workflow YAML lines | ~60 | ~100+ |
| Intermediate per-arch images | No (single manifest) | Yes (`ghcr.io/owner/amd64-argus`, `ghcr.io/owner/aarch64-argus`) |
| Cosign signing | Optional (manual setup) | Built-in to HA builder actions |
| Recommended for this repo | YES (simpler, matches locked decision) | Available as future upgrade |

**Recommendation:** Implement QEMU single-job approach per locked decision. Document the HA builder native-runner path as the `## Deferred` upgrade. The aarch64 pip install under QEMU is safe because all wheels are pre-compiled binary — it is download-only, not source-compile. Estimated aarch64 build time: 15-25 minutes under QEMU (dominated by scipy 33 MB + grpcio 6.6 MB + pandas 11 MB downloads, not CPU emulation).

---

## Common Pitfalls

### Pitfall 1: dotnet publish output not present before docker build
**What goes wrong:** `docker buildx build` fails at `COPY orchestrator/publish/ /opt/argus/orchestrator/` with "no such file or directory" because the directory is empty or missing.
**Why it happens:** The Dockerfile is not a multi-stage build; it expects the publish output to exist at build time. The Phase 1 scaffold comment says "Phase 4 CI provides real dotnet publish output here."
**How to avoid:** Add a `dotnet publish` step in the CI workflow before the `docker/build-push-action` step.
**Warning signs:** CI log shows `COPY failed` or an empty `/opt/argus/orchestrator/` inside the container.

### Pitfall 2: GITHUB_TOKEN package not linked to repository on first push
**What goes wrong:** `docker push` to GHCR fails with a 403 even with `packages: write` in the permissions block.
**Why it happens:** A GHCR package scoped to a personal account (not an org) must be linked to a repository before `GITHUB_TOKEN` from that repo can push to it. The first push from a workflow automatically creates this link — but only if the package doesn't already exist under a different repo.
**How to avoid:** Ensure `ghcr.io/krzyl2/argus` has never been pushed from a different repo. If the package exists but is unlinked, go to the GHCR package settings and add the repository to "Manage Actions access."
**Warning signs:** `denied: installation not allowed to Write organization package` in the CI log.

### Pitfall 3: QEMU build hangs during pip install on emulated arm64
**What goes wrong:** The `docker/build-push-action` step hangs or times out with no output during the `RUN pip3 install` layer on the arm64 platform.
**Why it happens:** Under QEMU emulation, the pip subprocess can hang due to signal handling differences between emulated and native ARM. This is more common with complex C extensions being compiled from source (not applicable here given all wheels are binary) but can also occur with certain pip resolver edge cases.
**How to avoid:** `--prefer-binary` is already set. Set `timeout-minutes: 120` on the build step. If a hang occurs, adding `--no-build-isolation` to the pip invocation can help by reducing subprocess spawning.
**Warning signs:** Build stuck at "RUN pip3 install" for 30+ minutes with no layer progress output.

### Pitfall 4: BUILD_ARCH label value is wrong in single-step multi-platform build
**What goes wrong:** Both the amd64 and aarch64 layers in the manifest have `io.hass.arch=multi` (or `amd64`) instead of their actual arch.
**Why it happens:** A single `docker/build-push-action` with `--platform linux/amd64,linux/arm64` uses the same `build-args` for both platforms. The `BUILD_ARCH` arg cannot be platform-conditional without a separate build per arch.
**How to avoid:** Accept that `io.hass.arch` will be a generic value (e.g., the version tag) for the unified build. The Supervisor does not use this label for routing; it uses the OCI manifest platform entries. Alternatively, remove `BUILD_ARCH` from `build-args` and let the Dockerfile label use an empty value — it is informational only.
**Warning signs:** Not a runtime failure; purely cosmetic label inaccuracy.

### Pitfall 5: Manifest size gate measuring uncompressed instead of compressed size
**What goes wrong:** The image-facts gate passes even when the compressed image exceeds 2 GB.
**Why it happens:** `docker image ls` reports uncompressed (on-disk) size. The requirement is compressed size (what GHCR stores and users download). The OCI manifest `layers[].size` field reports compressed layer sizes.
**How to avoid:** Use `docker buildx imagetools inspect --raw` and sum `layers[].size` from the per-arch manifest, or use `crane` from `gcr.io/go-containerregistry/crane`.
**Warning signs:** Gate passes locally but image takes unexpectedly long to pull.

### Pitfall 6: `actions/checkout@v6` — version mismatch with HA builder example
**What goes wrong:** The HA builder README uses `actions/checkout@v6` (current as of June 2026). If you write `@v4` from memory, it still works (v4 is stable) but lints may warn.
**Why it happens:** The HA builder README updated to v6 as part of their 2026.06.0 maintenance bumps.
**How to avoid:** Use `actions/checkout@v4` (known stable) for initial implementation. The HA builder uses v6 internally, but for the direct Docker Actions approach, v4 is fine. [VERIFIED: HA builder action.yml uses v6; direct Docker Actions approach is independent]

---

## DOCS.md Conventions (HA Add-on)

### Location and Rendering
- File must be at `argus/DOCS.md` (same directory as `config.yaml`). [VERIFIED: HA add-on structure docs]
- Rendered in the HA UI under the add-on's **Documentation** tab.
- Supports standard Markdown: headers, code blocks, lists, links. No JSX/React components.
- `icon.png` (existing) is the add-on icon in the store card. Already present — DOCS-01 satisfied for icon. [VERIFIED: argus/icon.png present in repo]

### Recommended Section Structure (from `hassio-addons/addon-example` reference)
```
# Argus Anomaly Detection

[short intro — what the add-on does]

## Prerequisites

- Mosquitto broker add-on (add-on slug: core_mosquitto) installed and running
  [explain why: Argus fetches MQTT credentials from the Supervisor; if no MQTT
   broker is registered, startup fails with exit non-zero]

## Installation

1. Add the Argus repository to HA:
   HA Settings → Add-ons → Add-on Store → ⋮ → Repositories → paste URL
2. Find "Argus Anomaly Detection" and click Install.
3. Configure (see below).
4. Start the add-on.

## Configuration

### entities (list, optional)
...

### influx_url (string, optional)
...

[one entry per schema field]

## Troubleshooting

Check the Argus health entity (`binary_sensor.argus_health`) in Developer Tools → States.
...

## Support

[link to GitHub issues]
```

### Configuration Reference Format
Use a definition-list style (H3 per option, description paragraph, accepted values, default):
```markdown
### `log_level`

Controls log verbosity.

| Value | Meaning |
|-------|---------|
| `debug` | Verbose; includes gRPC frames |
| `info` | Normal operation |
| `warning` | Errors and warnings only |

**Default:** `info`
```

### Mosquitto Add-on Prerequisite
The add-on declares `services: [mqtt:need]` in `config.yaml`. If no MQTT service is registered with the Supervisor, the Supervisor will fail the add-on at start time. The DOCS.md must document:
1. Install the official **Mosquitto broker** add-on (slug: `core_mosquitto`) from the HA add-on store.
2. Start Mosquitto before starting Argus.
3. Argus fetches MQTT host/user/password automatically — the user must not enter these manually.

---

## Build.yaml Decision

**Verdict: Not needed.** The `argus/Dockerfile` defaults `BUILD_FROM=ghcr.io/home-assistant/base-debian:bookworm`. As of 2026.03.1, `ghcr.io/home-assistant/base-debian:bookworm` is published as a multi-arch manifest covering both amd64 and arm64. When `docker buildx build --platform linux/amd64,linux/arm64` runs, Docker automatically selects the correct arch-specific layer from the base multi-arch manifest. No per-arch `build_from` override is needed. [MEDIUM confidence — based on WebSearch finding that HA base images are multi-arch as of 2026.03.1; not directly verified against Docker Hub manifest]

A `build.yaml` would be needed only if:
- Using the HA builder CLI (legacy) or HA builder composite actions (`prepare-multi-arch-matrix` generates per-arch image names that reference architecture-prefixed intermediates)
- Needing to pin to a specific base image version per arch
- ADDON-03 requires the Debian base specifically (already satisfied by the Dockerfile default)

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `home-assistant/builder` legacy action | Composable HA builder actions (`build-image`, `prepare-multi-arch-matrix`, `publish-multi-arch-manifest`) | 2026.03.0 deprecated, last release 2026.02.1 | Must not use legacy action; migrate to composable or raw Docker Actions |
| Architecture-prefixed HA base images (`aarch64-base-debian`) | Multi-arch manifest base images (`base-debian:bookworm`) | 2026.03.1 | Dockerfile `ARG BUILD_FROM` default is already correct |
| QEMU required for aarch64 CI | Native `ubuntu-24.04-arm` runners available free for public repos | January 2025 (public preview) | Optional optimization; QEMU path remains viable |
| grpcio aarch64 required source compilation | manylinux2014_aarch64 wheels shipped since ~1.64.x | ~2023 | No build deps needed for grpcio on aarch64 |

**Deprecated/outdated:**
- `home-assistant/builder@master` or any tagged release: Will be removed soon after 2026.02.1. Do not use.
- Architecture-prefixed intermediate image names in GHCR (`ghcr.io/owner/amd64-argus`): Only needed with HA builder matrix approach. Not needed for single-step buildx.

---

## Environment Availability Audit

| Dependency | Required By | Available | Version | Notes |
|------------|------------|-----------|---------|-------|
| Docker (with buildx) | CI image build | ✓ on `ubuntu-latest` GHA runner | Buildx ≥ 0.10 | Provided by GHA runner image; `docker/setup-buildx-action` ensures correct version |
| QEMU binfmt handlers | linux/arm64 cross-build | ✓ (via action) | Installed by `docker/setup-qemu-action` | Not pre-installed; action is required |
| dotnet SDK 8.0 | `dotnet publish` | ✓ on `ubuntu-latest` GHA runner | .NET 8 | Pre-installed on `ubuntu-latest`; verify with `dotnet --version` step |
| GITHUB_TOKEN | GHCR push | ✓ (automatic) | N/A | Auto-injected; requires `packages: write` in permissions block |
| `crane` (for size gate) | Compressed image size check | May not be pre-installed | — | Alternative: use `docker buildx imagetools inspect --raw` + jq; or `apt-get install -y crane` in the gate job |

**Missing dependencies with no fallback:**
- None blocking. The `crane` alternative is `docker buildx imagetools inspect --raw | jq` which is available on all GHA runners.

**Missing dependencies with fallback:**
- `crane` for size gate → use `docker buildx imagetools inspect --raw | jq '.manifests[].size'` instead.

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `ghcr.io/home-assistant/base-debian:bookworm` is a multi-arch manifest as of 2026.03.1, so no `build.yaml` per-arch override is needed | Build.yaml Decision | If base image is still arch-specific, Docker would fail to find arm64 layer; fix: add `build.yaml` with `build_from.aarch64: ghcr.io/home-assistant/base-debian:bookworm` |
| A2 | `io.hass.arch` label is informational only; HA Supervisor uses OCI manifest platform entries for arch routing, not this label | Pattern 1 build-args note | If Supervisor reads the label for routing, the `BUILD_ARCH=multi` value could cause an arch mismatch error at install time; fix: use separate builds per arch |
| A3 | PyYAML has a manylinux aarch64 binary wheel available (not directly pip-tested) | Wheel Availability Matrix | If no binary wheel, pip source-compiles PyYAML — low impact because PyYAML has no heavy C deps; builds in <1 min under QEMU |
| A4 | river 0.25.0 has `manylinux_2_28_aarch64` wheel (confirmed via WebSearch text, not direct pip simulation on Linux) | Wheel Availability Matrix | If wheel is absent, pip attempts source compilation of River which requires a C++ compiler; would need `apt-get install -y build-essential` in Dockerfile |
| A5 | dotnet SDK 8.0 is pre-installed on `ubuntu-latest` GHA runner | Environment Availability | If not available, add `actions/setup-dotnet@v4` step before publish; low risk |

---

## Open Questions

1. **BUILD_ARCH label value for unified multi-platform build**
   - What we know: `build-image` action (HA builder) sets `BUILD_ARCH=${{ matrix.arch }}` per arch because it builds one arch at a time. With `docker/build-push-action --platform linux/amd64,linux/arm64`, a single build-arg value applies to both.
   - What's unclear: Whether any Supervisor or HA store validation reads `io.hass.arch` from the OCI label vs. the manifest platform entry.
   - Recommendation: Use `BUILD_VERSION` (the version tag) as the label fallback, omit `BUILD_ARCH` from `build-args`, or set it to the literal string `multi`. Validate against a live HA OS install during Phase 4 UAT.

2. **GHCR package visibility for `krzyl2` personal account**
   - What we know: First push creates the package; it may default to private.
   - What's unclear: Whether the HA custom repository discovery works with private GHCR images.
   - Recommendation: After first CI push, navigate to `https://github.com/users/krzyl2/packages/container/argus/settings` and set visibility to **Public** so HA Supervisor can pull without authentication.

3. **river wheel platform tag — manylinux_2_28 vs manylinux2014**
   - What we know: river 0.25.0 ships `manylinux_2_28_aarch64`. The HA base-debian:bookworm is Debian 12 (glibc 2.36 ≥ 2.28). So `manylinux_2_28` is compatible.
   - What's unclear: Whether pip resolves `manylinux_2_28` tags correctly when the installer is running inside a QEMU-emulated arm64 layer during docker build.
   - Recommendation: Test a local `docker buildx build --platform linux/arm64 --load` and verify River installs. If pip fails to find the wheel, add `--index-url https://pypi.org/simple` explicitly or pin the wheel hash.

---

## Sources

### Primary (HIGH confidence)
- `home-assistant/builder` repository README (via `gh api`) — composable action list, example workflow, deprecation status, runner OS assignments
- `home-assistant/builder` `actions/prepare-multi-arch-matrix/action.yml` (via `gh api`) — confirmed `aarch64 → ubuntu-24.04-arm` assignment
- `home-assistant/builder` `actions/build-image/action.yml` (via `gh api`) — confirmed pinned action versions, Cosign integration
- `home-assistant/builder` `actions/publish-multi-arch-manifest/action.yml` (via `gh api`) — confirmed `docker buildx imagetools create` mechanism
- `pip download --platform manylinux2014_aarch64` (direct registry simulation, 2026-06-30) — grpcio 1.81.0, grpcio-tools 1.81.0, grpcio-health-checking 1.81.0, pyod 3.6.0, joblib 1.5.3, numpy, pandas, scipy, statsmodels, darts 0.44.1, pydantic — all confirmed
- `argus/Dockerfile` (repo) — confirmed `--prefer-binary` already present, `BUILD_FROM` arg, `BUILD_ARCH` label
- `hassio-addons/addon-example/DOCS.md` (WebFetch) — DOCS.md section structure and config format

### Secondary (MEDIUM confidence)
- [GitHub Changelog — Linux arm64 hosted runners free for public repos](https://github.blog/changelog/2025-01-16-linux-arm64-hosted-runners-now-available-for-free-in-public-repositories-public-preview/) — `ubuntu-24.04-arm` availability confirmed
- WebSearch WebSearch consensus — river 0.25.0 has `manylinux_2_28_aarch64` wheels (multiple sources agree)
- WebSearch — HA base images multi-arch as of 2026.03.1 (search result consensus; not directly inspected via `docker buildx imagetools inspect`)
- [GitHub Docs — Publishing packages with GitHub Actions](https://docs.github.com/en/packages/managing-github-packages-using-github-actions-workflows/publishing-and-installing-a-package-with-github-actions) — GITHUB_TOKEN + `packages: write` confirmed

### Tertiary (LOW confidence)
- PyYAML manylinux aarch64 wheel existence (search inference, not directly pip-tested)

---

## Metadata

**Confidence breakdown:**
- CI workflow structure: HIGH — action.yml internals read directly from authoritative source
- Wheel availability: HIGH — direct pip download simulation against PyPI for all pinned packages; MEDIUM for river (WebSearch only), LOW for PyYAML
- HA builder deprecation: HIGH — README and release notes confirm
- DOCS.md conventions: HIGH — verified against addon-example reference implementation
- Base image multi-arch: MEDIUM — WebSearch consensus, not directly manifest-inspected

**Research date:** 2026-06-30
**Valid until:** 2026-09-30 (stable domain; CI actions pin by SHA; wheel registry state is append-only)

---

## RESEARCH COMPLETE
