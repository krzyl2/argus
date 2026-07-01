# Phase 2: Live Sensor Discovery + Entity Selection UI - Context

**Gathered:** 2026-07-01
**Status:** Ready for planning

<domain>
## Phase Boundary

The UI lists live HA numeric sensors (from a registry populated by the existing `get_states`
snapshot — no second WebSocket) and lets the user choose which entities Argus tracks.
`include_patterns`/`exclude_patterns` are honored as real selection filters (closing the v2.0
patterns-ignored gap). A `gen-entities.py` restart guard lands BEFORE the first UI save so an
add-on restart cannot erase UI-authored config.

Out of scope: detector assignment + editable params + live hot-reload (Phase 3); full
`validate_session` Ingress auth middleware + validation/CI/docs (Phase 4). Newly-selected
entities get the `hst` detector with default params (assignment UI is Phase 3). The running
pipeline reflects a new selection on the **next pipeline cycle** — live hot-reload is Phase 3.
</domain>

<decisions>
## Implementation Decisions

### Entity Picker Display & Interaction
- Flat list with client-side text search on `entity_id` (SC1). No domain/device_class grouping.
- Each row shows: `entity_id` + current value + unit of measurement (+ `friendly_name` when present).
- Tracked vs untracked distinction: checkbox reflects tracked state AND tracked rows carry a
  "tracked" pill/badge (SC2). Single list, not split sections.
- Selection is per-row checkboxes committed by an explicit **Save** button (atomic batch write via
  the Phase-1 `ConfigWriter`). No instant-save-per-toggle.

### Patterns as Selection Filters
- Pattern syntax: glob on `entity_id` (fnmatch-style, e.g. `sensor.*temp*`) — matches HA
  include/exclude conventions.
- Combine model (authoritative): resolved tracked set =
  `(entities matching include-globs − entities matching exclude-globs) ∪ manually-checked
  − manually-unchecked`. **Manual selection overrides patterns.**
- Persist BOTH the raw patterns AND the resolved concrete entity list, so re-opening the UI shows
  the patterns and the resulting selection (round-trips).
- Patterns are expanded server-side at save time into the concrete entity list written to
  `entities.yaml` (SC4). Client may preview, but the server is the source of truth.

### Save, Persistence & Restart Guard
- Save writes the resolved `entities.yaml` via the Phase-1 atomic `ConfigWriter`
  (temp + `File.Move(overwrite)` + `SemaphoreSlim`). Raw patterns are stored as metadata so they
  survive a round-trip (e.g. a top-level `_patterns:` block or sidecar — planner decides exact shape,
  must not break `EntitiesConfigLoader`).
- gen-entities.py restart guard (SC5): a `.ui_config_present` lock file in `/data`. `10-config-gen.sh`
  (cont-init.d) checks for it and SKIPS regeneration when present; the UI save creates it. This guard
  MUST land before the first save endpoint is wired.
- Newly-selected entities default to the `hst` detector with default params (detector assignment UI
  is Phase 3) — consistent with today's gen-entities.py behavior.
- Interim Ingress auth: accept connections from the Supervisor IP `172.30.32.2` in Phase 2; the full
  `validate_session` middleware is deferred to Phase 4 (per research flag). Probe the live Supervisor
  `validate_session` shape opportunistically but do not block Phase 2 on it.

### Claude's Discretion
- Exact htmx interaction wiring, endpoint routes, and the on-disk shape of the persisted patterns
  metadata (must remain backward-compatible with `EntitiesConfigLoader`).
</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` — already fetches the `get_states`
  snapshot (`client.GetStatesAsync`), logs unconfigured numeric sensors (UICFG-05), and has a
  static `SelectDiscoverableSensors` selector + numeric `double.TryParse` (invariant culture) filter.
  **There is NO persistent `IHaSensorRegistry` yet** — Phase 2 must add one that caches the latest
  numeric-sensor snapshot (entity_id, value, unit, friendly_name) on each connect, for the UI to read.
  Populate it from this class; do NOT open a second WebSocket (ADR-4 / Anti-Pattern 5).
- `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` (Phase 1) — atomic YAML writer; use for Save.
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs` — `EntitiesConfig.Entities: List<EntityConfig>`;
  `EntityConfig { EntityId, FriendlyName, Detectors[], Covariates?, Groups? }`. No pattern fields today.
- `orchestrator/Argus.Orchestrator/Program.cs` (Phase 1) — WebApplication host with Kestrel + Minimal API;
  add `GET /api/sensors`, the picker page route, and `POST` save endpoint here (same PathBase/Ingress pipeline).
- `orchestrator/Argus.Orchestrator/PlaceholderPage.cs` + `wwwroot/css/argus.css` + committed htmx 2.0.10 —
  the design foundation (tokens, base layout, htmx conventions) to build the picker UI on.
- `argus/rootfs/usr/local/bin/gen-entities.py` + `argus/rootfs/etc/cont-init.d/10-config-gen.sh` —
  the config-gen path that overwrites `/data/entities.yaml` from `options.json` on every boot; add the guard here.

### Established Patterns
- Minimal API endpoints + singletons in Program.cs; sealed single-responsibility services; `LogEvents.*`
  EventId constants; `CapturingLogger` test helper; invariant-culture numeric parsing.

### Integration Points
- Registry singleton written by `NetDaemonHaEventSource`, read by `GET /api/sensors`.
- Save endpoint → `ConfigWriter` → `/data/entities.yaml` + `.ui_config_present` lock.
- `10-config-gen.sh` gains the `.ui_config_present` skip guard.
</code_context>

<specifics>
## Specific Ideas

- Entity picker built with server-rendered HTML + htmx (Phase 1 stack), reusing the argus.css tokens.
- Text search is client-side over the rendered list; tracked pill uses an accent/status token.
</specifics>

<deferred>
## Deferred Ideas

- Detector assignment + editable params + live config hot-reload → Phase 3.
- Full `validate_session` Ingress auth middleware → Phase 4.
- Server-side/client-side input validation polish, CI packaging, DOCS.md → Phase 4.
</deferred>
