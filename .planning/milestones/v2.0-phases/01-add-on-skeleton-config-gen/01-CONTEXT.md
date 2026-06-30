# Phase 1: Add-on Skeleton + Config-Gen - Context

**Gathered:** 2026-06-29
**Status:** Ready for planning

<domain>
## Phase Boundary

Deliver the Home Assistant add-on packaging skeleton and the config-gen integration seam. After this phase: the Argus repository is addable as a custom add-on repository, "Argus" appears installable in the HA store, its Configuration tab exposes all options with EN/PL labels, `ha addon validate` passes, the Dockerfile builds on `base-debian:bookworm` (amd64) under 2 GB with no PyTorch, and a config-gen step converts `/data/options.json` into `/data/entities.yaml` plus the s6 environment variables both processes consume — before any service starts.

Covers requirements: ADDON-01, ADDON-03, ADDON-05, SUPV-01, SUPV-02, UICFG-01, UICFG-02, UICFG-03, UICFG-04, UICFG-06, UICFG-07, UICFG-08. Out of this phase: s6 service wiring/runtime (Phase 3), orchestrator/detector code changes (Phase 2), multi-arch CI + live integration (Phase 4).

</domain>

<decisions>
## Implementation Decisions

### Add-on Identity & Repository Layout
- Store display name: **"Argus Anomaly Detection"**.
- Add-on lives in a `argus/` subfolder of the existing `krzyl2/argus` repository; `repository.yaml` at repo root (single-add-on custom repository).
- Ship `icon.png` + `logo.png` now (simple flat mark; visual polish deferred, not blocking).
- Expose a `log_level` option (`list(debug|info|warning)`, default `info`) wired to both processes' log level.

### Options Schema (Configuration tab)
- Entity selection: `entities: [str]` list of entity_id, plus optional `include_patterns: [str]?` / `exclude_patterns: [str]?` globs.
- InfluxDB: flat optional fields `influx_url?`, `influx_token?` (password), `influx_org?`, `influx_bucket?`, `influx_measurement?`, `influx_value_field?`. Empty `influx_url` disables the batch path cleanly.
- Detector: `detector_endpoint: str?` — empty runs the bundled local detector; a URL targets a remote detector.
- Batch schedule: `batch_interval_minutes: int` (default 10), `nightly_fit_hour: int` (default 2).
- Field labels/descriptions localized via `translations/` (English + Polish, D8).

### Config-Gen & Entity→Detector Mapping
- No per-entity detector tuning in the UI for v2.0: every configured entity gets the default HST streaming detector (and batch detectors where InfluxDB is set). Per-entity parameter tuning is deferred to v2.1.
- A `cont-init.d` oneshot generates `/data/entities.yaml` from `/data/options.json` (via a `gen-entities.py` helper) before any service starts; it must match `EntitiesConfigLoader`'s expected YAML structure exactly.
- HA auth wired from Supervisor: `SUPERVISOR_TOKEN` + `ws://supervisor/core/websocket`. The exact env-var set (`HomeAssistant__*` vs `ARGUS_HA_*`, and the proxy hostname) is resolved at plan time by reading `EntitiesConfigLoader` and the callers of `ConnectionSettings.HaUrl`/`HaToken` (research flag).
- MQTT wired via bashio `services: [mqtt:need]`: fail loud (non-zero exit) if no MQTT service is available; credentials re-read on every reconnect, never cached.

### Claude's Discretion
- Base image is `ghcr.io/home-assistant/base-debian:bookworm`; .NET 8 install method, exact s6 directory layout, schema field ordering, and icon artwork are at Claude's discretion within the research constraints (Debian not Alpine; `init: false`; `S6_BEHAVIOUR_IF_STAGE2_FAILS=2`; `darts` core only, no torch; `pip --prefer-binary`).

</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- `deploy/Dockerfile.orchestrator`, `deploy/Dockerfile.detector` — existing build recipes to adapt into a single add-on Dockerfile.
- `orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs` + `EntitiesConfigLoader.cs` — the env-var and entities.yaml contract config-gen must satisfy.
- `entities.yaml` (repo root) — the structure `gen-entities.py` must reproduce.
- `deploy/docker-compose.edge.yml` / `.gpu.yml` — document the ARGUS_* env surface to map from options.

### Established Patterns
- Config is bound from `ARGUS_*` env vars (CONF-03: no literal secret defaults; validated at startup).
- `ARGUS_ENTITIES_PATH` selects the entities.yaml path the orchestrator loads.

### Integration Points
- Config-gen is the seam between Supervisor (`/data/options.json`, `SUPERVISOR_TOKEN`, MQTT service API) and both existing processes via env vars + generated entities.yaml.

</code_context>

<specifics>
## Specific Ideas

- v1 research and decisions are archived under `.planning/archive/v1.0/`; v2 research under `.planning/research/` (SUMMARY.md is the roadmap-facing digest).
- Mosquitto is documented as a hard prerequisite; Zigbee2MQTT embedded broker is out of scope (DOCS warning only).

</specifics>

<deferred>
## Deferred Ideas

- Per-entity detector parameter tuning in the UI → v2.1.
- Auto-discovery-only mode (monitor all numeric sensors with exclude list) → v2.1+.
- `translations/en.yaml` list-item label support verification (community-flagged) → handle during implementation; fall back to field-level labels if unsupported.

</deferred>
