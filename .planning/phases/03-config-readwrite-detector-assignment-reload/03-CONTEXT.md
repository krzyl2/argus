# Phase 3: Config Read/Write + Detector Assignment + Reload - Context

**Gathered:** 2026-07-01
**Status:** Ready for planning

<domain>
## Phase Boundary

The user reads the current tracked-entity config in the UI, assigns one or more detectors
(HST/MAD/STL) with editable parameters per entity, saves, and the running pipeline reloads within
seconds — no add-on restart. Removed entities have their MQTT discovery topics retracted. This is the
highest-complexity phase: `ILiveEntitiesConfig` is the most invasive cross-cutting change.

Covers CFG-04 (reload-without-restart) — the reload MECHANISM is Phase 3 (Phase 4 only adds the
validation layer + CI gate on top).

Out of scope: full server+client input validation (Phase 4), CI multi-arch packaging + DOCS.md (Phase 4),
full `validate_session` Ingress auth (Phase 4). All live-HA/browser verification is deferred to the
end-of-milestone UAT sweep (consistent with Phases 1–2).
</domain>

<decisions>
## Implementation Decisions

### Detector Assignment UI
- Detector management lives in an **expandable section on each tracked entity row** in the Phase-2
  picker (expand a row to manage its detectors) — not a separate page or modal.
- Each detector is a per-detector **dropdown (HST / MAD / STL)** with an "Add detector" control so
  multiple detectors per entity are supported (SC3; the model already supports it).
- The UI **labels the timing honestly**: HST = "streaming (live, ~2 s reload)"; MAD/STL = "batch
  (runs every N min)". Rationale: only HST runs in the streaming pipeline; MAD/STL run in the batch
  path (`BatchSchedulerWorker`), so their reassignment takes effect on the next batch cycle, not in 2 s.
- When an entity has no explicit assignment, default to `hst` with sane defaults, pre-filled.

### Detector Parameters
- Param editing uses **per-type known fields with defaults**, persisted into the existing generic
  `DetectorConfig.Params` (`map<string,string>`) — NO model change:
  - HST: `window`, `n_trees`, `high_threshold`, `low_threshold`, `min_consecutive`, `frozen_window`,
    `frozen_variance_threshold` (defaults from `HstParams`).
  - MAD: `threshold`, `window` (sane defaults).
  - STL: `period`, `seasonal`, `threshold` (sane defaults).
- Validation is **client hints only in Phase 3**; full server+client validation is Phase 4 (per roadmap).
- Persistence shape is unchanged: `DetectorConfig { Name, Params }`; an empty `Params` map means "use
  defaults". The Python detector interprets params per type (proto `params` is `map<string,string>`).
- Pre-fill current values from `/data/entities.yaml`; fall back to type defaults where a key is absent (SC1).

### Reload, Retraction & Save UX
- Reload mechanism (roadmap-locked, confirmed): a new `ILiveEntitiesConfig` singleton holding a
  `volatile` reference swapped via `Interlocked.Exchange`, firing a `ConfigChanged` event AFTER the swap.
  `HaListenerWorker` subscribes; on change it cancels an **inner** `CancellationTokenSource` (NOT the
  host `stoppingToken`) and restarts the `ScoreStreamPipeline.RunAsync` loop. MQTT + gRPC transport stay
  alive. `ScoreStreamPipeline.BuildEntityStates()` (called at RunAsync entry) reads the swapped config, so
  the new config is live on restart. Streaming gap target < 1 s; SC2 "within 2 s" applies to HST/streaming.
- `BatchSchedulerWorker` currently captures `EntitiesConfig` at construction (`_entities` field) — it MUST
  change to read `ILiveEntitiesConfig.Get()` per batch cycle (and per nightly-fit cycle) so MAD/STL
  reassignments take effect without restart.
- Removed-entity MQTT retraction (SC4): on reload, diff old vs new entity sets and publish empty retained
  payloads to the discovery topics (binary_sensor + sensor) of removed entities BEFORE restarting the loop,
  so stale HA entities disappear within ≤30 s.
- Save feedback: htmx banner "Saved — pipeline reloading…" → success; no full page reload.
- The config UI reads `/data/entities.yaml` (via `EntitiesConfigLoader`) to pre-fill per-entity detector
  assignments + params; include/exclude patterns come from the Phase-2 `_patterns:` block.

### Claude's Discretion
- Exact htmx wiring for the expandable rows / add-detector control; the precise param-field set beyond the
  known keys above; whether `ScoreStreamPipeline` takes `ILiveEntitiesConfig` by ctor or reads it in
  `BuildEntityStates` (implementation detail — must read the swapped config on restart).
</decisions>

<code_context>
## Existing Code Insights

### Must-change (config-capture → live)
- `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` — captures `EntitiesConfig` at ctor
  (`_entities`; loops at lines ~127 and ~215 in RunBatchAsync + RunNightlyFitAsync). Switch to
  `ILiveEntitiesConfig.Get()` per cycle. It already iterates ALL detectors per entity and sends
  `{name, params}` over gRPC — so MAD/STL are already dispatched generically; no per-type C# logic needed.
- `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` — captures `EntitiesConfig` at ctor
  (`_entitiesConfig`); `BuildEntityStates()` (called at RunAsync entry, line ~80) only wires the `hst`
  detector (line ~252: `FirstOrDefault(d => d.Name == "hst")`). On loop restart it must read the swapped config.
- `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` — runs `_scoreStreamPipeline.RunAsync(..., stoppingToken)`
  once (line ~49). Add an inner CTS + ConfigChanged subscription + restart loop.
- `orchestrator/Argus.Orchestrator/Program.cs` — register `ILiveEntitiesConfig` singleton; migrate consumers
  (BatchSchedulerWorker factory, ScoreStreamPipeline, HaListenerWorker) from `EntitiesConfig` to it; add the
  detector-assignment endpoints/page.

### Reusable Assets
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs` — `DetectorConfig { Name, Params }` +
  `HstParams` typed defaults (source of HST default values for the UI).
- `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` — atomic save (reuse).
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` — read current config to pre-fill the UI.
- `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` — builds retained discovery payloads
  (binary_sensor + sensor); extend with a retract (empty-payload) path for removed entities.
- Phase-2 `HaSensorRegistry`, `GlobExpander`, `EntityPickerPage` — extend the picker with the detector section.
- `proto/argus.proto` — detector `params` is `map<string,string>` (generic); no proto change needed.

### Established Patterns
- Volatile singleton (ArgusHealthSignals / HaSensorRegistry) → the ILiveEntitiesConfig swap pattern.
- Minimal API endpoints + ConfigWriter save + HtmlEncode (Phase 1–2).
</code_context>

<specifics>
## Specific Ideas

- Reuse the Phase-2 picker page; the detector section is progressive disclosure (expand per row).
- "Saved — pipeline reloading…" banner is an htmx swap; the reload happens server-side via ConfigChanged.
</specifics>

<deferred>
## Deferred Ideas

- Full input validation (server + client) with error messages → Phase 4.
- CI multi-arch packaging + image-size gate + DOCS.md → Phase 4.
- Full `validate_session` Ingress auth middleware → Phase 4.
- Typed C# param accessors for MAD/STL (generic map suffices for Phase 3).
</deferred>
