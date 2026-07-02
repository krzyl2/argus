# Project Research Summary

**Project:** Argus v4.0 — Group & Multivariate Anomaly Detection + Light-SPA UI
**Domain:** Self-hosted Home Assistant add-on extension — new ML detection modes (peer-divergence, joint-multivariate) over an existing .NET 8 + Python gRPC anomaly pipeline, plus a frontend rebuild from server-rendered htmx to a built SPA.
**Researched:** 2026-07-02
**Confidence:** MEDIUM-HIGH overall

## Executive Summary

Argus v4.0 extends a shipped, working v1-v3 pipeline (per-entity univariate detection to MQTT-discovered HA entities) with two genuinely new detection modes and a UI rebuild. Peer-divergence ("which of these 4 tire pressures is off") and joint-multivariate ("is this room's temp+humidity combination abnormal") are architecturally distinct problems requiring different output shapes (per-member entities vs. single group-level entity) and different algorithms (custom leave-one-out z-score/MAD vs. PyOD's native multivariate detectors -- PCA/ECOD/COPOD/HBOS, all already available in the pinned pyod==3.6.0 dependency, zero new ML library needed). Both modes share one hard prerequisite: time-alignment of independently-reporting sensors onto a common grid, which must happen server-side in InfluxDB (aggregateWindow+pivot) before anything crosses the gRPC boundary -- this is both the correct architecture (keeps Python pure-numerics, reuses the existing .NET-queries/Python-scores contract) and the most consequential foundational decision in the milestone.

The recommended approach is additive throughout: extend argus.proto with new group messages/RPCs on the same DetectorService (never break wire compatibility with the existing univariate path), add a new top-level EntitiesConfig.Groups list (retiring the unused EntityConfig.Groups/.Covariates placeholders rather than populating them), and introduce parallel-but-not-forked components (GroupBatchSchedulerWorker, GroupDetectorRegistry, GroupDiscoveryPublisher) that reuse shared infrastructure (ModelStore, DetectionGateway, MQTT publishing conventions) wherever the underlying data shape does not force a fork. The SPA (Preact + Vite, built at Docker build-time only, never shipped with Node in the runtime image) replaces htmx specifically because the algorithm chooser and friendly-name/area search need real client-side state -- a deliberate, documented reversal of v3.0's htmx decision.

The dominant risks are subtle-correctness failures that "look done but aren't": faking multivariate detection by looping univariate calls and diffing in .NET (never actually catches joint anomalies), unscaled mixed-unit features letting pressure dominate humidity in joint scores, index-zipping unaligned time series instead of resampling, and small-N (2-3 member) groups where peer-divergence math degenerates without a robust (median/MAD) statistic and a minimum-N guard. On the UI/ops side, the SPA reintroduces the HA Ingress dynamic-base-path problem that htmx deliberately avoided (must use hash routing or runtime path templating), and the Node build step must not blow past the project's existing image-size discipline. Mitigations for all of these are well-documented and should be treated as explicit phase-acceptance criteria, not discovered late.

## Key Findings

### Recommended Stack

No new ML dependency is needed -- pyod==3.6.0 (already pinned) natively supports the multivariate detectors (PCA, ECOD, COPOD, IForest) needed for joint-multivariate mode via its standard fit(X)/decision_function(X) API on (n_samples, n_features) matrices; peer-divergence is ~20 lines of custom numpy (leave-one-out median/MAD), not a library. The SPA layer is new: Preact 10.x + Vite 8.x, built in a Docker multi-stage step and baked into the image as static assets -- Node/npm never appear in the final runtime image. Time-alignment uses InfluxDB's native Flux aggregateWindow() + pivot() server-side, requiring no new .NET package (existing InfluxDB.Client 5.0.0 already supports arbitrary Flux via QueryApi).

**Core technologies:**
- PyOD (existing, new usage mode) -- multivariate detectors for joint-anomaly scoring -- already licensed, already pinned, zero migration cost
- Preact + Vite -- light SPA runtime/build -- smaller footprint than React/Vue, React-compatible API keeps learning curve low
- InfluxDB Flux aggregateWindow/pivot -- server-side time-alignment -- avoids duplicating resampling logic in a second language

### Expected Features

**Must have (table stakes):**
- Peer-divergence group detection with per-member attribution (per-member binary_sensor + score, NOT a single group flag -- preserves the existing "one flag+score per entity" HA contract)
- Joint-multivariate group detection with single group-level flag+score (different output shape than peer-divergence -- different question being answered)
- Batch-first InfluxDB time-alignment (hard prerequisite for both modes)
- Explicit, operator-defined group membership config (no auto-discovery)
- Sensitivity preset (Low/Med/High) + Advanced toggle, replacing raw parameter exposure
- Friendly-name search + area/domain categorization of the sensor list

**Should have (competitive):**
- "Best for..." descriptions per algorithm in the chooser (cheap, high value)
- Guided "what are you monitoring?" chooser that shows and explains its pick, never hides it (this operator profile explicitly prefers direct control over magic)
- Area-scoped group suggestions (operator approves, never auto-groups)

**Defer (v2+):**
- Per-member attribution for joint-multivariate anomalies (needs per-feature reconstruction error/Shapley -- real complexity, ship base joint detection first)
- Streaming groups (explicitly deferred in PROJECT.md pending batch-model validation -- LVCF/windowing across independently-arriving streams has no existing analogue in the codebase)
- Fully automatic algorithm selection, continuous sensitivity slider, automatic dynamic group discovery, cross-group meta-dashboards -- all explicitly anti-features for a single-operator, no-cloud, no-support-team tool

### Architecture Approach

Additive-only extension of the existing .NET 8 orchestrator + Python gRPC detector: new proto messages/RPCs (GroupScoreBatch/GroupFit) on the same DetectorService; a new parallel GroupBatchSchedulerWorker (own timer, own latency target -- separate from the <2s single-sensor Core Value) rather than branching inside the existing BatchSchedulerWorker; group keying reuses the existing (subject_id, detector) tuple pattern with group_id substituting for entity_id (prefixed internally to avoid namespace collision); resampling/alignment lives in .NET (Flux), never in Python -- Python only ever receives pre-aligned matrices.

**Major components:**
1. GroupConfig / EntitiesConfig.Groups (new top-level list) -- group-centric config, retiring the old per-entity Groups/Covariates object stubs
2. GroupBatchSchedulerWorker + GroupDiscoveryPublisher (.NET) -- parallel scheduling/publishing, fault-isolated from single-entity path
3. GroupDetectorRegistry with MultivariatePyODDetector (joint mode) and PeerDivergenceDetector (peer mode) (Python) -- one new RPC, mode as a request field, not one RPC per algorithm

### Critical Pitfalls

1. **Fake multivariate via looped univariate calls** -- never fires on genuine joint anomalies; must extend the proto with a real 2D-matrix message before writing detection code.
2. **Unscaled mixed-unit features** -- pressure (~950-1050 hPa) drowns out humidity/temperature in joint scores unless every feature is z-scored against its own rolling stats before fitting; persist the scaler with the model.
3. **Index-zipped unaligned time series** -- comparing sensor A's 09:00 reading to sensor B's 09:47 reading produces meaningless joint/peer comparisons; always resample via Flux aggregateWindow, with a staleness cap on forward-filled gaps.
4. **Small-N peer-divergence instability** -- N=2-3 groups make "divergence from consensus" mathematically close to meaningless with mean/stddev; require a minimum-N floor and use median/MAD.
5. **Group MQTT entity churn** -- deriving unique_id from group membership (rather than a stable operator-assigned group_id) orphans HA entities every time membership is edited.
6. **SPA breaks HA Ingress's dynamic base path** -- the exact problem htmx was chosen to avoid in v3.0; SPA must use hash routing + relative asset paths, or runtime X-Ingress-Path templating -- verify only via "Open Web UI" in real Supervisor, never direct port access.

## Implications for Roadmap

Based on research, suggested phase structure:

### Phase 1: Proto + Python Detector Core
**Rationale:** Foundational contract decision (real multivariate message shape) that everything else depends on; independently testable without .NET or InfluxDB (avoids Pitfall 1 by construction).
**Delivers:** Additive argus.proto extension (GroupScoreBatch/GroupFit RPCs, wire-compatible), MultivariatePyODDetector, PeerDivergenceDetector, GroupDetectorRegistry, reused ModelStore with group__ prefix namespace guard.
**Addresses:** Peer-divergence + joint-multivariate detection (table stakes from FEATURES.md)
**Avoids:** Pitfall 1 (fake multivariate), Pitfall 2 (unscaled features -- add mixed-unit synthetic test as acceptance gate), Pitfall 5 (small-N -- add minimum-N guard + median/MAD)

### Phase 2: Batch-First Group Pipeline (.NET orchestrator wiring)
**Rationale:** Depends on Phase 1's proto/detector contract; wires the real HA-InfluxDB-gRPC-MQTT path end to end.
**Delivers:** GroupConfig/EntitiesConfig.Groups, Flux-based group-aware Influx query (aggregateWindow+pivot), GroupBatchSchedulerWorker (own timer, own latency target), GroupDiscoveryPublisher with stable group_id-keyed unique_id.
**Uses:** InfluxDB.Client 5.0.0 (existing), Flux aggregateWindow/pivot
**Implements:** ADR-1 (alignment in .NET), ADR-4 (parallel worker, not a branch), ADR-5 (separate latency target)
**Avoids:** Pitfall 3 (time-alignment), Pitfall 7 (MQTT churn), Pitfall 8 (shared-resource latency leakage into the <2s streaming path)

### Phase 3: Group Config Validation & Unit-Compatibility Guards
**Rationale:** Cheap, purely-additive validation that must land before real operators can create broken groups; sequenced after Phase 2's config model exists.
**Delivers:** Config-load-time validation (unit_of_measurement compatibility check, group_id/entity_id namespace disjointness, minimum-N enforcement for peer mode), tolerant loader for back-compat with pre-v4.0 entities.yaml.
**Addresses:** Pitfall 4 (unvalidated mixed-unit groups), Pitfall 12 (schema activation breaking old configs)

### Phase 4: SPA Scaffolding
**Rationale:** Must be solved and verified against real HA Ingress before any feature UI is built on top of it -- this is a blocking foundation, not incidental plumbing (mirrors how v3.0 treated the equivalent htmx pitfall as Phase 1).
**Delivers:** Preact+Vite build pipeline, multi-stage Dockerfile addition, hash-based routing (or runtime base-path templating), route-group auth middleware for all /api/* endpoints, re-baselined image-size gate.
**Avoids:** Pitfall 9 (Node build/image bloat), Pitfall 10 (Ingress base-path breakage), Pitfall 11 (missing auth on new API endpoints)

### Phase 5: Group Config UI + Algorithm Chooser UX
**Rationale:** Depends on both the group pipeline (Phase 2/3) and the SPA foundation (Phase 4); this is where the user-facing value (chooser, search, presets) actually surfaces.
**Delivers:** Group authoring UI (multi-select members, mode toggle, detector+params), sensitivity Low/Med/High presets (designed per-detector-type, sequenced after group detector parameters are known -- Pitfall 13), friendly-name search, area/domain categorization, "best for..." descriptions.
**Addresses:** Remaining table-stakes and differentiator features from FEATURES.md
**Avoids:** Pitfall 6 (overconfident attribution -- surface as ranked score, not flat boolean), Pitfall 13 (presets that do not fit group detector parameter shapes)

### Phase Ordering Rationale

- Detection core (backend, both modes) must precede and be independently verifiable before any UI work touches it -- the ML correctness pitfalls (1, 2, 3, 5) are cheapest to catch in isolated unit/integration tests, not through a UI.
- Config validation is deliberately its own thin phase between pipeline and UI -- it is cheap insurance against silent-nonsense pitfalls (4, 12) and blocks nothing else.
- SPA scaffolding is sequenced as its own phase specifically because Pitfall 10 (Ingress base path) is a "must verify against real Supervisor before building on top" foundation, exactly parallel to how v3.0 treated htmx+Ingress.
- Group config UI is deliberately last because it depends on both the backend group pipeline being real (Phase 2/3) and the SPA shell existing (Phase 4) -- building it earlier risks the SPA-rebuild timeline gating backend UX-support work, which FEATURES.md explicitly warns against.

### Research Flags

Phases likely needing deeper research during planning:
- **Phase 1 (Proto + Detector Core):** PyOD multivariate parameter tuning and scaler-persistence lifecycle design warrant a research pass at plan time -- the "which default algorithm, which scaler strategy" decisions aren't fully pinned.
- **Phase 4 (SPA Scaffolding):** HA Ingress base-path handling for SPAs is a known-tricky, thinly-documented area (LOW-confidence sources in PITFALLS.md/STACK.md); verify the chosen strategy (hash routing vs. runtime templating) against a real Supervisor install early.

Phases with standard patterns (skip research-phase):
- **Phase 2 (Batch Pipeline):** Flux aggregateWindow/pivot and the .NET worker pattern are well-documented and mirror existing code (BatchSchedulerWorker) directly.
- **Phase 3 (Config Validation):** Straightforward extension of existing EntitiesConfigLoader validation patterns.

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | MEDIUM-HIGH | PyOD/InfluxDB facts cross-checked against official docs and the repo's own pinned dependencies (HIGH); Preact/Vite bundle-size figures are LOW-confidence web-search snapshots (directional only) |
| Features | MEDIUM | Patterns cross-corroborated across commercial tools (Datadog, New Relic, Azure) and ML literature, but no single authoritative source for a self-hosted single-operator HA anomaly tool -- treat feature shape decisions (Option B output shape) as solid, specific algorithm citations as directional |
| Architecture | HIGH | Proto, EntitiesConfig, ModelStore, DetectorRegistry, BatchSchedulerWorker, DiscoveryPublisher, servicer.py all read directly from source; PyOD multivariate API confirmed via official docs |
| Pitfalls | HIGH (codebase-grounded) / MEDIUM (general ML/SPA) | Ingress middleware, MQTT discovery, config model pitfalls read directly from source (HIGH); general ML/time-series/SPA-ecosystem pitfalls are web-sourced but cross-checked across independent sources |

**Overall confidence:** MEDIUM-HIGH

### Gaps to Address

- Default algorithm choice for joint-multivariate mode (PCA vs. ECOD vs. COPOD) is not locked -- STACK.md recommends starting with one as default and exposing others via the chooser; finalize during Phase 1 planning.
- Exact minimum-N threshold for peer-divergence (N>=3 vs. N>=4-5) needs a concrete decision during Phase 1 planning -- PITFALLS.md flags the risk but doesn't mandate a specific number.
- SPA Ingress base-path strategy (hash routing vs. runtime templating) is presented as an open choice with tradeoffs in STACK.md/PITFALLS.md -- decide explicitly at the start of Phase 4, verify against real Supervisor before building further UI on top.
- Whether group members require a parallel EntityConfig entry is flagged as an open design question in ARCHITECTURE.md (recommendation: no) -- confirm this decision explicitly during Phase 2/3 planning.
- New explicit image-size ceiling (replacing the stale 2GB v3.0 gate) is not yet chosen -- needs a concrete number during Phase 4 planning.

## Sources

### Primary (HIGH confidence)
- Repo source read directly: proto/argus.proto, Config/EntitiesConfig.cs, Config/EntitiesConfigLoader.cs, Batch/BatchSchedulerWorker.cs, Batch/InfluxDbReader.cs, Mqtt/DiscoveryPublisher.cs, Detection/DetectionGateway.cs, detector/argus_detector/{model_store,registry,servicer,pyod_detector}.py, detector/requirements.txt, argus/Dockerfile, .planning/PROJECT.md
- PyOD official docs (https://pyod.readthedocs.io/) and PyOD GitHub LICENSE (https://github.com/yzhao062/pyod/blob/master/LICENSE) -- BSD-2-Clause, multivariate API confirmed
- ASP.NET Core static files docs (https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files?view=aspnetcore-8.0)

### Secondary (MEDIUM confidence)
- InfluxDB Flux aggregateWindow/pivot docs (https://docs.influxdata.com/flux/v0/stdlib/universe/aggregatewindow/)
- Datadog (https://docs.datadoghq.com/monitors/types/anomaly/) / New Relic (https://docs.newrelic.com/docs/alerts/create-alert/set-thresholds/anomaly-detection/) / Azure AI Anomaly Detector (https://azure.microsoft.com/en-us/products/ai-services/ai-anomaly-detector) -- sensitivity/preset UX patterns
- Home Assistant entity filter / selectors docs (https://www.home-assistant.io/dashboards/entity-filter/)

### Tertiary (LOW confidence)
- Preact/Vite bundle-size comparisons (WeBridge, StackShare, Sentry Engineering blog) -- directional only, not benchmarked against this project's UI
- Various ML/time-series arXiv papers on multivariate attribution and root-cause analysis -- used for pitfall framing, not algorithm selection
- HA Ingress + SPA community threads -- consistent directional guidance (hash routing / runtime base-path templating), not officially documented by HA

---
*Research completed: 2026-07-02*
*Ready for roadmap: yes*
