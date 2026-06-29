#!/usr/bin/env bash
# deploy/image-facts.sh — image-fact gate for Argus add-on
#
# USAGE:
#   bash deploy/image-facts.sh              # defaults to static mode
#   bash deploy/image-facts.sh static       # grep-assert Dockerfile directives (no Docker needed)
#   bash deploy/image-facts.sh image <tag>  # assert built-image facts (Docker required)
#
# STATIC MODE (always runnable, no Docker):
#   Verifies argus/Dockerfile contains the required directives and is free of
#   forbidden patterns. Fails immediately with a clear message on any violation.
#
# IMAGE MODE (reused by Phase 4 CI and local builds when Docker is present):
#   Requires a successfully built image. Asserts:
#     1. No PyPI torch package in the image (ADDON-05)
#     2. Compressed image size < 2 GB (ADDON-05)
#     3. glibc linkage of the orchestrator binary (musl-linked .NET fails at runtime)
#
# PHASE 1 NOTE FOR CALLERS:
#   Phase 1 may not have a real orchestrator publish output in orchestrator/publish/.
#   Create a stub so the docker build COPY step succeeds:
#     mkdir -p orchestrator/publish && touch orchestrator/publish/.keep
#   The torch-absent and size assertions are valid even with a stub — they exercise
#   the Python layer. The glibc assertion is SKIPPED when the dll is absent.

set -euo pipefail

DOCKERFILE="argus/Dockerfile"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
DOCKERFILE_PATH="${REPO_ROOT}/${DOCKERFILE}"

# ─────────────────────────────────────────────────────────────────────────────
# Helpers
# ─────────────────────────────────────────────────────────────────────────────

pass() { echo "  PASS  $*"; }
fail() { echo "  FAIL  $*" >&2; exit 1; }
skip() { echo "  SKIP  $*"; }

# ─────────────────────────────────────────────────────────────────────────────
# MODE: static
# ─────────────────────────────────────────────────────────────────────────────

run_static() {
    echo "=== image-facts.sh static ==="
    echo "Dockerfile: ${DOCKERFILE_PATH}"
    echo ""

    if [[ ! -f "${DOCKERFILE_PATH}" ]]; then
        fail "Dockerfile not found at ${DOCKERFILE_PATH}"
    fi

    local content
    content="$(cat "${DOCKERFILE_PATH}")"

    # ADDON-03: Debian bookworm base, never Alpine
    if echo "${content}" | grep -qF 'ghcr.io/home-assistant/base-debian:bookworm'; then
        pass "base-debian:bookworm present"
    else
        fail "base-debian:bookworm NOT found — Dockerfile must use ghcr.io/home-assistant/base-debian:bookworm (ADDON-03)"
    fi

    if echo "${content}" | grep -qi 'alpine'; then
        fail "Alpine reference found — add-on must use Debian base (ADDON-03)"
    else
        pass "no Alpine reference"
    fi

    # s6 fail-loud setting
    if echo "${content}" | grep -qF 'S6_BEHAVIOUR_IF_STAGE2_FAILS=2'; then
        pass "S6_BEHAVIOUR_IF_STAGE2_FAILS=2 set"
    else
        fail "S6_BEHAVIOUR_IF_STAGE2_FAILS=2 NOT set — s6 must exit on service crash"
    fi

    # pip --prefer-binary (mandatory for aarch64)
    if echo "${content}" | grep -qF -- '--prefer-binary'; then
        pass "--prefer-binary present"
    else
        fail "--prefer-binary NOT found — mandatory for aarch64 amd64 compatibility (ADDON-03)"
    fi

    # PyYAML (required by gen-entities.py, not in detector/requirements.txt)
    if echo "${content}" | grep -qi 'pyyaml'; then
        pass "pyyaml install present"
    else
        fail "pyyaml NOT found — gen-entities.py requires PyYAML"
    fi

    # ADDON-05: no PyPI torch package pulled in
    if echo "${content}" | grep -qi 'torch'; then
        fail "torch reference found in Dockerfile — no ML-framework extras allowed (ADDON-05)"
    else
        pass "no torch reference"
    fi

    # ADDON-05: no darts extras that would pull torch
    if echo "${content}" | grep -qF 'darts['; then
        fail "darts with extras found — use core darts only to avoid size inflation (ADDON-05)"
    else
        pass "no darts extras"
    fi

    # No CMD/ENTRYPOINT (base image owns /init)
    if echo "${content}" | grep -qE '^\s*(CMD|ENTRYPOINT)\b'; then
        fail "CMD or ENTRYPOINT found — base image owns /init; do not override"
    else
        pass "no CMD/ENTRYPOINT"
    fi

    # dotnet-runtime-8.0 installed
    if echo "${content}" | grep -qF 'dotnet-runtime-8.0'; then
        pass "dotnet-runtime-8.0 install present"
    else
        fail "dotnet-runtime-8.0 NOT found — .NET 8 runtime required for orchestrator"
    fi

    # rootfs COPY (brings cont-init.d + gen-entities.py into image)
    if echo "${content}" | grep -qF 'COPY argus/rootfs'; then
        pass "COPY argus/rootfs present"
    else
        fail "COPY argus/rootfs NOT found — s6 init scripts must be copied into image"
    fi

    echo ""
    echo "=== static: ALL CHECKS PASSED ==="
}

# ─────────────────────────────────────────────────────────────────────────────
# MODE: image <tag>
# ─────────────────────────────────────────────────────────────────────────────

run_image() {
    local tag="${1:-}"
    if [[ -z "${tag}" ]]; then
        echo "Usage: $0 image <tag>" >&2
        exit 1
    fi

    echo "=== image-facts.sh image ${tag} ==="
    echo ""

    if ! command -v docker &>/dev/null; then
        fail "docker not found — image mode requires Docker"
    fi

    # ── 1. No PyPI torch package (ADDON-05) ──────────────────────────────────
    # The pass condition is a NON-ZERO exit from the import attempt.
    # If torch is absent, python3 -c "import torch" exits 1 — that is correct.
    # If torch is present (regression), it exits 0 — fail the gate.
    echo "Checking: no torch in image (ADDON-05)..."
    if docker run --rm "${tag}" python3 -c "import torch" 2>/dev/null; then
        fail "torch IS present in image — darts or another dep pulled it in (ADDON-05)"
    else
        pass "import torch failed as expected — torch absent from image"
    fi

    # ── 2. Compressed image size < 2 GB (ADDON-05) ───────────────────────────
    echo "Checking: compressed image size < 2 GB (ADDON-05)..."
    local compressed_bytes=0

    # Prefer manifest inspect (works for pushed images and multi-arch manifests).
    # Fall back to local inspect size (uncompressed, slightly pessimistic) when the
    # image is not pushed to a registry.
    if docker manifest inspect "${tag}" &>/dev/null 2>&1; then
        compressed_bytes=$(docker manifest inspect "${tag}" \
            | python3 -c "import json,sys; m=json.load(sys.stdin); print(sum(l['size'] for l in m.get('layers',[])))")
        echo "  Source: docker manifest inspect (compressed layer sum)"
    else
        # Local fallback: docker image inspect Size (uncompressed virtual size).
        # Uncompressed is larger than compressed so this is a conservative upper bound.
        compressed_bytes=$(docker image inspect "${tag}" \
            | python3 -c "import json,sys; d=json.load(sys.stdin); print(d[0]['Size'])")
        echo "  Source: docker image inspect (uncompressed — conservative upper bound)"
    fi

    python3 - <<PYEOF
import sys
compressed_bytes = ${compressed_bytes}
compressed_gb = compressed_bytes / 1e9
limit_gb = 2.0
print(f"  Size: {compressed_gb:.3f} GB (limit {limit_gb} GB)")
if compressed_gb >= limit_gb:
    print(f"  FAIL  image is {compressed_gb:.3f} GB — must be < {limit_gb} GB (ADDON-05)", file=sys.stderr)
    sys.exit(1)
print(f"  PASS  image is under {limit_gb} GB")
PYEOF

    # ── 3. glibc linkage of orchestrator binary ───────────────────────────────
    echo "Checking: glibc linkage of orchestrator dll..."
    local dll_path="/opt/argus/orchestrator/Argus.Orchestrator.dll"

    if docker run --rm "${tag}" test -f "${dll_path}" 2>/dev/null; then
        # dll present — assert glibc (linux-vdso or libc present in ldd output)
        local ldd_out
        ldd_out=$(docker run --rm "${tag}" ldd "${dll_path}" 2>&1 || true)
        if echo "${ldd_out}" | grep -qE 'linux-vdso|libc\.so|libpthread'; then
            pass "glibc linkage confirmed for Argus.Orchestrator.dll"
        else
            fail "glibc NOT found in ldd output — orchestrator must be glibc-linked (Debian base, not Alpine/musl)"
        fi
    else
        skip "Argus.Orchestrator.dll absent (Phase 1 stub or no publish output) — glibc assertion deferred to Phase 4 CI"
    fi

    echo ""
    echo "=== image: ALL CHECKS PASSED for ${tag} ==="
}

# ─────────────────────────────────────────────────────────────────────────────
# Dispatch
# ─────────────────────────────────────────────────────────────────────────────

MODE="${1:-static}"

case "${MODE}" in
    static)
        run_static
        ;;
    image)
        shift
        run_image "${1:-}"
        ;;
    *)
        echo "Unknown mode: ${MODE}" >&2
        echo "Usage: $0 [static | image <tag>]" >&2
        exit 1
        ;;
esac
