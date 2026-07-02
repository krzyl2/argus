# Phase 8: Group Config UI + Algorithm Chooser - Context

**Gathered:** 2026-07-02
**Status:** Ready for planning

<domain>
## Phase Boundary

The final v4.0 UX phase. On the Phase 7 Preact+Vite SPA, and consuming the Phase 5/6 group backend, deliver: (1) a group authoring UI (create/edit named groups, pick members, mode, detector), (2) a transparent guided algorithm chooser (Low/Med/High sensitivity presets + Advanced override + "best for…" descriptions + a "what are you monitoring?" flow that pre-selects and explains), (3) sensor search by friendly_name + browse by HA area/domain + area-scoped group suggestions (approve-only), and (4) joint-multivariate anomaly attribution displayed as a ranked per-feature/per-member contribution instead of a flat boolean. Read-only attribution in the Argus UI (no custom HA dashboards — PROJECT.md exclusion).

Covers requirements: GRP-09, ALGO-01, ALGO-02, ALGO-03, ALGO-04, SRCH-01, SRCH-02, SRCH-03.
</domain>

<decisions>
## Implementation Decisions

### Group Authoring UI + Persistence
- New SPA routes: `#/groups` (list) + `#/groups/new` and `#/groups/:id` (editor). `#/sensors` (Phase 7) stays.
- New backend endpoints: `GET /api/groups` (list groups from live config) + `POST /api/groups/save` (writes the top-level `groups:` list to entities.yaml via the existing ConfigWriter + LiveEntitiesConfig.Swap pipeline — same hot-reload path, no restart). Ingress auth (IsAuthorizedRequest) first, as with all /api/*.
- Members picker reuses the Phase 7 SensorList / SensorSearchInput components in multi-select mode.
- Client-side validation mirrors the Phase 6 backend group guards: min-member floor (3), unit consistency for peer-divergence groups (using HA-sourced unit metadata) — validated before save; backend remains the authority (degrade-not-crash on load).

### Algorithm Chooser (ALGO-01..04)
- Backend `GET /api/detectors/catalog` is the single source of truth: per group detector it returns the Low/Med/High presets (→ concrete param values), the "best for…" description (ALGO-03), and the param schema (types/ranges for the Advanced form).
- Preset expansion: the chosen preset is stored as a label AND expanded to concrete params written into the group config (self-contained YAML). The Advanced toggle (ALGO-02) reveals the raw params and lets the operator override individual values behind the preset (ALGO-01).
- Guided flow (ALGO-04): a "what are you monitoring?" step (e.g. "a room/area's related sensors" → joint ECOD; "which one diverges from its peers" → peer-divergence) pre-selects a detector, VISIBLY shows/explains the pick, and always allows one-click override. Never an opaque auto-pick (Out-of-Scope: fully-automatic selection).
- Scope: presets/chooser apply ONLY to the new group detectors (peer_divergence, ECOD, COPOD, PCA, IForest). Univariate MAD/STL/HST are unchanged (ALGO-F1 uniform sensitivity is deferred to a future milestone).

### Sensor Search & Browse (SRCH-01..03)
- Discovery enrichment: fetch HA `area_registry` + `entity_registry` (via the existing HaWebSocketClient WS path) so each discovered sensor carries `friendly_name`, `area_name`, `domain`, and `unit_of_measurement`; cached at config-load / discovery time.
- Search (SRCH-01): extend SensorSearchInput to match friendly_name AND entity_id (today entity_id only).
- Browse (SRCH-02): sensor list is grouped/collapsible by HA area (fallback to domain when no area).
- Suggestions (SRCH-03): "N sensors share area X — group them?" surfaced as an operator-approved proposal that pre-fills the group editor; NEVER auto-groups (Out-of-Scope: automatic dynamic group discovery).

### Python Detector Param-Wiring (ALGO-01/02 — added after research)
- **In scope (operator-confirmed):** the Python group detectors currently read NO tunable params (peer_divergence threshold is a hardcoded `_THRESHOLD=3.5`; `GroupMultivariateDetector.__init__` takes only the algorithm name; `servicer` never passes `request.params` for group detectors). Without wiring, Low/Med/High presets would be cosmetic. Phase 8 therefore ADDS Python-side param-wiring so presets genuinely change detection:
  - `peer_divergence`: accept a `from_params`-style threshold (Low/Med/High → e.g. stricter/looser modified-z cutoff), replacing the hardcoded constant as the default.
  - `GroupMultivariateDetector`: accept the params that actually move the published continuous score where the detector supports it; be HONEST where a detector is parameter-free (ECOD/COPOD's only knob is `contamination`, which shifts the binary threshold, NOT the continuous decision_function score that MQTT publishes — preset copy must say so).
  - `servicer.py`: pass `request.params` into the group detector factory (mirror the existing per-entity `PyODDetector.from_params()` precedent).
- The catalog's preset→param mapping must correspond to params the detector actually honors after this wiring. Preset copy is precise about parameter-free detectors.

### Open-Question Resolutions (research)
- Area resolution: entity-only `area_id` + domain fallback for v1; device_registry-inherited-area join is a documented fast-follow (many entities have null own area_id, inheriting from their device) — noted, not implemented this phase.
- `FeatureContribution` ranking/sort happens in the .NET `GroupStatusCache` (server-side), so the SPA's "already ranked" contract holds.
- HA `config/area_registry/list` / `config/entity_registry/list` field shapes are LOW-confidence (not fully documented) — treat as a live-HA verification item; implement defensively.

### Attribution Display (GRP-09)
- Data source: the orchestrator retains each group's last verdict + `FeatureContribution` list in memory (analogous to the existing health signals cache), exposed via `GET /api/groups/{id}/status`.
- Presentation: a ranked per-feature/per-member contribution (sorted list / bar) instead of a flat boolean. Attribution is only available for detectors that produce it (ECOD/COPOD — from Phase 5); PCA/IForest return null contributions → show a "no per-feature attribution for this detector" message, not a fake ranking.
- Refresh: the SPA polls `/api/groups/{id}/status` while a group's status view is open (roughly the batch interval cadence). No SSE.
- Scope: read-only display in the Argus UI. No custom HA dashboards (PROJECT.md exclusion); the HA entities themselves remain as shipped in Phase 6.
</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets (Phase 7 SPA)
- `orchestrator/ui/src/` — Preact+Vite+TS SPA: `router.ts` (hand-rolled hash router — add #/groups routes), `api/client.ts` (relative-fetch wrapper — add group calls, NO leading slash), `api/types.ts` (add GroupConfig/DetectorCatalog/GroupStatus types), `state/sensors.ts` (signals state pattern — add groups state), `validation/detectorParams.ts` (client validation parity — extend for group rules), components (SensorList, SensorSearchInput, SensorListRow, DetectorEntry, DetectorParamGrid, DetectorDisclosure, SaveBar, SaveResultBanner, EmptyState, FieldValidationError, AppShell).
- `argus.css` (in orchestrator/ui/public/css) carried-forward tokens — reuse; no new design system.

### Reusable Assets (backend, Phase 5/6)
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs` — `GroupConfig` + `EntitiesConfig.Groups` (Phase 6). The /api/groups endpoints read/write these.
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` — `ValidateGroups` (floor/unit/dedup, warn-not-throw). Client validation mirrors these rules.
- `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` + `LiveEntitiesConfig` — write + hot-swap; /api/groups/save reuses this exactly (hot-reload parity).
- `orchestrator/Argus.Orchestrator/Program.cs` — `IsAuthorizedRequest` (call first on every new endpoint), JSON endpoint pattern (Phase 7 `SaveRequest.cs`, `DetectorDefaults.cs`), `MapFallbackToFile`, static files. New endpoints wire here.
- `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` — group scoring loop (Phase 6) produces the group verdict + contributions; this is where the last-verdict-in-memory cache is populated for /api/groups/{id}/status.
- Proto: `GroupScoreResponse` carries `FeatureContribution` (Phase 5) — the attribution data already flows to the orchestrator.
- HA WebSocket client (HaWebSocketClient) — used for get_states discovery; extend for area_registry/list + entity_registry/list.
- `DetectorDefaults.cs` (Phase 7) — the detector default param values; the catalog endpoint's preset "Med" likely aligns with these defaults.

### Established Patterns
- JSON endpoints: IsAuthorizedRequest first, ReadFromJsonAsync for POST DTOs, InvariantCulture for numbers, nested DTO shape mirrored in types.ts.
- SPA: relative fetch only (Ingress base-path safety), hash routing, @preact/signals state, Vitest + @testing-library/preact tests, argus.css tokens.
- Config write: InputValidator/Validate → ConfigWriter.WriteAsync → lock file → LiveEntitiesConfig.Swap (no restart).
- Fault isolation + degrade-not-crash for group config.

### Integration Points
- Program.cs — new /api/groups, /api/groups/save, /api/detectors/catalog, /api/groups/{id}/status endpoints (auth-guarded).
- EntitiesConfig.Groups — the authoring UI's read/write target.
- BatchSchedulerWorker group loop — populate the in-memory last-verdict/contribution cache consumed by /api/groups/{id}/status.
- HaWebSocketClient — area/entity registry enrichment for search/browse/suggestions.
- SPA router.ts / api layer / components — the new screens.
</code_context>

<specifics>
## Specific Ideas

- Transparency is the hard requirement across ALGO-01..04: presets hide raw params by DEFAULT but the Advanced toggle must reveal+override them; the guided pick must be VISIBLE and one-click-overridable — no opaque black box (explicit Out-of-Scope).
- Attribution (GRP-09) must be a REAL ranked contribution from the detector (ECOD/COPOD FeatureContribution), not a fabricated ranking; PCA/IForest honestly show "no per-feature attribution."
- Suggestions (SRCH-03) and group membership are ALWAYS operator-approved — never auto-grouped, never auto-selected algorithm.
- Preserve zero-regression on Phase 7 capability: the #/sensors screen and per-entity flow keep working; group features are additive.
- No new continuous sensitivity slider (Out-of-Scope) — discrete Low/Med/High + Advanced only.

## Human Verification (carry forward)
- Like Phase 7's UI-02, the full live-HA Ingress round-trip for the new screens is human-verified at deploy. Automated tests cover component/endpoint behavior; the live "Open Web UI" click-through is a deferred human item.
</specifics>

<deferred>
## Deferred Ideas

- Uniform Low/Med/High sensitivity across univariate MAD/STL/HST (ALGO-F1) — future milestone, after the per-detector-family mapping is proven on the group detectors.
- Streaming group detection + live streaming attribution (STRM-01/02) — out of scope this milestone.
- Cross-group "meta-anomaly" dashboard / any custom HA dashboards — explicit Out-of-Scope (PROJECT.md).
- Fully-automatic algorithm selection or a continuous sensitivity slider — explicit Out-of-Scope.
</deferred>
