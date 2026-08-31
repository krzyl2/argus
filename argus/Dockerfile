# BUILD_FROM must be declared as a global ARG before the first FROM — a stage-scoped
# ARG declared between FROM lines is NOT visible to the next FROM (Docker constraint).
ARG BUILD_FROM=ghcr.io/home-assistant/base-debian:bookworm

# ── Stage 1: build the SPA (Node, discarded) ──────────────────────────────────
# Pinned to BUILDPLATFORM: the SPA output is plain JS/CSS, byte-identical for every
# target arch, so emulating this stage under QEMU for the arm64 leg only costs time.
FROM --platform=$BUILDPLATFORM node:20-alpine AS ui-build
WORKDIR /src/ui
COPY orchestrator/ui/package.json orchestrator/ui/package-lock.json ./
RUN npm ci
COPY orchestrator/ui/ ./
RUN npm run build
# vite.config.ts sets outDir to ../Argus.Orchestrator/wwwroot (relative to /src/ui),
# i.e. Vite writes directly to /src/Argus.Orchestrator/wwwroot — NOT /src/ui/dist/.
# Output: /src/Argus.Orchestrator/wwwroot/ (index.html, assets/*.js, css/argus.css)

# ── Stage 2: publish the .NET orchestrator (SDK, discarded) ──────────────────
# Pinned to BUILDPLATFORM. The publish is framework-dependent and RID-less, and the s6
# run script starts it as `dotnet Argus.Orchestrator.dll`, so the output is portable IL:
# the same files are correct in both the amd64 and the arm64 image. Running the SDK
# under QEMU for the arm64 leg therefore bought nothing — and killed the build:
#   qemu : error : uncaught target signal 11 (Segmentation fault) - core dumped
# Keep this pinned unless the app starts needing a per-arch apphost or a native RID.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet-build
WORKDIR /src
COPY orchestrator/ ./orchestrator/
COPY proto/ ./proto/
# Vite build output must land in wwwroot/ BEFORE dotnet publish runs, since the Web
# SDK's static-web-assets pipeline snapshots wwwroot/ contents at publish time.
COPY --from=ui-build /src/Argus.Orchestrator/wwwroot/ ./orchestrator/Argus.Orchestrator/wwwroot/
RUN dotnet publish orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj \
    -c Release --self-contained false -o /app/publish

# ── Stage 3: runtime ────────────────────────────────────────────────────────
FROM ${BUILD_FROM}

# s6-overlay: exit container when any service crashes rather than looping silently.
# Must be set before s6 init reads stage-2 config.
ENV S6_BEHAVIOUR_IF_STAGE2_FAILS=2

# ── .NET 8 Runtime (official dotnet-install.sh, NOT the apt feed) ──────────────
# ASP.NET Core runtime (not SDK) — orchestrator is a Web SDK app (v3 Ingress UI)
# and needs Microsoft.AspNetCore.App; --runtime aspnetcore also carries the base
# Microsoft.NETCore.App runtime. Orchestrator is published inside this image by
# the dotnet-build stage above (see Stage 2).
# The packages.microsoft.com Debian 12 feed only ships dotnet-runtime-8.0 for
# amd64; the arm64 component has 10.0 only, so apt install fails on aarch64.
# dotnet-install.sh pulls the arch-correct 8.0 runtime from the global CDN and
# is feed-independent. Native runtime deps come from Debian's own repos.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        wget ca-certificates \
        libicu72 libssl3 zlib1g libstdc++6 \
    && wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 8.0 --runtime aspnetcore --install-dir /usr/share/dotnet \
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

# ── Orchestrator publish output (built in the dotnet-build stage above) ────────
COPY --from=dotnet-build /app/publish/ /opt/argus/orchestrator/

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
