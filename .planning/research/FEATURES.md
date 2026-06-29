# Feature Research

**Domain:** Home Assistant add-on packaging + UI configuration + lifecycle
**Researched:** 2026-06-29
**Confidence:** LOW (all sources: web/webfetch; official HA dev docs fetched directly but provider tier is LOW)

---

## Scope

This file covers only the add-on shell: install flow, configuration UX, lifecycle management, entity selection, and the mapping from the existing `entities.yaml` / `ARGUS_*` env-var surface to the add-on options form. Detection algorithms are out of scope.

---

## How HA Add-ons Work — Background

### Install flow (user perspective)

1. Settings → Add-ons → Add-on Store → three-dot menu → Repositories → paste custom repo URL
2. Supervisor fetches `repository.yaml` at the repo root (name, maintainer, url)
3. Add-on subfolder appears in the store under "Custom repositories"
4. User selects add-on → Install (pulls Docker image)
5. Configuration tab: user fills out options form generated from `options` + `schema` in `config.yaml`
6. User presses Start → Supervisor creates container, injects `/data/options.json`, sets env vars
7. Logs tab: everything written to stdout/stderr appears here in near-real-time

### Runtime plumbing Argus gets for free as an add-on

| Capability | How | Replaces |
|------------|-----|---------|
| HA WebSocket auth | `homeassistant_api: true` → `SUPERVISOR_TOKEN` env var; use as Bearer at `ws://supervisor/core/websocket` | `ARGUS_HA_URL` + `ARGUS_HA_TOKEN` |
| HA REST API | `http://supervisor/core/api/*` with same token | same |
| MQTT credentials | `services: [mqtt:need]` → `bashio::services mqtt host/port/username/password` | `ARGUS_MQTT_HOST/PORT/USER/PASSWORD` |
| User options | `/data/options.json` written by Supervisor at startup | `entities.yaml` (generated) + `ARGUS_*` env vars |
| Persistent state | `/data/` volume survives restarts | existing model storage path |

### Options schema type system

Types available in `config.yaml` `schema:` block:

| Type | UI control | Notes |
|------|------------|-------|
| `str` | text input | `str(min,max)` for length constraint |
| `bool` | toggle | |
| `int` | number input | `int(min,max)` for range |
| `float` | number input | `float(min,max)` |
| `password` | masked input | never shown in plaintext in UI |
| `url` | text input with url validation | |
| `port` | number input 1–65535 | |
| `email` | text input | |
| `match(regex)` | text input + pattern validation | |
| `list(a\|b\|c)` | dropdown/select | |
| `[str]` | repeatable string list | user adds rows; closest to entity picker |
| `[{key: type}]` | repeatable object list | nested dict, max depth 2 |
| `type?` | any of above + `?` suffix | field is truly optional (no default required) |

There is **no native entity picker** widget. User types entity_id strings into a `[str]` array field. The UI renders this as a list with add/remove row controls.

---

## Feature Landscape

### Table Stakes (Users Expect These)

| Feature | Why Expected | Complexity | Dependency on Existing Config | Notes |
|---------|--------------|------------|-------------------------------|-------|
| Custom repo + install from store | All HA add-ons work this way; missing = not an add-on | LOW | None — new repo structure | `repository.yaml` + add-on subfolder; Docker image on GHCR |
| Configuration tab (options form) | Every add-on has one; missing = unusable | MEDIUM | Replaces all `ARGUS_*` env vars; `entities.yaml` generated from it | Schema must cover: entity list, InfluxDB, detector endpoint, batch schedule |
| Auto HA auth (no token field) | Users expect add-ons to integrate silently; asking for a token = poor UX | LOW | Eliminates `ARGUS_HA_URL` + `ARGUS_HA_TOKEN` | `homeassistant_api: true` + `SUPERVISOR_TOKEN` |
| Auto MQTT credentials (no MQTT fields) | Official Mosquitto add-on is the standard broker; credentials should wire up automatically | LOW | Eliminates all `ARGUS_MQTT_*` vars | `services: [mqtt:need]`; startup script reads via bashio |
| Logs tab shows add-on output | Expected by every HA user for troubleshooting | LOW | No change — stdout/stderr already used | Ensure both orchestrator + detector processes log to stdout |
| Start/Stop/Restart from UI | Standard add-on lifecycle; missing = must SSH | LOW | None | Managed by Supervisor automatically |
| Add-on survives HA restart | Standard expectation | LOW | None | `boot: auto` in config.yaml |
| startup script generates entities.yaml from options | Bridge between add-on options form and existing code | MEDIUM | Direct dependency: replaces manual `entities.yaml` editing | Bash or .NET startup reads `/data/options.json`, writes `entities.yaml` |
| icon.png + logo.png | Users expect polished add-on cards | LOW | None | 128x128 icon, 250x100 logo; PNG |
| DOCS.md in Documentation tab | Users read this before configuring; missing = confusion | LOW | None | Markdown shown verbatim in HA UI; cover all config options |
| Watchdog / auto-restart on crash | Users expect services to self-heal | LOW | None | `watchdog: tcp://[HOST]:PORT` on a gRPC or HTTP health endpoint; Mosquitto pattern |
| Multi-arch (amd64 + aarch64) | Raspberry Pi (aarch64) is the dominant HA platform; amd64-only = excludes majority of users | MEDIUM | None | GitHub Actions matrix build; `arch: [aarch64, amd64]` in config.yaml |

### Differentiators (Argus-Specific Value)

| Feature | Value Proposition | Complexity | Dependency on Existing Config | Notes |
|---------|-------------------|------------|-------------------------------|-------|
| HA API auto-discovery of numeric sensors (optional, at startup) | User does not have to know entity_id strings; add-on can propose a filtered list | MEDIUM | None — new feature using SUPERVISOR_TOKEN | Call `GET /api/states` at startup; filter `state_class: measurement` + numeric `unit_of_measurement`; write entity list to startup log so user can copy-paste into form. Does not auto-populate the form (schema limitation). |
| `include_patterns` + `exclude_patterns` glob fields | Power-user UX: monitor `sensor.*_temperature` without listing 20 entities | MEDIUM | Replaces / supplements `entities` list | `[str]` fields; startup script expands globs against `/api/states` to build `entities.yaml`. This is additive to the explicit list. |
| Local detector by default (loopback, no mTLS, no config) | Zero networking config for the common case | MEDIUM | Overrides D4 mTLS (intentional for v2); `ARGUS_DETECTOR_ENDPOINT` becomes optional | `detector_endpoint` field absent or empty → use loopback; presence triggers mTLS path |
| Optional external detector endpoint | Retains the GPU-host split for power users | LOW | `detector_endpoint: url?` + `tls_ca/cert/key: str?` optional fields | mTLS cert content as base64 `password?` fields (paths don't exist in add-on filesystem) |
| InfluxDB in options form (all fields) | Batch detection works without SSH / env file editing | LOW | Maps 1:1 to `ARGUS_INFLUX_*` vars | url, token (password), org, bucket, measurement, value_field, batch_interval_minutes, nightly_fit_hour |
| translations/en.yaml for option labels | Configuration tab shows human-readable labels instead of raw key names | LOW | None — additive | `configuration.entities.name`, `.description` etc.; straightforward to add |
| Startup log prints discovered entities | Bridges the no-entity-picker gap; user sees what Argus found and can refine list | LOW | Requires homeassistant_api call at startup | Log lines like `[ARGUS] Discovered numeric sensor: sensor.salon_temperatura` |

### Anti-Features (Deliberately NOT Building)

| Feature | Why Requested | Why Problematic | What To Do Instead |
|---------|---------------|-----------------|-------------------|
| Native entity picker in the config form | Users expect GUI entity selection | Not possible in add-on options schema — the type system has no entity selector widget | Use `[str]` list + startup auto-discovery log; document that user copies entity_ids from HA Developer Tools |
| Ingress / sidebar panel | Looks professional; ESPHome does it | Ingress is for add-ons with a web UI. Argus has no web frontend. Adding ingress for no reason creates a dead link in the sidebar and confuses users. | Do not set `ingress: true`. Use `panel_icon` only if a future UI panel is added. |
| Per-entity detector tuning in the options form | Full control over detector params per entity | `[{entity_id: str, detector: str, threshold: float}]` nested list is schema depth 2 and extremely tedious to configure through the HA list UI; breaks the "zero-config" value | Use defaults for all detector params in the add-on options form. Advanced users can override via the v1 `entities.yaml` mechanism (mount point or local add-on variant). |
| MQTT username/password fields in options | Users who don't use official Mosquitto need to enter creds | Adds 4 fields that 95% of users never fill in; creates confusion about whether to fill them | Use `services: [mqtt:need]`; if user needs external MQTT, that's a v3 concern. Document the Mosquitto dependency clearly in DOCS.md. |
| HA token field in options | Power users on Container/Core don't have Supervisor | Duplicates auto-auth; misleads users on OS/Supervised into thinking they need to generate a token | The docker-compose path (for Container/Core) remains the v1 deployment; the add-on is strictly for OS/Supervised. Document this clearly. |
| Arbitrary config file editor inside the add-on | Full power of entities.yaml without leaving HA | Complex to build safely; not what the options form is for | The add-on mount (`map: [addon_config:rw]`) can expose `/addon_configs/argus/` to File Editor; document this in DOCS.md for advanced users. |
| GitHub Action auto-publish to store on every commit | Continuous deployment | HA add-on store indexes by GitHub release tags, not commits; auto-publishing every commit produces broken intermediate versions | Use GitHub release tags; GHA workflow triggers on `release: published` only. |

---

## Options Schema → Existing Config Mapping

Concrete mapping of v2 add-on options to v1 `ConnectionSettings.cs` fields and `entities.yaml`:

### Eliminated (handled by Supervisor)

| v1 env var | Why eliminated |
|-----------|---------------|
| `ARGUS_HA_URL` | `http://supervisor/core/api` is the fixed endpoint |
| `ARGUS_HA_TOKEN` | `SUPERVISOR_TOKEN` is injected automatically |
| `ARGUS_MQTT_HOST` | `bashio::services mqtt "host"` |
| `ARGUS_MQTT_PORT` | `bashio::services mqtt "port"` |
| `ARGUS_MQTT_USER` | `bashio::services mqtt "username"` |
| `ARGUS_MQTT_PASSWORD` | `bashio::services mqtt "password"` |
| `ARGUS_ENTITIES_PATH` | Always `/data/entities.yaml` (generated at startup) |

### Mapped to options form fields

| Options field | Type | Default | Maps to | Notes |
|--------------|------|---------|---------|-------|
| `entities` | `[str]` | `[]` | `entities.yaml` entity list | One entity_id per row; HST detector with all defaults |
| `include_patterns` | `[str]?` | omit | Entity glob expansion at startup | e.g. `sensor.*_temperature` |
| `exclude_patterns` | `[str]?` | omit | Excluded from glob expansion | e.g. `sensor.outdoor_*` |
| `detector_endpoint` | `url?` | omit | `ARGUS_DETECTOR_ENDPOINT` | If absent → loopback gRPC, no mTLS |
| `tls_ca` | `password?` | omit | `ARGUS_TLS_CA` (written to temp file) | Base64-encoded cert content; written to `/tmp/ca.crt` at startup |
| `tls_cert` | `password?` | omit | `ARGUS_TLS_CERT` | Same pattern |
| `tls_key` | `password?` | omit | `ARGUS_TLS_KEY` | Same pattern |
| `influx_url` | `url?` | omit | `ARGUS_INFLUX_URL` | Batch detection only; if absent, batch disabled |
| `influx_token` | `password?` | omit | `ARGUS_INFLUX_TOKEN` | |
| `influx_org` | `str?` | omit | `ARGUS_INFLUX_ORG` | |
| `influx_bucket` | `str?` | omit | `ARGUS_INFLUX_BUCKET` | |
| `influx_measurement` | `str` | `homeassistant` | `ARGUS_INFLUX_MEASUREMENT` | |
| `influx_value_field` | `str` | `value` | `ARGUS_INFLUX_VALUE_FIELD` | |
| `batch_interval_minutes` | `int(1,1440)` | `10` | `ARGUS_BATCH_INTERVAL_MIN` | |
| `nightly_fit_hour` | `int(0,23)` | `2` | `ARGUS_NIGHTLY_FIT_HOUR` | |

### Generated at startup (not in options form)

- `entities.yaml` — written by startup script by expanding `entities` list + `include_patterns` minus `exclude_patterns` against HA `/api/states`; all entities get default HST detector config
- mTLS cert files — if `tls_*` options present, base64-decoded and written to `/tmp/`

---

## Entity Selection UX Options (Concrete)

The add-on schema has no entity picker. Three realistic approaches, ranked by implementation cost:

### Option A — Manual list only (minimum viable, recommended for v2)

```yaml
# config.yaml
options:
  entities: []
schema:
  entities:
    - str
```

User manually types `sensor.salon_temperatura`, `sensor.outdoor_temperature` etc. as rows in the UI list.

**Startup behavior:** startup script reads `entities` array from `/data/options.json`, writes `entities.yaml` with default HST params for each.

**Gap mitigation:** startup script calls `GET /api/states`, filters by numeric `state_class`, logs discovered entity_ids so user can copy-paste.

Complexity: LOW. No HA API call required beyond what already exists.

### Option B — Manual list + glob patterns (differentiator)

Add `include_patterns` and `exclude_patterns` as `[str]?` fields. Startup script expands globs.

```yaml
options:
  entities: []
  include_patterns: []
  exclude_patterns: []
schema:
  entities:
    - str
  include_patterns:
    - str?
  exclude_patterns:
    - str?
```

Final entity list = (explicit `entities`) ∪ (globs expanded from `include_patterns`) \ (globs from `exclude_patterns`).

Complexity: MEDIUM. Requires HA API call + fnmatch/glob expansion at startup.

### Option C — Auto-discovery only (future / v3)

No options form entry; startup calls `/api/states`, finds all `state_class: measurement` numeric sensors, monitors all of them. User uses `exclude_patterns` to opt out.

Not recommended for v2: users need explicit control over what's monitored to avoid model pollution from unrelated sensors.

**Recommendation: implement Option A for v2, expose discovered entity list in startup log. Option B can follow in a patch if users find the manual list tedious.**

---

## Feature Dependencies

```
[Custom repo + install flow]
    └──requires──> [config.yaml with options schema]
                       └──requires──> [startup script reads /data/options.json]
                                          └──requires──> [entities.yaml generation]

[Auto MQTT credentials]
    └──requires──> [services: mqtt:need in config.yaml]
    └──requires──> [Mosquitto add-on installed by user]

[Auto HA auth]
    └──requires──> [homeassistant_api: true in config.yaml]

[Startup log entity discovery]
    └──requires──> [homeassistant_api: true]
    └──enhances──> [manual entity list UX]

[Local detector default]
    └──requires──> [detector + orchestrator both run in same container via s6]
    └──conflicts──> [external detector endpoint] (mutually exclusive at runtime, not at config time)

[Watchdog]
    └──requires──> [health endpoint on a known port in the container]

[Multi-arch build]
    └──requires──> [Dockerfile with multi-arch base images]
    └──requires──> [GitHub Actions matrix build]
```

---

## MVP Definition for v2.0

### Must ship

- [ ] `repository.yaml` + add-on folder structure (slug, config.yaml, Dockerfile)
- [ ] options schema covering: `entities [str]`, InfluxDB fields, `detector_endpoint?`, `batch_interval_minutes`, `nightly_fit_hour`
- [ ] `homeassistant_api: true` + `services: [mqtt:need]` (auto auth + auto MQTT creds)
- [ ] Startup script: reads `/data/options.json`, writes `entities.yaml`, sets env vars, starts s6 services
- [ ] s6-overlay service supervision for orchestrator + detector processes
- [ ] Watchdog declaration (`tcp://` on gRPC port or `http://` on health endpoint)
- [ ] `boot: auto` + `startup: application`
- [ ] Multi-arch: amd64 + aarch64 builds via GitHub Actions
- [ ] DOCS.md covering install flow, prerequisites (Mosquitto), all config fields, troubleshooting
- [ ] icon.png + logo.png
- [ ] Startup log line listing discovered numeric sensors (entity selection gap mitigation)

### Add after v2.0 ships

- [ ] `translations/en.yaml` for option labels — improves polish, low effort
- [ ] `include_patterns` / `exclude_patterns` glob fields — reduces manual list burden
- [ ] Custom AppArmor profile — earns security point; requires profiling .NET + Python subprocess behavior
- [ ] Per-entity detector param overrides — only if users complain about false positive rate

### Defer to v3+

- [ ] Ingress / sidebar panel — only if a web UI (diagnostics dashboard) is added
- [ ] Auto-discovery-only mode (Option C) — requires UX for exclude lists first
- [ ] HACS submission — requires stable public release and review process

---

## Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---------|------------|---------------------|----------|
| Custom repo + install | HIGH | LOW | P1 |
| Options form (entity list + InfluxDB) | HIGH | MEDIUM | P1 |
| Auto HA auth (homeassistant_api) | HIGH | LOW | P1 |
| Auto MQTT creds (services) | HIGH | LOW | P1 |
| Startup generates entities.yaml | HIGH | MEDIUM | P1 |
| s6 two-process supervision | HIGH | MEDIUM | P1 |
| Multi-arch (amd64 + aarch64) | HIGH | MEDIUM | P1 |
| Watchdog | MEDIUM | LOW | P1 |
| DOCS.md | MEDIUM | LOW | P1 |
| icon.png + logo.png | LOW | LOW | P1 |
| Startup log: discovered entities | MEDIUM | LOW | P1 |
| translations/en.yaml | LOW | LOW | P2 |
| include_patterns / exclude_patterns | MEDIUM | MEDIUM | P2 |
| Custom AppArmor profile | LOW | MEDIUM | P2 |
| Per-entity detector tuning in form | LOW | HIGH | P3 |
| Ingress panel | LOW | HIGH | P3 |

---

## Sources

- [HA Add-on Configuration docs](https://developers.home-assistant.io/docs/add-ons/configuration/) — schema type system, options/schema fields, watchdog, services, maps
- [HA Add-on Presentation docs](https://developers.home-assistant.io/docs/add-ons/presentation/) — DOCS.md, icon/logo specs
- [HA Add-on Tutorial](https://developers.home-assistant.io/docs/add-ons/tutorial/) — install flow, run.sh conventions, /data/options.json
- [HA Add-on Communication docs](https://developers.home-assistant.io/docs/add-ons/communication/) — SUPERVISOR_TOKEN, homeassistant_api, services mqtt credential injection
- [Mosquitto add-on config.yaml](https://github.com/home-assistant/addons/blob/master/mosquitto/config.yaml) — watchdog tcp:// pattern, services: mqtt:provide, startup: system
- [Z-Wave JS UI add-on config.yaml](https://github.com/hassio-addons/addon-zwave-js-ui/blob/main/zwave-js-ui/config.yaml) — ingress, panel_icon, services: mqtt:want, mature add-on conventions
- [HA builder GitHub Action](https://github.com/marketplace/actions/home-assistant-builder) — multi-arch build workflow
- [hassio-addons/bashio](https://github.com/hassio-addons/bashio) — bashio::services mqtt, bashio::config helpers

---
*Feature research for: Home Assistant add-on packaging + UI configuration + lifecycle*
*Researched: 2026-06-29*
