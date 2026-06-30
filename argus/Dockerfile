ARG BUILD_FROM=ghcr.io/home-assistant/base-debian:bookworm
FROM ${BUILD_FROM}

# s6-overlay: exit container when any service crashes rather than looping silently.
# Must be set before s6 init reads stage-2 config.
ENV S6_BEHAVIOUR_IF_STAGE2_FAILS=2

# ── .NET 8 Runtime (official dotnet-install.sh, NOT the apt feed) ──────────────
# Runtime only (not SDK) — orchestrator is published by CI before docker build.
# The packages.microsoft.com Debian 12 feed only ships dotnet-runtime-8.0 for
# amd64; the arm64 component has 10.0 only, so apt install fails on aarch64.
# dotnet-install.sh pulls the arch-correct 8.0 runtime from the global CDN and
# is feed-independent. Native runtime deps come from Debian's own repos.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        wget ca-certificates \
        libicu72 libssl3 zlib1g libstdc++6 \
    && wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 8.0 --runtime dotnet --install-dir /usr/share/dotnet \
    && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && rm /tmp/dotnet-install.sh \
    && rm -rf /var/lib/apt/lists/*

# ── Python 3.11 (Debian bookworm native) + pip ────────────────────────────────
# bookworm's apt ships Python 3.11 (no python3.12 package exists on any Debian
# stable). 3.11 is what these detector deps have the broadest aarch64 binary
# wheels for (ADDON-04: avoids source compilation). The standalone detector
# image (deploy/) still uses the official python:3.12 base.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        python3 \
        python3-pip \
        python3-venv \
    && rm -rf /var/lib/apt/lists/*

# ── Python detector dependencies ──────────────────────────────────────────────
# --prefer-binary: pull manylinux aarch64 wheels, never source-compile (ADDON-04,
# verified locally — arm64 build pulls 0 source builds). Deps: grpcio stack, river,
# pyod, numpy/pandas/scipy — no torch (keeps image < 2 GB, ADDON-05).
COPY detector/requirements.txt /tmp/requirements.txt
RUN pip3 install --no-cache-dir --prefer-binary --break-system-packages -r /tmp/requirements.txt

# PyYAML is required by gen-entities.py (cont-init.d/10-config-gen.sh calls it to convert
# options.json → /data/entities.yaml). It is NOT in detector/requirements.txt because the
# detector itself does not use YAML — this explicit install keeps the dependency auditable.
RUN pip3 install --no-cache-dir --break-system-packages pyyaml

# ── Orchestrator publish output (built by CI before docker build) ──────────────
# Phase 1: create a stub directory so the COPY succeeds even without a real publish.
# Phase 4 CI provides real dotnet publish output here.
COPY orchestrator/publish/ /opt/argus/orchestrator/

# ── Detector source + pre-generated proto stubs ───────────────────────────────
COPY detector/ /opt/argus/detector/

# ── s6 init scripts, service run files, and helper scripts ────────────────────
# Includes:
#   etc/cont-init.d/10-config-gen.sh  — options.json → s6 env + entities.yaml
#   etc/services.d/detector/          — Phase 3
#   etc/services.d/orchestrator/      — Phase 3
#   usr/local/bin/gen-entities.py     — entity YAML generator
#   usr/local/bin/wait-detector.py    — Phase 3 health gate
COPY argus/rootfs/ /

# ── Build labels (injected by the composable build-image GHA action) ──────────
# BUILD_ARCH defaults to buildx's automatic per-platform TARGETARCH arg so the
# io.hass.arch label is correct for each manifest entry (amd64/arm64) without a
# static CI build-arg — a single static value cannot be per-arch-correct in a
# multi-platform buildx build.
ARG TARGETARCH
ARG BUILD_ARCH=${TARGETARCH}
ARG BUILD_DATE
ARG BUILD_REF
ARG BUILD_VERSION
LABEL \
    io.hass.name="Argus Anomaly Detection" \
    io.hass.description="Streaming + batch anomaly detection for HA sensors." \
    io.hass.arch="${BUILD_ARCH}" \
    io.hass.type="addon" \
    io.hass.version="${BUILD_VERSION}"

# Base image sets ENTRYPOINT ["/init"] — do NOT add CMD or ENTRYPOINT here.
# Adding either would shadow the s6 init entry point and prevent service startup.
