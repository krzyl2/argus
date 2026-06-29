ARG BUILD_FROM=ghcr.io/home-assistant/base-debian:bookworm
FROM ${BUILD_FROM}

# s6-overlay: exit container when any service crashes rather than looping silently.
# Must be set before s6 init reads stage-2 config.
ENV S6_BEHAVIOUR_IF_STAGE2_FAILS=2

# ── .NET 8 Runtime (Microsoft Debian 12 package feed) ─────────────────────────
# Runtime only (not SDK) — orchestrator is published by CI before docker build.
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget ca-certificates \
    && wget -q https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb \
         -O /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && rm /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends dotnet-runtime-8.0 \
    && rm -rf /var/lib/apt/lists/*

# ── Python 3.12 + pip ─────────────────────────────────────────────────────────
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        python3.12 \
        python3-pip \
        python3.12-venv \
    && rm -rf /var/lib/apt/lists/*

# ── Python detector dependencies ──────────────────────────────────────────────
# --prefer-binary: mandatory for aarch64 to avoid scipy/statsmodels source compilation
# Core darts only (no ML-framework extras) — keeps image under 2 GB (ADDON-05)
COPY detector/requirements.txt /tmp/requirements.txt
RUN pip3 install --no-cache-dir --prefer-binary -r /tmp/requirements.txt

# PyYAML is required by gen-entities.py (cont-init.d/10-config-gen.sh calls it to convert
# options.json → /data/entities.yaml). It is NOT in detector/requirements.txt because the
# detector itself does not use YAML — this explicit install keeps the dependency auditable.
RUN pip3 install --no-cache-dir pyyaml

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
ARG BUILD_ARCH
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
