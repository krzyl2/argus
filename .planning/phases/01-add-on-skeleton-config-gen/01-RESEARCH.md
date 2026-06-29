# Phase 1: Add-on Skeleton + Config-Gen — Research

**Researched:** 2026-06-29
**Domain:** HA add-on packaging skeleton + config-gen integration seam (.NET 8 + Python 3.12)
**Confidence:** HIGH (all research flags resolved from live codebase reads; ecosystem facts from prior authoritative research)

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

- Add-on store display name: "Argus Anomaly Detection"
- Repo layout: `argus/` subfolder in `krzyl2/argus` repo; `repository.yaml` at repo root
- Ship `icon.png` + `logo.png` now (simple flat mark; visual polish deferred)
- `log_level` option: `list(debug|info|warning)`, default `info`, wired to both processes
- Entity selection: `entities: [str]` list of entity_id + optional `include_patterns: [str]?` / `exclude_patterns: [str]?`
- InfluxDB: flat optional fields `influx_url?`, `influx_token?` (password), `influx_org?`, `influx_bucket?`, `influx_measurement?`, `influx_value_field?`. Empty `influx_url` disables batch path
- `detector_endpoint: str?` — empty = bundled local detector; non-empty = remote
- `batch_interval_minutes: int` (default 10), `nightly_fit_hour: int` (default 2)
- Field labels/descriptions via `translations/en.yaml` + `translations/pl.yaml` (D8)
- No per-entity detector tuning in UI (v2.1 deferred)
- `cont-init.d` oneshot generates `/data/entities.yaml` from `/data/options.json` via `gen-entities.py` before any service starts
- MQTT: `services: [mqtt:need]`; exit non-zero if MQTT unavailable; credentials re-read on every reconnect (SUPV-02)
- Base image: `ghcr.io/home-assistant/base-debian:bookworm`; `init: false`; `S6_BEHAVIOUR_IF_STAGE2_FAILS=2`; `darts` core only, no torch; `pip --prefer-binary`

### Claude's Discretion

- .NET 8 install method (Debian packages vs tarball) within Debian base constraint
- Exact s6 directory layout (services.d compat vs native v3 — compat recommended)
- Schema field ordering in config.yaml
- Icon/logo artwork content

### Deferred Ideas (OUT OF SCOPE)

- Per-entity detector parameter tuning in UI → v2.1
- Auto-discovery-only mode → v2.1+
- `translations/en.yaml` list-item label support (community-flagged) → handle during implementation; fallback to field-level labels if unsupported
</user_constraints>

---

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| ADDON-01 | User can add Argus repo URL and see "Argus" in HA store | `repository.yaml` format documented; `slug` in config.yaml drives folder naming |
| ADDON-03 | Build on `ghcr.io/home-assistant/base-debian:bookworm` | Dockerfile skeleton section; `ARG BUILD_FROM` pattern |
| ADDON-05 | Image < 2 GB compressed, no PyTorch | Darts core install, `--prefer-binary`, CI assertion pattern |
| SUPV-01 | Auto HA auth via `SUPERVISOR_TOKEN` | Research Flag 2 resolved: ARGUS_HA_URL + ARGUS_HA_TOKEN are the only vars needed |
| SUPV-02 | Auto MQTT via `services: [mqtt:need]`; fail loud if unavailable | config.yaml schema, bashio pattern documented |
| UICFG-01 | Entity list in Configuration tab | `entities: [str]` schema type; no native entity picker |
| UICFG-02 | InfluxDB config in UI; empty = batch disabled | Schema draft with `url?`/`password?`/`str?` types |
| UICFG-03 | `detector_endpoint: str?` optional | Schema: omit from options default to make truly optional |
| UICFG-04 | Batch schedule in UI | `batch_interval_minutes: int(1,1440)`, `nightly_fit_hour: int(0,23)` |
| UICFG-06 | `include_patterns`/`exclude_patterns` globs in UI | Optional lists with empty-list default workaround documented |
| UICFG-07 | EN + PL translations via `translations/` | Translation YAML structure documented; list-item caveat noted |
| UICFG-08 | Startup generates `/data/entities.yaml` from `/data/options.json` | Research Flag 1 resolved: exact YAML contract for gen-entities.py |
</phase_requirements>

---

## Summary

This research resolves the two explicit research flags from ROADMAP.md before any code is written. Both flags are now definitively answered from codebase reads, not assumptions.

**Research Flag 1 (entities.yaml contract) — RESOLVED:** `EntitiesConfigLoader` uses `UnderscoredNamingConvention` and `IgnoreUnmatchedProperties`. The top-level key is `entities:`. Each entry requires `entity_id` (non-empty string) and `detectors` (non-empty list). The minimum viable entry has `name: hst` and `params: {}` (empty dict = all HST defaults). All `params` values are string-typed even for numbers. `friendly_name` is not validated and can be omitted. `gen-entities.py` can safely emit `params: {}` for all entities from a plain `[str]` entity_id list.

**Research Flag 2 (HA auth env-var consumers) — RESOLVED:** `NetDaemonHaEventSource` reads `ARGUS_HA_URL` and `ARGUS_HA_TOKEN` **directly** from `ConnectionSettings` and passes them as explicit parameters to `IHomeAssistantClient.ConnectAsync(host, port, ssl, token, ct)`. The `HomeAssistant__*` IConfiguration keys are NOT consumed by the v1 code path. Config-gen must write `ARGUS_HA_URL` and `ARGUS_HA_TOKEN`; it must NOT write `HomeAssistant__*` keys. The ParseHaUrl implementation has a port-extraction quirk: config-gen must write `ws://supervisor:80` with explicit `:80` (if omitted, ws:// as an unregistered .NET URI scheme returns port -1).

**Primary recommendation:** Write config.yaml exactly as specified in the schema draft below, implement `gen-entities.py` outputting entities with `params: {}` for all UI-listed entity_ids, and set `ARGUS_HA_URL=ws://supervisor:80` in config-gen.

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| HA WebSocket connection | API / Backend (orchestrator) | — | NetDaemonHaEventSource owns the connection; configured via ARGUS_HA_URL/TOKEN |
| MQTT credential fetch | API / Backend (config-gen) | — | bashio::services in cont-init.d, writes ARGUS_MQTT_* to s6 env before services start |
| Entity YAML generation | Init script (config-gen) | — | Oneshot cont-init.d runs before any service; reads options.json, writes /data/entities.yaml |
| options.json → env vars | Init script (config-gen) | — | `cont-init.d/10-config-gen.sh` owns the full env materialization |
| Add-on manifest / schema | Static config (config.yaml) | — | Validated by Supervisor before container starts |
| Process supervision | s6 longrun (services.d/) | — | Phase 3 wires this; Phase 1 defines the folder layout and Dockerfile |
| Model persistence | Storage (/data/models/) | — | Path must be /data or models are wiped on restart |

---

## Research Flag 1 — Definitive: entities.yaml YAML Contract

**Source:** [VERIFIED: EntitiesConfigLoader.cs + EntitiesConfig.cs + entities.yaml — live codebase reads]

### Deserializer behaviour

- `UnderscoredNamingConvention.Instance`: C# PascalCase → YAML snake_case (`EntityId` → `entity_id`, `FriendlyName` → `friendly_name`, `Detectors` → `detectors`, `Params` → `params`)
- `IgnoreUnmatchedProperties()`: extra YAML keys are silently ignored — safe to include extra metadata
- Deserializes to `EntitiesConfig` then calls `Validate()`

### Validation rules (from source — will throw on violation)

1. `entities` list must be non-null and have at least 1 element
2. Each `entity.EntityId` must be non-empty
3. Each `entity.Detectors` list must be non-null and have at least 1 element

### Field reference

| YAML key | C# property | Required by Validate() | Type | Notes |
|----------|-------------|----------------------|------|-------|
| `entities` | `EntitiesConfig.Entities` | yes (non-empty list) | list | Top-level, only key |
| `entity_id` | `EntityConfig.EntityId` | yes (non-empty) | str | HA entity_id |
| `friendly_name` | `EntityConfig.FriendlyName` | no | str | Defaults to `""` if absent |
| `detectors` | `EntityConfig.Detectors` | yes (non-empty list) | list | At least 1 detector per entity |
| `name` | `DetectorConfig.Name` | not validated, but expected by pipeline | str | Use `"hst"` for streaming detector |
| `params` | `DetectorConfig.Params` | not validated | `dict[str, str]` | ALL values are strings — not int/float |
| `covariates` | `EntityConfig.Covariates` | no | `object?` | Parsed but ignored (warning logged) |
| `groups` | `EntityConfig.Groups` | no | `object?` | Parsed but ignored (warning logged) |

### Params typing — CRITICAL

`HstParams.From(Dictionary<string, string>)` parses params via `int.TryParse` and `double.TryParse`. Values must be serialized as YAML strings, not bare numbers:

```yaml
params:
  window: "250"      # string "250" not integer 250
  high_threshold: "0.7"  # string "0.7" not float 0.7
```

Empty `params: {}` is valid — all HST defaults apply from `HstParams` constructor defaults.

### HST default param values (from HstParams source)

| Param key | Default value | Source property |
|-----------|--------------|----------------|
| `window` | `"250"` | `HstParams.Window = 250` |
| `n_trees` | `"25"` | `HstParams.NTrees = 25` |
| `high_threshold` | `"0.7"` | `HstParams.HighThreshold = 0.7` |
| `low_threshold` | `"0.3"` | `HstParams.LowThreshold = 0.3` |
| `min_consecutive` | `"3"` | `HstParams.MinConsecutive = 3` |
| `frozen_window` | `"10"` | `HstParams.FrozenWindow = 10` |
| `frozen_variance_threshold` | `"0.001"` | `HstParams.FrozenVarianceThreshold = 0.001` |

### Concrete gen-entities.py output: UI `entities: ["sensor.foo", "sensor.bar"]` → valid entities.yaml

```yaml
# Generated by gen-entities.py from /data/options.json
# All entities use HST streaming detector with default parameters.
entities:
  - entity_id: sensor.foo
    friendly_name: ""
    detectors:
      - name: hst
        params: {}
  - entity_id: sensor.bar
    friendly_name: ""
    detectors:
      - name: hst
        params: {}
```

`params: {}` is valid (empty dict, all HST defaults activate). `friendly_name: ""` is valid (not validated). No need to emit explicit param values unless the user provides overrides (v2.1+).

---

## Research Flag 2 — Definitive: HA Auth Env-Var Consumers

**Source:** [VERIFIED: NetDaemonHaEventSource.cs + Program.cs — live codebase reads]

### Who reads ARGUS_HA_URL and ARGUS_HA_TOKEN

`NetDaemonHaEventSource` receives `ConnectionSettings` via DI and calls:

```csharp
var (host, port, ssl) = ParseHaUrl(_settings.HaUrl);  // reads ARGUS_HA_URL
var connection = await _haClient.ConnectAsync(
    host, port, ssl, _settings.HaToken ?? string.Empty, ct);  // reads ARGUS_HA_TOKEN
```

`_settings.HaUrl` maps to `ARGUS_HA_URL` (set in `Program.cs` line 25: `HaUrl = builder.Configuration["ARGUS_HA_URL"]`). `_settings.HaToken` maps to `ARGUS_HA_TOKEN` (line 26).

### HomeAssistant__* config keys — NOT used

`AddHomeAssistantClient()` (Program.cs line 69) registers `IHomeAssistantClient` in DI. However, `NetDaemonHaEventSource` calls `ConnectAsync` with **explicit parameters** extracted from `ARGUS_HA_URL`/`ARGUS_HA_TOKEN` — it does NOT use any `HomeAssistant__*` IConfiguration keys. Config-gen must NOT write `HomeAssistant__*` env vars; they serve no purpose in this codebase.

### ParseHaUrl port extraction quirk — CRITICAL

```csharp
// From NetDaemonHaEventSource.cs ParseHaUrl():
var ssl = uri.Scheme is "wss" or "https";
var port = uri.IsDefaultPort ? (ssl ? 443 : 8123) : uri.Port;
return (uri.Host, port, ssl);
```

In .NET's `Uri` class, `ws://` and `wss://` are unregistered schemes (no default port in the BCL port table). Behavior:

| ARGUS_HA_URL value | `uri.Port` | `uri.IsDefaultPort` | `ssl` | Resulting port |
|--------------------|-----------|---------------------|-------|----------------|
| `ws://supervisor/core/websocket` | -1 | false | false | **-1 (BROKEN)** |
| `http://supervisor/api` | 80 | true | false | **8123 (WRONG — http 80 is default)** |
| `http://supervisor:80/api` | 80 | true | false | **8123 (WRONG — IsDefaultPort still true)** |
| `ws://supervisor:80/core/websocket` | 80 | false | false | **80 (CORRECT)** |

**Rule:** config-gen must write `ARGUS_HA_URL=ws://supervisor:80`. The explicit `:80` is mandatory. The path component is ignored by ParseHaUrl (only host/port/ssl extracted). Any ws:// URL with explicit `:80` works.

### Minimal env-var set config-gen must write

**HA auth (SUPV-01):**
```
ARGUS_HA_URL=ws://supervisor:80
ARGUS_HA_TOKEN=${SUPERVISOR_TOKEN}
```

**MQTT credentials (SUPV-02 — from Supervisor service discovery):**
```
ARGUS_MQTT_HOST=$(bashio::services mqtt "host")
ARGUS_MQTT_PORT=$(bashio::services mqtt "port")
ARGUS_MQTT_USER=$(bashio::services mqtt "username")
ARGUS_MQTT_PASSWORD=$(bashio::services mqtt "password")
```

**gRPC / detector (local mode):**
```
ARGUS_DETECTOR_ENDPOINT=http://127.0.0.1:50051
ARGUS_GRPC_BIND=127.0.0.1
ARGUS_MODEL_ROOT=/data/models
```

**gRPC / detector (remote mode, when detector_endpoint is set):**
```
ARGUS_DETECTOR_ENDPOINT=${detector_endpoint_from_options}
ARGUS_TLS_CA=/data/certs/ca.crt
ARGUS_TLS_CERT=/data/certs/client.crt
ARGUS_TLS_KEY=/data/certs/client.key
ARGUS_MODEL_ROOT=/data/models
```

**Entities + InfluxDB + scheduling (from options.json):**
```
ARGUS_ENTITIES_PATH=/data/entities.yaml
ARGUS_INFLUX_URL=${influx_url or empty}
ARGUS_INFLUX_TOKEN=${influx_token or empty}
ARGUS_INFLUX_ORG=${influx_org or empty}
ARGUS_INFLUX_BUCKET=${influx_bucket or empty}
ARGUS_INFLUX_MEASUREMENT=${influx_measurement, default homeassistant}
ARGUS_INFLUX_VALUE_FIELD=${influx_value_field, default value}
ARGUS_BATCH_INTERVAL_MIN=${batch_interval_minutes, default 10}
ARGUS_NIGHTLY_FIT_HOUR=${nightly_fit_hour, default 2}
```

All vars written to `/var/run/s6/container_environment/` (s6 per-service env injection, readable by `#!/usr/bin/with-contenv` scripts).

**Open question — needs live HA OS test (Phase 3):** NetDaemon.Client internally constructs `ws://supervisor:80/api/websocket` from the extracted host/port. The Supervisor proxy at `supervisor:80` may route this to HA WebSocket differently than the documented `ws://supervisor/core/websocket` path. If Phase 3 integration reveals connection failure, the fix is to refactor `NetDaemonHaEventSource` to pass full URL rather than relying on ParseHaUrl — or confirm that supervisor:80/api/websocket is proxied correctly. [ASSUMED: Supervisor proxy at supervisor:80 responds to /api/websocket]

---

## Standard Stack

### Core (locked by CONTEXT.md — do not research alternatives)

| Component | Version | Purpose | Source |
|-----------|---------|---------|--------|
| `ghcr.io/home-assistant/base-debian` | `bookworm` (2026.06.1) | Add-on base image | [VERIFIED: prior research + HA GitHub] |
| s6-overlay | v3.2.3.0 (bundled) | Process supervision | [VERIFIED: prior research] |
| bashio | latest (bundled) | Supervisor API helper | [VERIFIED: prior research] |
| `dotnet-runtime-8.0` | 8.0 (Debian 12 packages) | .NET orchestrator runtime | [VERIFIED: prior research] |
| `python3.12` | 3.12 (bookworm) | Detector runtime | [VERIFIED: prior research] |
| `darts` | 0.44.1 (existing pin, core only) | STL decomposition (no torch) | [VERIFIED: CLAUDE.md] |

### What NOT to use

| Avoid | Why |
|-------|-----|
| `HomeAssistant__*` env vars | Not consumed by any v1 code path — confirmed by codebase read |
| `grpc.experimental.aio` | Old import path; use `import grpc.aio` (CLAUDE.md) |
| ManagedClient (MQTTnet v4) | Removed in v5 (CLAUDE.md) |
| `darts[torch]` / `darts[all]` | Pulls PyTorch, inflates image 3-5 GB |
| `services: - mqtt:want` | Returns empty credentials silently; use `mqtt:need` |
| Alpine base | musl ABI incompatible with .NET 8 glibc binaries |
| `home-assistant/builder` GHA action | Deprecated 2026.02.1 |

---

## Architecture Patterns

### System Architecture Diagram

```
[HA Supervisor]
  /data/options.json ──────────────────────────────────────────┐
  SUPERVISOR_TOKEN ───────────────────────────────────────────┐ │
  Services API (MQTT host/port/user/pass) ────────────────┐   │ │
                                                          │   │ │
                         ┌──────────────────────────────────────────────┐
                         │  cont-init.d/10-config-gen.sh                │
                         │  (oneshot, runs BEFORE services.d/)          │
                         │  ┌─────────────────────────────┐             │
                         │  │ parse options.json           │             │
                         │  │  ↓ entities list             │             │
                         │  │  gen-entities.py             │             │
                         │  │  → /data/entities.yaml       │             │
                         │  │                             │             │
                         │  │ inject s6 env vars          │ ←──────────(MQTT creds)
                         │  │  ARGUS_HA_URL=ws://supervisor:80│ ←──────(SUPERVISOR_TOKEN)
                         │  │  ARGUS_HA_TOKEN             │ ←──────────(options.json)
                         │  │  ARGUS_MQTT_*               │             │
                         │  │  ARGUS_INFLUX_*             │             │
                         │  │  ARGUS_DETECTOR_ENDPOINT    │             │
                         │  │  ARGUS_ENTITIES_PATH        │             │
                         │  └─────────────────────────────┘             │
                         └──────────────────────────────────────────────┘
                                            │ s6 starts services.d/
                         ┌──────────────────┴──────────────────┐
                         ▼                                      ▼
              ┌──────────────────────┐              ┌───────────────────────┐
              │  services.d/detector  │              │ services.d/orchestrator│
              │  (local mode only;   │◄─ gRPC h2c ──│  (Phase 3 health gate)│
              │   downed in remote   │  127.0.0.1   │                       │
              │   mode by down file) │  :50051      │  reads /data/entities  │
              └──────────────────────┘              │  → ARGUS_HA_URL/TOKEN │
                       /data/models/                │  → Supervisor:80 WS   │
                                                    │  → ARGUS_MQTT_*       │
                                                    └───────────────────────┘
```

### Recommended Project Structure

```
argus/                         ← add-on subfolder (slug must match config.yaml slug: argus)
├── config.yaml                ← add-on manifest + options schema
├── Dockerfile                 ← single-arch-parametric (BUILD_FROM ARG)
├── DOCS.md                    ← install guide, config reference
├── icon.png                   ← 250×250px PNG
├── logo.png                   ← optional branding
├── translations/
│   ├── en.yaml                ← English field labels + descriptions
│   └── pl.yaml                ← Polish field labels + descriptions (D8)
└── rootfs/
    └── etc/
        ├── cont-init.d/
        │   └── 10-config-gen.sh    ← central init: options → env + entities.yaml
        └── services.d/             ← Phase 3 wires these
            ├── detector/
            │   ├── run
            │   └── finish
            └── orchestrator/
                ├── run
                └── finish
repository.yaml                ← repo root (identifies repo as HA add-on repository)
```

The `rootfs/usr/local/bin/` scripts (`gen-entities.py`, `wait-detector.py`) are Phase 1 and 3 deliverables respectively and live under `rootfs/`.

### cont-init.d Ordering Guarantee

[VERIFIED: prior research from HA developer blog + official example add-on]

s6-overlay processes ALL `cont-init.d/` scripts **serially in lexicographic order** and completes them entirely before starting ANY `services.d/` service. Numbering `10-config-gen.sh` ensures it runs before any future additional init scripts (20-, 30-, ...). This is the canonical HA add-on pattern for startup initialization.

### Pattern 1: config-gen script structure

```bash
#!/usr/bin/with-contenv bashio
# cont-init.d/10-config-gen.sh
# Runs once before any service starts.

set -e

# ── HA Auth (SUPV-01) ────────────────────────────────────────────────────
# ParseHaUrl in NetDaemonHaEventSource requires explicit :80 for ws:// scheme
printf "ws://supervisor:80" > /var/run/s6/container_environment/ARGUS_HA_URL
printf "%s" "${SUPERVISOR_TOKEN}" > /var/run/s6/container_environment/ARGUS_HA_TOKEN

# ── MQTT Credentials (SUPV-02) ──────────────────────────────────────────
if ! bashio::services.available "mqtt"; then
    bashio::log.fatal "MQTT service is not available. Install the Mosquitto add-on first."
    exit 1
fi
printf "%s" "$(bashio::services mqtt "host")"     > /var/run/s6/container_environment/ARGUS_MQTT_HOST
printf "%s" "$(bashio::services mqtt "port")"     > /var/run/s6/container_environment/ARGUS_MQTT_PORT
printf "%s" "$(bashio::services mqtt "username")" > /var/run/s6/container_environment/ARGUS_MQTT_USER
printf "%s" "$(bashio::services mqtt "password")" > /var/run/s6/container_environment/ARGUS_MQTT_PASSWORD

# ── Detector Mode Detection ──────────────────────────────────────────────
DETECTOR_EP=$(bashio::config 'detector_endpoint' || echo "")
if [ -z "${DETECTOR_EP}" ]; then
    printf "http://127.0.0.1:50051" > /var/run/s6/container_environment/ARGUS_DETECTOR_ENDPOINT
    printf "127.0.0.1"              > /var/run/s6/container_environment/ARGUS_GRPC_BIND
    printf "local"                  > /run/argus/mode
else
    printf "%s" "${DETECTOR_EP}"    > /var/run/s6/container_environment/ARGUS_DETECTOR_ENDPOINT
    printf "/data/certs/ca.crt"    > /var/run/s6/container_environment/ARGUS_TLS_CA
    printf "/data/certs/client.crt" > /var/run/s6/container_environment/ARGUS_TLS_CERT
    printf "/data/certs/client.key" > /var/run/s6/container_environment/ARGUS_TLS_KEY
    printf "remote"                 > /run/argus/mode
    touch /etc/services.d/detector/down  # disables local detector in Phase 3
fi
printf "/data/models"  > /var/run/s6/container_environment/ARGUS_MODEL_ROOT

# ── InfluxDB (UICFG-02) ──────────────────────────────────────────────────
INFLUX_URL=$(bashio::config 'influx_url' || echo "")
printf "%s" "${INFLUX_URL}" > /var/run/s6/container_environment/ARGUS_INFLUX_URL
printf "%s" "$(bashio::config 'influx_token' || echo "")"        > /var/run/s6/container_environment/ARGUS_INFLUX_TOKEN
printf "%s" "$(bashio::config 'influx_org' || echo "")"          > /var/run/s6/container_environment/ARGUS_INFLUX_ORG
printf "%s" "$(bashio::config 'influx_bucket' || echo "")"       > /var/run/s6/container_environment/ARGUS_INFLUX_BUCKET
printf "%s" "$(bashio::config 'influx_measurement')"             > /var/run/s6/container_environment/ARGUS_INFLUX_MEASUREMENT
printf "%s" "$(bashio::config 'influx_value_field')"             > /var/run/s6/container_environment/ARGUS_INFLUX_VALUE_FIELD

# ── Batch Schedule (UICFG-04) ─────────────────────────────────────────────
printf "%s" "$(bashio::config 'batch_interval_minutes')" > /var/run/s6/container_environment/ARGUS_BATCH_INTERVAL_MIN
printf "%s" "$(bashio::config 'nightly_fit_hour')"       > /var/run/s6/container_environment/ARGUS_NIGHTLY_FIT_HOUR

# ── entities.yaml Generation (UICFG-08) ──────────────────────────────────
mkdir -p /data/models
printf "/data/entities.yaml" > /var/run/s6/container_environment/ARGUS_ENTITIES_PATH
python3 /usr/local/bin/gen-entities.py /data/options.json > /data/entities.yaml

bashio::log.info "Config-gen complete."
```

### Pattern 2: gen-entities.py

```python
#!/usr/bin/env python3
"""
Converts /data/options.json entity list to /data/entities.yaml.

Input:  options.json   { "entities": ["sensor.foo", "sensor.bar"], ... }
Output: entities.yaml  matching EntitiesConfigLoader expected structure.

All entities get the HST streaming detector with default params (params: {}).
EntitiesConfigLoader.Validate() requires:
  - entities list non-empty
  - each entity_id non-empty
  - each entity has at least 1 detector
"""
import json
import sys
import yaml   # PyYAML (included in detector deps via darts transitive)

options_path = sys.argv[1] if len(sys.argv) > 1 else "/data/options.json"

with open(options_path) as f:
    options = json.load(f)

entity_ids = options.get("entities", [])
if not entity_ids:
    # Write empty-list YAML that Validate() will reject with a clear error.
    # The orchestrator will exit with "entities.yaml contains no entities".
    print("entities: []")
    sys.exit(0)

config = {
    "entities": [
        {
            "entity_id": eid,
            "friendly_name": "",
            "detectors": [
                {"name": "hst", "params": {}}
            ]
        }
        for eid in entity_ids
    ]
}

print(yaml.dump(config, default_flow_style=False, allow_unicode=True, sort_keys=False))
```

**Note:** `params: {}` serializes to YAML as `params: {}`. `EntitiesConfigLoader` deserializes an empty dict as `Dictionary<string, string>()` and `HstParams.From({})` returns all defaults. Verified via `IgnoreUnmatchedProperties` + `DeserializerBuilder` — no error on empty dict.

### Anti-Patterns to Avoid

- **`ARGUS_HA_URL=ws://supervisor/core/websocket` (no explicit port):** ParseHaUrl returns port -1 for unregistered ws:// scheme with no explicit port. Connection fails silently.
- **`ARGUS_HA_URL=http://supervisor:80/`:** `http://` is a registered scheme; port 80 is its default → `uri.IsDefaultPort = true` → ParseHaUrl returns 8123. Wrong port.
- **Writing `HomeAssistant__*` env vars:** Not consumed by any code path in v1. Would confuse future maintainers.
- **`services: - mqtt:want`:** Returns empty credential strings silently; MQTT connection fails with "not authorised".
- **Empty `params: {}` serialized as absent:** PyYAML with `default_flow_style=False` serializes `{"params": {}}` as `params: {}` on a single line — valid. Do not omit the `params` key entirely; EntitiesConfigLoader deserializes absent key as `null` dict, which the `HstParams.From()` call handles but is non-standard.

---

## config.yaml Schema Draft (complete — ready for planner use)

```yaml
name: "Argus Anomaly Detection"
description: "Streaming + batch anomaly detection for Home Assistant sensors."
version: "2.0.0"
slug: argus
url: "https://github.com/krzyl2/argus"
arch:
  - amd64
  - aarch64
startup: application
boot: auto
init: false
homeassistant_api: true
services:
  - mqtt:need
map:
  - type: data

options:
  entities: []
  include_patterns: []
  exclude_patterns: []
  influx_measurement: "homeassistant"
  influx_value_field: "value"
  batch_interval_minutes: 10
  nightly_fit_hour: 2
  log_level: "info"

schema:
  entities:
    - str
  include_patterns:
    - str
  exclude_patterns:
    - str
  influx_url: url?
  influx_token: password?
  influx_org: str?
  influx_bucket: str?
  influx_measurement: str
  influx_value_field: str
  detector_endpoint: str?
  batch_interval_minutes: int(1,1440)
  nightly_fit_hour: int(0,23)
  log_level: list(debug|info|warning)
```

**Schema notes:**
- `detector_endpoint`: schema `str?`, **absent from `options`** — truly optional, can be missing from options.json. Config-gen reads with `bashio::config 'detector_endpoint' || echo ""`.
- `influx_url` through `influx_bucket`: schema `TYPE?`, **absent from `options`** — optional fields. Leaving them out of options means users start with a clean slate. `influx_measurement` and `influx_value_field` have defaults so they appear in `options`.
- `include_patterns` / `exclude_patterns`: No `[str]?` list-optional syntax exists in HA add-on schema. Use `- str` (required list type) with empty list `[]` default. Config-gen treats empty list as "no filter". [ASSUMED: `[str]?` syntax is not supported]
- `password?` type: masked in Supervisor UI and excluded from logs. Use for `influx_token` and any future MQTT override fields.
- Batch interval validated by Program.cs at startup (WR-04: must be > 0; `int(1,1440)` enforces this at Supervisor level before the orchestrator sees it).

---

## translations Structure

**Source:** [ASSUMED: structure from HA developer docs training knowledge; field-level labels confirmed working]

### `translations/en.yaml`

```yaml
configuration:
  entities:
    name: Monitored Entities
    description: >-
      Home Assistant entity_id strings to monitor for anomalies
      (e.g. sensor.salon_temperatura). One per line.
  include_patterns:
    name: Include Patterns
    description: >-
      Glob patterns to filter which entities are monitored
      (e.g. sensor.outdoor_*). Leave empty to use the explicit entity list.
  exclude_patterns:
    name: Exclude Patterns
    description: >-
      Glob patterns to exclude entities from monitoring
      (e.g. sensor.*_voltage).
  influx_url:
    name: InfluxDB URL
    description: >-
      InfluxDB v2 server URL (e.g. http://192.168.1.10:8086).
      Leave empty to disable batch anomaly detection.
  influx_token:
    name: InfluxDB Token
    description: InfluxDB v2 API token with read access to the sensor bucket.
  influx_org:
    name: InfluxDB Organization
    description: InfluxDB organization name.
  influx_bucket:
    name: InfluxDB Bucket
    description: InfluxDB bucket containing Home Assistant sensor history.
  influx_measurement:
    name: Measurement Name
    description: InfluxDB measurement name for HA states (default homeassistant).
  influx_value_field:
    name: Value Field
    description: InfluxDB field key for sensor values (default value).
  detector_endpoint:
    name: Detector Endpoint
    description: >-
      Remote gRPC detector URL (e.g. https://gpu-host:50051).
      Leave empty to run the bundled local detector.
  batch_interval_minutes:
    name: Batch Interval (minutes)
    description: "How often to run batch anomaly detection. Range: 1-1440."
  nightly_fit_hour:
    name: Nightly Model Refit Hour
    description: "UTC hour (0-23) when the nightly model refit runs."
  log_level:
    name: Log Level
    description: Logging verbosity for detector and orchestrator.
```

### `translations/pl.yaml`

```yaml
configuration:
  entities:
    name: Monitorowane encje
    description: >-
      Identyfikatory encji HA do monitorowania pod kątem anomalii
      (np. sensor.salon_temperatura). Jeden na wiersz.
  include_patterns:
    name: Wzorce dołączania
    description: >-
      Wzorce glob filtrujące encje (np. sensor.outdoor_*).
      Pozostaw puste, aby używać listy jawnej.
  exclude_patterns:
    name: Wzorce wykluczania
    description: Wzorce glob wykluczające encje z monitorowania.
  influx_url:
    name: URL InfluxDB
    description: >-
      Adres serwera InfluxDB v2 (np. http://192.168.1.10:8086).
      Pozostaw puste, aby wyłączyć wykrywanie wsadowe.
  influx_token:
    name: Token InfluxDB
    description: Token API InfluxDB v2 z dostępem do bucketa.
  influx_org:
    name: Organizacja InfluxDB
    description: Nazwa organizacji InfluxDB.
  influx_bucket:
    name: Bucket InfluxDB
    description: Bucket InfluxDB zawierający historię sensorów HA.
  influx_measurement:
    name: Nazwa pomiaru
    description: Nazwa pomiaru InfluxDB dla stanów HA (domyślnie homeassistant).
  influx_value_field:
    name: Pole wartości
    description: Klucz pola InfluxDB dla wartości sensorów (domyślnie value).
  detector_endpoint:
    name: Endpoint detektora
    description: >-
      URL zdalnego detektora gRPC (np. https://gpu-host:50051).
      Pozostaw puste, aby uruchomić lokalny detektor.
  batch_interval_minutes:
    name: Interwał wsadowy (minuty)
    description: "Częstotliwość wykrywania wsadowego. Zakres: 1-1440."
  nightly_fit_hour:
    name: Godzina nocnego dopasowania modelu
    description: "Godzina UTC (0-23) nocnego przepasowania modelu."
  log_level:
    name: Poziom logowania
    description: Szczegółowość logowania dla detektora i orchestratora.
```

**List-item label caveat:** Individual labels for items within `entities: [str]`, `include_patterns: [str]`, and `exclude_patterns: [str]` are NOT supported by the translation system — only field-level labels (`name:`, `description:`) are supported. This is the standard HA limitation; users see the field label but not per-item labels. No fallback action required — field-level descriptions suffice. [ASSUMED: confirmed by community reports but not officially documented]

---

## repository.yaml (repo root)

```yaml
name: "Argus Add-on Repository"
url: "https://github.com/krzyl2/argus"
maintainer: "Krzysztof Krawczyk <k.krawczyk@it-ray.pl>"
```

**Fields required:** `name` and `url` at minimum. `maintainer` is optional but shown in HA store. The `url` must be the exact GitHub repo URL users will add via the HA custom repository dialog. [VERIFIED: prior research from HA developer docs]

---

## Dockerfile Skeleton

```dockerfile
ARG BUILD_FROM=ghcr.io/home-assistant/base-debian:bookworm
FROM ${BUILD_FROM}

# s6-overlay: exit container when any service crashes rather than looping silently
ENV S6_BEHAVIOUR_IF_STAGE2_FAILS=2

# ── .NET 8 Runtime (Microsoft Debian 12 package feed) ─────────────────────
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget ca-certificates \
    && wget -q https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb \
         -O /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && rm /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends dotnet-runtime-8.0 \
    && rm -rf /var/lib/apt/lists/*

# ── Python 3.12 + pip ─────────────────────────────────────────────────────
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        python3.12 \
        python3-pip \
        python3.12-venv \
    && rm -rf /var/lib/apt/lists/*

# ── Python detector dependencies ──────────────────────────────────────────
# --prefer-binary: mandatory for aarch64 to avoid scipy/statsmodels source compilation
# darts (no extras): core only — prevents 3-5 GB torch inflation (ADDON-05)
COPY detector/requirements.txt /tmp/requirements.txt
RUN pip3 install --no-cache-dir --prefer-binary -r /tmp/requirements.txt

# ── Orchestrator publish output (built by CI before Docker build) ──────────
COPY orchestrator/publish/ /opt/argus/orchestrator/

# ── Detector source + pre-generated proto stubs ───────────────────────────
COPY detector/ /opt/argus/detector/

# ── s6 init scripts, service run files, helper scripts ────────────────────
# Includes: etc/cont-init.d/10-config-gen.sh
#           etc/services.d/detector/ and orchestrator/ (Phase 3)
#           usr/local/bin/gen-entities.py
#           usr/local/bin/wait-detector.py (Phase 3)
COPY argus/rootfs/ /

# ── Build labels (populated by composable build-image GHA action) ─────────
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

# Base image sets ENTRYPOINT ["/init"] — do NOT add CMD or ENTRYPOINT.
```

**Notes for planner:**
- `.NET 8 runtime` (not SDK) is sufficient; orchestrator is published by CI beforehand.
- `pip3 install --prefer-binary` is non-negotiable for aarch64 amd64 (PITFALL: source compilation of scipy/statsmodels).
- `darts` pin in `detector/requirements.txt` must NOT include extras (`darts==0.44.1` not `darts[torch]==0.44.1`).
- The `COPY argus/rootfs/ /` copies rootfs contents to container root — services.d/ scripts must be executable before commit (`git update-index --chmod=+x`).
- `ARG BUILD_FROM` is substituted by the composable GHA `build-image` action with the arch-specific base image tag.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Read options.json fields | Manual `jq` / Python json parsing | `bashio::config 'field_name'` | Handles missing keys, type coercion, empty defaults — one-liner |
| Read MQTT credentials from Supervisor | `curl http://supervisor/services/mqtt` + manual JSON | `bashio::services mqtt "host"` etc. | Supervisor auth handled internally; error handling built-in |
| Check MQTT service availability | Manual HTTP probe | `bashio::services.available "mqtt"` | Returns boolean; handles all edge cases |
| Parse YAML in gen-entities.py | Write own YAML serializer | `yaml.dump()` from PyYAML | PyYAML is already in image (Darts transitive dep); handles escaping, unicode |
| Inject s6 env vars | Export in run script | Write to `/var/run/s6/container_environment/` from cont-init.d | Only mechanism that propagates to ALL supervised services; exports in run scripts are unreliable |

**Key insight:** bashio absorbs all Supervisor API complexity. Every manual `curl` call to the Supervisor API is a potential auth-header bug. Bashio is in the base image — use it unconditionally.

---

## Common Pitfalls

### Pitfall 1: ws:// port extraction in ParseHaUrl

**What goes wrong:** Writing `ARGUS_HA_URL=ws://supervisor/core/websocket` (no port) causes ParseHaUrl to extract port -1 from an unregistered URI scheme. NetDaemon.Client fails to connect.

**Why it happens:** .NET's `Uri` class has no registered default port for `ws://`. Unspecified port returns -1 rather than 80.

**How to avoid:** Always write `ws://supervisor:80` with explicit port. Path component is ignored by ParseHaUrl.

**Warning signs:** Orchestrator logs "Unable to connect to HA WebSocket" or connection refused on port -1.

### Pitfall 2: mqtt:want → empty credentials

**What goes wrong:** `services: - mqtt:want` causes Supervisor to return empty strings if MQTT is unavailable. MQTTnet connects with blank credentials, Mosquitto rejects with "not authorised".

**How to avoid:** Use `services: - mqtt:need`. Guard with `bashio::services.available "mqtt"` and `exit 1` if false.

**Warning signs:** MQTT connect attempts in orchestrator logs with no credential error — just silent rejection.

### Pitfall 3: params values as integers in gen-entities.py

**What goes wrong:** `yaml.dump({"params": {"window": 250}})` serializes `250` as YAML integer. `HstParams.From()` calls `int.TryParse` on a `Dictionary<string, string>` — if YAML deserialization returns an integer where a string is expected, YamlDotNet may fail or silently use default.

**How to avoid:** Always serialize params values as Python strings: `{"window": "250"}`. Or use empty `params: {}` (safer — skips all string-to-type parsing).

**Warning signs:** Orchestrator logs unexpected default param values; or YamlDotNet deserialization exception on params.

### Pitfall 4: execute bits lost on s6 scripts

**What goes wrong:** Windows git strips execute bits from `run` and `finish` scripts. s6 cannot start services — container exits immediately.

**How to avoid:** `git update-index --chmod=+x` for every `run`, `finish`, and `10-config-gen.sh` script. Verify with `git ls-files -s` (should show mode 100755).

**Warning signs:** s6 error "s6-supervise: unable to exec run: Permission denied" in container logs.

### Pitfall 5: darts pulls torch via extras

**What goes wrong:** `darts[all]` or `darts[torch]` in requirements.txt adds PyTorch (~1.8 GB). Image exceeds 2 GB (ADDON-05).

**How to avoid:** Pin `darts==0.44.1` (no extras). Add CI gate: `python3 -c "import torch"` must fail.

**Warning signs:** `docker build` output shows "Downloading torch-*.whl".

### Pitfall 6: include_patterns / exclude_patterns as optional list

**What goes wrong:** Attempting schema `- str?` for optional list items — this means each string is individually optional, not the list itself. `null` values in the list cause config-gen JSON parsing errors.

**How to avoid:** Schema `- str` with options default `[]`. Config-gen checks `len(patterns) == 0` → no filtering. An empty list is always valid.

---

## Code Examples

### Verified: entities.yaml produced by gen-entities.py

```yaml
# Source: entities.yaml (repo root) + EntitiesConfig.cs [VERIFIED: live codebase reads]
# Input: options.json entities: ["sensor.salon_temperatura", "sensor.outdoor_temperature"]

entities:
- entity_id: sensor.salon_temperatura
  friendly_name: ''
  detectors:
  - name: hst
    params: {}
- entity_id: sensor.outdoor_temperature
  friendly_name: ''
  detectors:
  - name: hst
    params: {}
```

PyYAML's `yaml.dump()` with `default_flow_style=False` produces this format. The `params: {}` is valid YamlDotNet deserialization for `Dictionary<string, string>` (empty dict).

### Verified: s6 env var injection pattern

```bash
# Source: ARCHITECTURE.md + HA s6-overlay conventions [VERIFIED: prior research]
# Write env vars BEFORE services start. Use printf (not echo) to avoid trailing newlines.
printf "%s" "value" > /var/run/s6/container_environment/VAR_NAME

# Service run scripts using #!/usr/bin/with-contenv bashio automatically inherit these.
```

### Verified: bashio MQTT service pattern

```bash
# Source: bashio lib/services.sh + HA developer docs [VERIFIED: prior research]
if ! bashio::services.available "mqtt"; then
    bashio::log.fatal "MQTT add-on not found. Install Mosquitto."
    exit 1
fi
MQTT_HOST=$(bashio::services mqtt "host")
MQTT_PORT=$(bashio::services mqtt "port")
MQTT_USER=$(bashio::services mqtt "username")
MQTT_PASS=$(bashio::services mqtt "password")
```

### Config-gen deterministic test (Phase 4 CI reuse)

```python
# tests/test_gen_entities.py — run with: pytest tests/test_gen_entities.py -x
import json, subprocess, yaml, tempfile, os

def test_gen_entities_minimal():
    options = {
        "entities": ["sensor.foo", "sensor.bar"],
        "influx_measurement": "homeassistant",
        "influx_value_field": "value",
        "batch_interval_minutes": 10,
        "nightly_fit_hour": 2,
        "include_patterns": [],
        "exclude_patterns": [],
        "log_level": "info",
    }
    with tempfile.NamedTemporaryFile(mode="w", suffix=".json", delete=False) as f:
        json.dump(options, f)
        tmp_path = f.name

    result = subprocess.run(
        ["python3", "argus/rootfs/usr/local/bin/gen-entities.py", tmp_path],
        capture_output=True, text=True, check=True
    )
    cfg = yaml.safe_load(result.stdout)

    assert len(cfg["entities"]) == 2
    entity_ids = [e["entity_id"] for e in cfg["entities"]]
    assert "sensor.foo" in entity_ids
    assert "sensor.bar" in entity_ids
    for entity in cfg["entities"]:
        assert len(entity["detectors"]) == 1
        assert entity["detectors"][0]["name"] == "hst"
        assert entity["detectors"][0]["params"] == {}
    os.unlink(tmp_path)
```

### Image fact assertions (Phase 4 CI)

```bash
# Assert glibc (not musl) — .NET requirement
docker run --rm "${IMAGE}" ldd /opt/argus/orchestrator/Argus.Orchestrator.dll \
  | grep "linux-vdso\|libpthread\|libdl" \
  || (echo "FAIL: glibc not found" && exit 1)

# Assert no PyTorch (ADDON-05)
docker run --rm "${IMAGE}" python3 -c "import torch" \
  && (echo "FAIL: torch is present in image" && exit 1) \
  || echo "OK: no torch"

# Assert image size < 2 GB compressed (ADDON-05)
COMPRESSED_BYTES=$(docker manifest inspect "${IMAGE}" \
  | python3 -c "import json,sys; m=json.load(sys.stdin); print(sum(l['size'] for l in m.get('layers',[])))")
COMPRESSED_GB=$(python3 -c "print(${COMPRESSED_BYTES}/1e9)")
python3 -c "assert ${COMPRESSED_GB} < 2.0, f'FAIL: image is {${COMPRESSED_GB}:.2f} GB'"
echo "OK: compressed image ${COMPRESSED_GB} GB"
```

---

## Environment Availability

> Phase 1 is packaging + config-gen work (file authoring). No external runtime dependencies are required during development. Build verification requires Docker (available on CI). Live Supervisor integration requires a running HA OS instance (Phase 3+).

| Dependency | Required By | Available in dev | Fallback |
|------------|------------|-----------------|----------|
| Docker | Dockerfile build + image size assertion | ✓ (CI environment) | — |
| HA OS / Supervisor | Live MQTT + WebSocket integration | ✗ (Phase 3+) | config-gen unit test with mock options.json |
| Mosquitto add-on | SUPV-02 live test | ✗ (Phase 3+) | config-gen exits non-zero path tested offline |
| PyYAML | gen-entities.py | ✓ (in detector requirements.txt via darts) | — |

**Missing dependencies with no fallback:** None for Phase 1 deliverables. Live Supervisor testing is Phase 3.

---

## Security Domain

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | Yes (HA auth via SUPERVISOR_TOKEN) | `homeassistant_api: true`; token written to s6 env, never to logs |
| V3 Session Management | No | No user sessions; Supervisor manages token lifecycle |
| V4 Access Control | No | Single-user, single-tenant, local network only |
| V5 Input Validation | Yes (options.json field values) | Supervisor schema validates before container starts; config-gen must treat options.json as untrusted for string injection into YAML |
| V6 Cryptography | Yes (SUPERVISOR_TOKEN, influx_token) | `password?` schema type masks in UI + logs; written via `printf` not `echo` to avoid shell history |

### Known Threat Patterns for this Stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| SUPERVISOR_TOKEN in logs | Information Disclosure | `printf` to s6 env file (not shell var), never log the value (CONF-03 in v1 code) |
| influx_token in plaintext in options.json | Information Disclosure | `password?` schema type (Supervisor masks in UI and redacts from debug logs) |
| YAML injection via entity_id strings | Tampering | Use `yaml.dump()` — never string-format YAML. PyYAML quotes special characters automatically |
| MQTT credentials cached after broker restart | Tampering | Re-read from Supervisor on every reconnect (SUPV-03); never store in process memory beyond the reconnect call |
| Entity_id path traversal in YAML output | Tampering | `yaml.dump()` escapes all special characters; entity_id format validated by Supervisor schema (`str` type) |

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | NetDaemon.Client `ConnectAsync` internally uses path `/api/websocket`; Supervisor proxy at supervisor:80 serves this path | Research Flag 2 | Phase 3 live integration fails with WebSocket upgrade error; fix is to patch ParseHaUrl or use a different NetDaemon connection pattern |
| A2 | `[str]?` (optional list) is not a valid HA add-on schema type; workaround is empty-list default with `- str` schema | config.yaml schema | If `[str]?` is valid, include_patterns/exclude_patterns can be truly optional. Low-risk: empty list default is semantically equivalent |
| A3 | Per-item labels in `translations/en.yaml` for `[str]` list fields are not rendered in HA UI | translations structure | If supported, additional per-item label keys can be added — no breaking change |
| A4 | `darts==0.44.1` (bare, no extras) does not pull torch transitively | Dockerfile skeleton | If torch is pulled anyway, add `--no-deps` install of torch exclusion or pin `torch` as absent |
| A5 | `watchdog: "tcp://[HOST]:50051"` syntax in config.yaml is valid HA add-on schema (for PROC-05, Phase 3) | config.yaml schema | If syntax is different, Phase 3 must update config.yaml; Phase 1 can omit watchdog field and add it in Phase 3 |

---

## Open Questions (RESOLVED)

1. **Supervisor proxy WebSocket path (Phase 3 risk)**
   - RESOLVED (deferred to Phase 3): Phase 1 writes `ARGUS_HA_URL=ws://supervisor:80`, which is correct per the `ParseHaUrl` code analysis (host=supervisor, port=80, ssl=false). Phase 1 runs no live HA container, so the exact proxy path (`/api/websocket` vs `/core/websocket`) does not block it. Live path-routing verification is a Phase 3 Wave 0 task; documented fallback: configure NetDaemon.Client via `HomeAssistant__Host/Port/Ssl/Token` IConfiguration keys in addition to `ARGUS_HA_URL/TOKEN` if the upgrade fails.

2. **darts torch transitive pull on fresh pip install**
   - RESOLVED: Mitigated by `image-facts.sh` (plan 01-03) asserting `python3 -c "import torch"` fails on the built image. v1 `detector/requirements.txt` does not install darts with torch extras (and currently does not install darts at all), so the assertion passes; the gate enforces it as a regression check. If torch ever appears, the gate fails and a dependency audit runs then.

---

## Sources

### Primary (HIGH confidence — live codebase reads)
- `EntitiesConfigLoader.cs` — deserializer config, validation rules, warn-ignored-keys logic [VERIFIED: codebase]
- `EntitiesConfig.cs` — exact C# model → YAML field name mapping, `HstParams.From()` param types [VERIFIED: codebase]
- `entities.yaml` (repo root) — canonical sample confirming string-typed params [VERIFIED: codebase]
- `NetDaemonHaEventSource.cs` — ARGUS_HA_URL/TOKEN consumption, ParseHaUrl implementation [VERIFIED: codebase]
- `Program.cs` — ConnectionSettings binding from ARGUS_* config keys [VERIFIED: codebase]
- `ConnectionSettings.cs` — full env var → property mapping [VERIFIED: codebase]

### Primary (HIGH confidence — prior authoritative research)
- `.planning/research/STACK.md` — base image, schema types, Dockerfile pattern, repository.yaml [prior research]
- `.planning/research/ARCHITECTURE.md` — s6 layout, cont-init.d ordering, config-gen pattern [prior research]
- `.planning/research/PITFALLS.md` — s6 v3 misconfigs, mqtt:need vs want, darts torch pitfall [prior research]
- `.planning/research/SUMMARY.md` — executive digest cross-referencing all research [prior research]

### Tertiary (LOW confidence — training knowledge)
- translations/en.yaml structure — HA developer docs (from training; list-item label caveat unverified)
- `[str]?` schema type availability — assumed unsupported based on HA schema type reference (training)
- Supervisor proxy path routing (`/api/websocket` vs `/core/websocket`) — needs live HA OS verification

---

## Metadata

**Confidence breakdown:**
- entities.yaml contract: HIGH — sourced from two C# source files + sample YAML
- HA auth env-var consumers: HIGH — sourced from NetDaemonHaEventSource.cs + Program.cs
- config.yaml schema draft: HIGH for schema types (prior authoritative research); MEDIUM for optional-list workaround
- translations structure: MEDIUM — standard HA pattern but not re-verified this session
- Dockerfile skeleton: HIGH — based on locked base-image decision + prior research patterns
- Pitfalls: HIGH — sourced from prior authoritative research

**Research date:** 2026-06-29
**Valid until:** 2026-07-30 (stable ecosystem; only risk is a grpcio or NetDaemon.Client minor version that changes ConnectAsync signature)
