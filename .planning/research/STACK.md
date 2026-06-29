# Stack Research — Argus v2: Home Assistant Add-on Packaging

**Domain:** Home Assistant add-on packaging (.NET 8 + Python 3.12, single container)
**Researched:** 2026-06-29
**Confidence:** MEDIUM (official HA developer docs + ghcr.io release tags; builder action deprecation verified)

---

## Recommended Stack

### Core Technologies

| Technology | Version / Tag | Purpose | Why Recommended |
|------------|--------------|---------|-----------------|
| `ghcr.io/home-assistant/base-debian` | `bookworm` (pinned; latest is `trixie`) | Add-on base image | Debian 12 — .NET 8 has official Debian packages, Python 3.12 is in-repo; Alpine (the other HA base) has musl/glibc incompatibilities with .NET. Bundles s6-overlay v3, bashio, and tempio out of the box. |
| s6-overlay | v3.2.3.0 (bundled in base image) | Process supervision for .NET + Python side-by-side | Already in base image; `/etc/services.d/` compat layout means zero extra install. Handles process restarts, dependency ordering, and container lifecycle. |
| bashio | latest (bundled in base image) | Shell helper for reading options + Supervisor API | Pre-installed; `bashio::config`, `bashio::services` eliminate manual JSON parsing and `curl` calls to the Supervisor. |

### GitHub Actions (CI/CD build)

| Action | Version | Purpose | Why |
|--------|---------|---------|-----|
| `home-assistant/actions/prepare-multi-arch-matrix` | current | Build matrix for amd64 + aarch64 | New composable replacement for deprecated `home-assistant/builder`. |
| `home-assistant/actions/build-image` | current | Per-arch Docker Buildx build + push | Handles GHCR auth, Cosign signing, layer caching. |
| `home-assistant/actions/publish-multi-arch-manifest` | current | Merge per-arch images into one manifest | Produces `ghcr.io/user/argus:1.0.0` covering both arches from `amd64-argus:1.0.0` + `aarch64-argus:1.0.0`. |

---

## Add-on Repository Layout

```
argus-addon/                    ← GitHub repo root (the "custom repository")
├── repository.yaml             ← REQUIRED: identifies this as an add-on repo
├── README.md
└── argus/                      ← add-on subfolder (slug matches config.yaml)
    ├── config.yaml             ← add-on manifest + options schema
    ├── Dockerfile
    ├── DOCS.md
    ├── CHANGELOG.md
    ├── icon.png                ← 250×250px, shown in HA store UI
    └── rootfs/                 ← copied to / inside the container
        └── etc/
            ├── cont-init.d/
            │   └── 01-generate-entities.sh   ← oneshot: writes entities.yaml from options.json
            └── services.d/
                ├── detector/
                │   ├── run       ← longrun: exec python -m argus_detector.server
                │   └── finish
                └── orchestrator/
                    ├── run       ← longrun: exec dotnet Argus.Orchestrator.dll
                    └── finish
```

---

## s6-overlay v3 Service Layout (the `/etc/services.d/` compat path)

The HA base images support two s6-overlay layouts. Use the **compat/v2 layout** (`/etc/services.d/`) — it is what the official HA example add-on uses and requires no additional registration steps.

**File contents:**

```
rootfs/etc/services.d/detector/run
```
```bash
#!/usr/bin/with-contenv sh
set -e
exec python -m argus_detector.server
```

```
rootfs/etc/services.d/orchestrator/run
```
```bash
#!/usr/bin/with-contenv sh
set -e
exec dotnet /app/orchestrator/Argus.Orchestrator.dll
```

```
rootfs/etc/cont-init.d/01-generate-entities.sh
```
```bash
#!/usr/bin/with-contenv bashio
# Runs once before services start. Writes entities.yaml from /data/options.json.
bashio::log.info "Generating entities.yaml from add-on options..."
python /app/scripts/generate_entities.py
```

**Critical:** all `run` and `finish` scripts must have execute permission. Set via git:
```bash
git update-index --chmod=+x rootfs/etc/services.d/detector/run
git update-index --chmod=+x rootfs/etc/services.d/orchestrator/run
git update-index --chmod=+x rootfs/etc/cont-init.d/01-generate-entities.sh
```

The base image sets `ENTRYPOINT ["/init"]` — do not override in the add-on Dockerfile.

---

## config.yaml — Complete Field Reference

```yaml
name: "Argus Anomaly Detection"
description: "Anomaly detection for HA sensors via streaming + batch ML."
version: "2.0.0"
slug: argus
url: "https://github.com/user/argus-addon"
arch:
  - amd64
  - aarch64

# Supervisor integration
init: false                # Required when using s6-overlay (base image provides /init)
homeassistant_api: true    # Enables http://supervisor/core/api/ and ws://supervisor/core/websocket
hassio_api: false          # Not needed; /services/* is accessible without this flag
services:
  - mqtt:want              # Declares MQTT dependency; Supervisor injects broker credentials

# Persistent data
map:
  - type: data             # /data — writable; stores options.json, model files, entities.yaml

options:                   # Default values shown in UI before user edits
  entities: []
  detector_endpoint: ""
  influx_url: ""
  influx_token: ""
  influx_org: ""
  influx_bucket: ""
  influx_measurement: "homeassistant"
  influx_value_field: "value"

schema:                    # Validation rules; type? = optional (may be absent)
  entities:
    - str                  # List of HA entity_id strings (plain str — no native entity selector)
  detector_endpoint: str?  # Empty = use local loopback detector; non-empty = remote + mTLS
  influx_url: url?
  influx_token: password?
  influx_org: str?
  influx_bucket: str?
  influx_measurement: str
  influx_value_field: str
```

### Schema Type System — Complete List

| Type syntax | Meaning |
|-------------|---------|
| `str` | String |
| `str(min,max)` | String with length bounds (either bound may be omitted) |
| `bool` | Boolean |
| `int` / `int(min,max)` | Integer with optional bounds |
| `float` / `float(min,max)` | Float with optional bounds |
| `email` | Email address |
| `url` | URL |
| `password` | Masked input, excluded from logs |
| `port` | Integer 0–65535 |
| `match(REGEX)` | String matching a regex |
| `list(a\|b\|c)` | Enumerated string choices |
| `device` / `device(subsystem=TYPE)` | Hardware device path |
| `TYPE?` | Any type + `?` suffix makes the field optional |

**What does NOT exist:** There is no native entity selector / entity_id picker in the add-on schema type system. Entity IDs must be collected as plain `str` values. The user types or pastes them manually.

---

## Supervisor Auth and MQTT Discovery (replaces v1 manual env vars)

### v1 → v2 mapping

| v1 env var (docker-compose) | v2 replacement |
|-----------------------------|---------------|
| `ARGUS_HA_TOKEN` | `SUPERVISOR_TOKEN` (auto-injected; use as HA bearer token) |
| `ARGUS_HA_URL` | Fixed: `http://supervisor/core/api/` / `ws://supervisor/core/websocket` |
| `ARGUS_MQTT_HOST` | `bashio::services "mqtt" "host"` |
| `ARGUS_MQTT_PORT` | `bashio::services "mqtt" "port"` |
| `ARGUS_MQTT_USER` | `bashio::services "mqtt" "username"` |
| `ARGUS_MQTT_PASSWORD` | `bashio::services "mqtt" "password"` |

### How to use in the init script

```bash
#!/usr/bin/with-contenv bashio

MQTT_HOST=$(bashio::services "mqtt" "host")
MQTT_PORT=$(bashio::services "mqtt" "port")
MQTT_USER=$(bashio::services "mqtt" "username")
MQTT_PASS=$(bashio::services "mqtt" "password")

# Read user options
ENTITIES=$(bashio::config 'entities')
DETECTOR_ENDPOINT=$(bashio::config 'detector_endpoint')
INFLUX_URL=$(bashio::config 'influx_url')
```

Or directly from `/data/options.json` using jq (usable from .NET startup code):
```bash
INFLUX_TOKEN=$(jq --raw-output '.influx_token // empty' /data/options.json)
```

The orchestrator can read `SUPERVISOR_TOKEN` as `Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN")` and `/data/options.json` directly (parse with `System.Text.Json`).

**API endpoints (no extra config needed once `homeassistant_api: true`):**
- HA REST: `http://supervisor/core/api/`
- HA WebSocket: `ws://supervisor/core/websocket`
- Supervisor services: `http://supervisor/services/mqtt` (GET, Bearer SUPERVISOR_TOKEN)

---

## Dockerfile Pattern (multi-arch add-on)

```dockerfile
ARG BUILD_FROM=ghcr.io/home-assistant/base-debian:bookworm
FROM ${BUILD_FROM}

# Install .NET 8 runtime (Microsoft Debian feed)
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \
    && wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends dotnet-runtime-8.0 \
    && rm -rf /var/lib/apt/lists/*

# Install Python 3.12 + pip (available in bookworm)
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3.12 python3-pip python3.12-venv \
    && rm -rf /var/lib/apt/lists/*

# Install Python detector dependencies
COPY detector/requirements.txt /tmp/requirements.txt
RUN pip3 install --no-cache-dir -r /tmp/requirements.txt

# Copy orchestrator publish output (built in CI, not in this Dockerfile)
COPY orchestrator/publish/ /app/orchestrator/

# Copy detector source + pre-generated proto stubs
COPY detector/ /app/detector/

# Copy proto stubs (pre-generated; no runtime codegen)
COPY proto/ /app/proto/

# Copy s6-overlay service scripts and init scripts
COPY rootfs/ /

# Build labels (populated by build-image action)
ARG BUILD_ARCH
ARG BUILD_DATE
ARG BUILD_REF
ARG BUILD_VERSION
LABEL \
    io.hass.name="Argus Anomaly Detection" \
    io.hass.description="Streaming + batch anomaly detection for HA sensors" \
    io.hass.arch="${BUILD_ARCH}" \
    io.hass.type="addon" \
    io.hass.version="${BUILD_VERSION}"
```

**Note:** The base image sets `ENTRYPOINT ["/init"]`. Do not add ENTRYPOINT or CMD.

---

## repository.yaml

```yaml
name: "Argus Add-on Repository"
url: "https://github.com/krawczyk/argus-addon"
maintainer: "Krzysztof Krawczyk <k.krawczyk@it-ray.pl>"
```

This file lives at the git repo root. HA Supervisor reads it to display the repository name in the add-on store. Users add the repository URL in HA UI: Settings > Add-ons > Add-on Store > triple-dot > Repositories.

---

## Multi-Arch Build (GitHub Actions)

```yaml
# .github/workflows/build.yml
on:
  release:
    types: [published]

jobs:
  init:
    runs-on: ubuntu-latest
    outputs:
      matrix: ${{ steps.matrix.outputs.matrix }}
    steps:
      - uses: home-assistant/actions/prepare-multi-arch-matrix@main
        id: matrix
        with:
          architectures: '["amd64", "aarch64"]'
          image: ghcr.io/${{ github.repository_owner }}/argus

  build:
    needs: init
    runs-on: ubuntu-latest
    strategy:
      matrix: ${{ fromJSON(needs.init.outputs.matrix) }}
    steps:
      - uses: actions/checkout@v4
      - uses: home-assistant/actions/build-image@main
        with:
          arch: ${{ matrix.arch }}
          image: ${{ matrix.image }}
          push: true
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}

  manifest:
    needs: build
    runs-on: ubuntu-latest
    steps:
      - uses: home-assistant/actions/publish-multi-arch-manifest@main
        with:
          image: ghcr.io/${{ github.repository_owner }}/argus
          version: ${{ github.event.release.tag_name }}
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

**Architectures in config.yaml** must match what the CI builds:
```yaml
arch:
  - amd64
  - aarch64
```

---

## Alternatives Considered

| Recommended | Alternative | Why Not |
|-------------|-------------|---------|
| `base-debian:bookworm` | `base:3.24` (Alpine) | Alpine uses musl libc; .NET 8 officially supports musl but requires extra build flags and some .NET libs assume glibc. Adds friction. Use Debian. |
| `base-debian:bookworm` | `base-python:3.12-alpine3.24` | Same musl issue; Python would be fine but .NET wouldn't be. |
| `base-debian:bookworm` | Build from `python:3.12-slim-bookworm` and install s6 manually | Requires manually installing s6-overlay, bashio, tempio; defeats the purpose of the HA base images which provide Supervisor integration. |
| `/etc/services.d/` (compat) | `/etc/s6-overlay/s6-rc.d/` (native v3) | Both work; compat path is simpler (no `type` file, no `contents.d` registration) and is what the official HA example uses. Use native v3 only if you need `dependencies.d` ordering between the two services (e.g., ensure detector starts before orchestrator). |
| New composable GHA actions | `home-assistant/builder` action | Deprecated since 2026.02.1; will be removed. |

---

## What NOT to Use

| Avoid | Why | Use Instead |
|-------|-----|-------------|
| `home-assistant/builder` GHA action | Deprecated 2026.02.1, will be removed | Composable `home-assistant/actions/*` actions |
| `amd64-base`, `aarch64-base` arch-prefixed image names | Deprecated since 2026.03.1; superseded by multi-arch images | `ghcr.io/home-assistant/base-debian:bookworm` (multi-arch) |
| `build.yaml` (legacy add-on build config) | No longer read by the new composable builder actions | Set `FROM ${BUILD_FROM}` directly in Dockerfile; build args via GitHub Actions inputs |
| `hassio_api: true` | Not required for MQTT service discovery (`/services/*` is open) | Omit unless you need deeper Supervisor API access |
| Manual `ARGUS_HA_TOKEN` / `ARGUS_HA_URL` env vars in options schema | Replaces by Supervisor-provided `SUPERVISOR_TOKEN` + fixed proxy URLs | `SUPERVISOR_TOKEN` env + `homeassistant_api: true` |
| Manual MQTT credentials in options schema | MQTT add-on exposes credentials via Services API | `services: [mqtt:want]` + `bashio::services "mqtt" "*"` |
| Entity selector in schema | Does not exist | `str` type; users paste entity_id strings manually or list type if set is known |
| ML.NET | Excluded (PROJECT.md D2) | Python detector (already built) |

---

## Version Compatibility

| Component | Version | Compatible With |
|-----------|---------|----------------|
| `ghcr.io/home-assistant/base-debian` | `bookworm` (2026.06.1 release) | .NET 8 runtime (Debian 12 packages), Python 3.12 (Debian 12 repos) |
| s6-overlay | v3.2.3.0 (bundled in base) | `/etc/services.d/` compat layout (v2-style scripts work unchanged) |
| .NET runtime | 8.0 | Debian 12 (bookworm) packages from packages.microsoft.com |
| Python | 3.12 | Debian 12 bookworm (`python3.12` package) |
| grpcio | 1.81.0 (existing pin) | Python 3.12, glibc (Debian — no musl issues) |

---

## Sources

- [Home Assistant Developer Docs — App Configuration](https://developers.home-assistant.io/docs/apps/configuration/) — config.yaml fields, schema types, services field
- [Home Assistant Developer Docs — App Communication](https://developers.home-assistant.io/docs/add-ons/communication/) — SUPERVISOR_TOKEN, homeassistant_api, Services API endpoints
- [Home Assistant Developer Docs — App Repository](https://developers.home-assistant.io/docs/apps/repository/) — repository.yaml structure
- [github.com/home-assistant/docker-base](https://github.com/home-assistant/docker-base) — base image variants, release 2026.06.1 (June 2026)
- [github.com/just-containers/s6-overlay](https://github.com/just-containers/s6-overlay) — v3.2.3.0 (May 2026), directory layout
- [github.com/hassio-addons/bashio — lib/services.sh](https://github.com/hassio-addons/bashio/blob/main/lib/services.sh) — bashio::services function signatures
- [github.com/home-assistant/addons-example](https://github.com/home-assistant/addons-example) — official example using `services.d/` compat layout + `#!/usr/bin/with-contenv bashio`
- [github.com/marketplace/actions/home-assistant-builder](https://github.com/marketplace/actions/home-assistant-builder) — builder deprecation notice, composable actions
- [HA Developer Blog — S6-Overlay v3 update](https://developers.home-assistant.io/blog/2022/05/12/s6-overlay-base-images/) — `init: false` requirement

---
*Stack research for: Argus v2 Home Assistant Add-on packaging*
*Researched: 2026-06-29*
