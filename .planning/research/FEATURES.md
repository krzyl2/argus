# Feature Research

**Domain:** Self-hosted home-automation anomaly detection — group/multivariate detection + algorithm-chooser UX (v4.0 milestone)
**Researched:** 2026-07-02
**Confidence:** MEDIUM (patterns cross-corroborated across commercial tools and ML literature; no single authoritative source, so treat specifics as directional not gospel)

> Note: this file covers ONLY the v4.0 milestone (group/multivariate detection + UX). The prior v3.0 Ingress Configuration UI research this file previously held is shipped and preserved in git history (see commit history for `.planning/research/FEATURES.md` prior to 2026-07-02).

## Context Recap (from PROJECT.md)

Existing pipeline: per-entity univariate detection (MAD, STL, River HST) → MQTT-discovered `binary_sensor` (flag) + `sensor` (score) per source entity. v3.0 shipped Ingress config UI (entity_id search only, per-entity detector assignment, hot-reload). v4.0 must add: peer-divergence group detection, joint-multivariate group detection, batch-first via InfluxDB resampling, expanded algorithm library with friendly chooser, friendly-name/area search, and a light-SPA UI rebuild.

---

## Feature Landscape

### Table Stakes (Users Expect These)

Features a single operator will consider "obviously required" once group detection exists at all — missing these makes the group feature feel broken or untrustworthy.

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| Peer-divergence group detection with per-member attribution | The canonical use case (4 tire pressures, one diverges) is meaningless without knowing WHICH member — a single "group anomaly" flag with no attribution answers the wrong question | MEDIUM | Standard approach: compute each member's deviation from group consensus (e.g. z-score of residual-from-group-mean, or pairwise distance to peers); a genuinely diverging member shows abnormal distance to ALL peers while healthy members show only minor mutual deviation. No new ML library needed — this is arithmetic over already-scored/aligned series, or a simple robust-stats layer on top |
| Group membership defined at config time | Operator already assigns detector+params per entity in v3.0 UI; grouping is a natural extension, not a new mental model | LOW | Reuses `EntityConfig.Groups` placeholder already in the model (currently parsed-and-ignored) |
| Time-alignment for group members before comparison | Sensors report on independent schedules; comparing raw unaligned samples produces false divergence | MEDIUM | Batch-first plan already covers this via InfluxDB resampling to a common grid (confirmed available). This is a hard prerequisite, not optional — do this before any group algorithm work |
| Per-member HA entity output for peer-divergence (not just a single group-level flag) | HA automations key off individual entities; "group anomaly, go check which one yourself" breaks the existing UX contract Argus already set (one flag+score pair per source entity) | MEDIUM–HIGH | See **Output-Shape Decision** below — this is the most consequential design choice in the milestone |
| Joint-multivariate group detection with a single group-level flag+score | Different semantic than peer-divergence: the question is "is this combination normal," not "who's the outlier" — one flag per group is the correct and expected shape here, no per-member attribution implied | MEDIUM | Standard approach: PyOD multivariate detectors (PCA reconstruction error, ECOD, COPOD, IsolationForest) or Hotelling's T² over the aligned feature vector. PyOD already in stack (D10) |
| Sensitivity preset (Low/Med/High) replacing raw parameter exposure | Operator is not a data scientist; existing v3.0 UI already exposes per-entity detector+params — friendly presets are the natural next step for expanded algorithm library, and every commercial anomaly tool surveyed (Datadog, New Relic, Dynatrace, GA4) does this | LOW–MEDIUM | Maps preset labels to internal params per detector (e.g. MAD threshold multiplier, PyOD contamination rate, HST window size). Pure UI/config-mapping work, no new ML |
| Advanced toggle to reveal raw parameters | Power users (this operator, notably) will eventually want to override a preset for one sensor; hiding the escape hatch is worse than not having presets at all | LOW | Simple UI show/hide; params underneath already exist |
| Search sensors by friendly name (not just entity_id) | Already an explicit v4.0 requirement; entity_id search alone is the acknowledged v3.0 gap | LOW–MEDIUM | HA's own entity picker component already does fuzzy search across entity_id + friendly name + area + domain — this is the reference pattern, not something to design from scratch |
| Categorize/browse long sensor list by HA area and/or domain | Same picker reference pattern; with dozens of rooms × sensor types the flat list becomes unusable well before "hundreds of entities" | LOW–MEDIUM | Requires area metadata from HA (available via WebSocket API, already a data source); group config UI benefits doubly since groups are naturally area-scoped (e.g. "all bedroom sensors") |

### Differentiators (Competitive Advantage)

Not required for group detection to "work," but meaningfully raise trust/usability for a self-hosted single-operator tool with no support team to lean on.

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| "Best for…" descriptions per algorithm in the chooser | Operator picking between MAD/STL/HST/PCA/ECOD/COPOD has no ML background; a one-line "best for slow-drifting sensors" vs "best for spiky/bursty sensors" turns algorithm choice from guesswork into a decision | LOW | Static copy, no logic — highest value-to-effort ratio in this milestone |
| Guided "what are you monitoring?" chooser (template → algorithm pre-select) | Commercial tools (Elastic ML jobs, Azure AI Anomaly Detector, Flowmon) use exactly this pattern: pick a data-type template, get a sensible default, still land on manual selection underneath | MEDIUM | Do NOT build a black-box "auto-select and hide the choice" — always show and allow override of what got picked, given this operator's stated preference for direct control over magic |
| Per-member contribution/attribution for the joint-multivariate case (which sensor drove the joint anomaly) | Answers "the room is jointly abnormal — but is it the humidity or the temp that's driving it?" Genuinely useful for the leak-detection example, not just nice-to-have | HIGH | Standard technique is per-feature reconstruction error (PCA/autoencoder) or Shapley decomposition — meaningfully harder than peer-divergence attribution (needs a trained model with feature-level error, not just an aggregate score). Treat as a stretch differentiator, not v4.0 baseline; can ship joint-multivariate WITHOUT this first, add attribution in a later phase once the base output shape is proven |
| Sensitivity preset applies uniformly across detector families | Avoids the operator having to relearn what "High sensitivity" means when switching a sensor from MAD to PCA | MEDIUM | Requires a normalized internal sensitivity scale (e.g. 0.0–1.0) that each detector adapter maps to its own native parameter range — real design work, but pays for itself immediately given "expanded algorithm library" is explicitly in scope |
| Area-scoped group suggestions ("these N sensors share an area — group them?") | Reduces group-config friction; area metadata is already available from HA | LOW–MEDIUM | Purely a config-UI suggestion, not automatic grouping — keep operator in control |

### Anti-Features (Commonly Requested, Often Problematic)

Things that look attractive for an anomaly tool but are wrong for a self-hosted, single-operator, no-cloud, no-support-team context.

| Feature | Why Requested | Why Problematic | Alternative |
|---------|---------------|------------------|-------------|
| Fully automatic algorithm selection (tool picks the "best" detector with no visible reasoning) | Feels like it removes cognitive load ("just make it smart") | Single operator has no one to escalate to when the black box is wrong; opaque auto-selection erodes exactly the trust this tool depends on (self-hosted users already chose Argus over a cloud SaaS specifically for control). Also contradicts the developer's own stated preference for direct, opinionated tooling over magic | Guided chooser that pre-selects AND shows why, with one-click override — "recommended: X because Y" not "we picked X" |
| Continuous sensitivity slider (0–100) instead of Low/Med/High presets | Feels more "precise" | False precision — the operator has no ground truth to calibrate against, so a 100-point scale just creates decision paralysis without adding real control. Every commercial tool surveyed converges on discrete presets + advanced escape hatch, not a raw continuous default | Low/Med/High presets (3 well-chosen points) + Advanced toggle for the rare case a raw param is truly needed |
| Automatic dynamic group discovery (ML infers which sensors "belong together") | Sounds impressive, reduces config work | Unverifiable and untrustworthy for a 1-operator, dozens-of-sensors deployment — wrong automatic groupings silently produce wrong anomaly attributions with no feedback loop to catch it. This is exactly the kind of "smart" feature explicit config wins over | Explicit group config (already table stakes above), optionally with area-scoped *suggestions* the operator approves |
| Real-time/streaming group detection in this milestone | "Streaming already exists for univariate, why not groups too?" | PROJECT.md already explicitly defers this ("Streaming groups... after batch groups prove the model") — time-alignment across independently-reporting sensors is fundamentally harder in a streaming context (no fixed grid to resample onto), and doing it before the batch model is validated risks building the wrong abstraction twice | Batch-first via InfluxDB resampling now; streaming (windowed, last-value-carried-forward) only after the batch group model is proven in production |
| Cross-group / whole-house "meta-anomaly" dashboard (anomaly of anomalies) | Feels like a natural next layer once groups exist | Scope creep well beyond "analyze groups of sensors" — turns Argus into a dashboarding/BI tool, which PROJECT.md explicitly puts out of scope ("Custom HA dashboards — auto-created entities are sufficient") | Keep output as auto-created HA entities per group; if the operator wants a rollup view, that's an HA dashboard concern, not Argus's |
| Notification/alerting logic tied to sensitivity presets (e.g. "High sensitivity = also page me") | Natural-feeling extension once you have a sensitivity concept | PROJECT.md explicitly excludes acting on anomalies — "Argus only exposes entities; operator wires reactions in HA/Node-RED." Presets must stay confined to detection threshold, never bleed into notification behavior | Presets affect only detector params; all alerting stays downstream in HA automations, unchanged |

---

## Output-Shape Decision: Peer-Divergence Group Detection

This is the most consequential open design question the downstream requirements phase must settle explicitly. Two shapes were considered:

### Option A — One binary_sensor per group + a "which member" attribute
- **Shape:** `binary_sensor.tire_pressure_group_anomaly` with state `on`/`off`, plus an attribute like `diverging_member: sensor.tire_pressure_fl`.
- **Pros:** Fewer entities created; matches "group anomaly" framing conceptually; simpler MQTT discovery footprint (one flag+score pair per group, like today's per-entity pattern).
- **Cons:** HA automations cannot template-trigger cleanly off an *attribute* the way they can off entity *state* — templating on an attribute value works but is clunkier and less discoverable than a dedicated entity; if the group later needs to flag multiple simultaneously-diverging members (e.g. two tires wearing together), a single attribute can't represent that without becoming a list, which historically causes HA templating pain.
- **Fits existing pattern?** No — breaks the "one flag+score pair per source entity" contract Argus established in v1–v3.

### Option B — Per-member binary_sensor + score, scoped under the group
- **Shape:** Each group member gets its own `binary_sensor.<entity>_peer_divergence` + score sensor (mirroring exactly how today's per-entity univariate detectors work), where the score reflects "how much this member currently diverges from its group's peers."
- **Pros:** Fully consistent with the existing v1–v3 output contract — operator already knows how to consume flag+score pairs per entity; each member's divergence is independently automatable in HA (e.g. "notify if `binary_sensor.tire_pressure_fl_peer_divergence` turns on"); naturally supports multiple simultaneous divergent members without any data-structure change.
- **Cons:** More entities created (N members × 2 entities vs 2 entities per group) — for a 4-tire group this is 8 entities instead of 2; MQTT discovery volume scales with group size, not group count (relevant given the earlier "empty include patterns select nothing" flood-prevention fix already in v3.0 history — the project has been burned by entity-count explosions before).

### Recommendation

**Option B (per-member flag+score), with the group name/other-members exposed as an attribute for context.** Rationale:
1. Consistency with the already-shipped contract is worth more than saving entities — the operator (and any HA automation already written) expects "anomaly on this entity → binary_sensor for this entity."
2. Peer-divergence is semantically "does THIS member have a problem," which is a per-entity question even though it's computed relative to a group — the entity model should match the semantic, not the computation shape.
3. The project's own history (GlobExpander flood-prevention fix, 2.0.9) shows entity-count sensitivity is a known concern — mitigate by scoping this to explicitly-configured groups only (never auto-discovered), keeping counts bounded and operator-controlled.

This is the opposite shape from **joint-multivariate** detection (Option A pattern — one flag+score per GROUP, no per-member split), because the underlying question is different: peer-divergence asks "which member," joint-multivariate asks "is the combination normal." Do not conflate the two output shapes — they should follow different rules even though both are "group" features.

---

## Feature Dependencies

```
Batch-first InfluxDB resampling (time-alignment)
    └──requires──> [already have: InfluxDB confirmed available, D-model has Groups placeholder]
    └──blocks──> Peer-divergence group detection
    └──blocks──> Joint-multivariate group detection

Peer-divergence group detection (per-member output)
    └──requires──> Batch-first resampling
    └──requires──> Group config UI (define membership)
    └──enhances──> Existing per-entity univariate pipeline (reuses scoring/HA-entity infrastructure, does NOT replace it)

Joint-multivariate group detection (group-level output)
    └──requires──> Batch-first resampling
    └──requires──> Group config UI (define membership)
    └──requires──> Expanded algorithm library (PyOD multivariate detectors: PCA/ECOD/COPOD)

Per-member attribution for joint-multivariate (differentiator)
    └──requires──> Joint-multivariate group detection (base output shape proven first)
    └──requires──> Per-feature reconstruction error or Shapley decomposition (new capability, not in current PyOD usage)

Sensitivity preset (Low/Med/High)
    └──requires──> Normalized internal sensitivity scale per detector adapter
    └──enhances──> Both existing univariate AND new group detectors uniformly

"Best for..." descriptions
    └──requires──> Expanded algorithm library (need algorithms to describe)
    └──conflicts with──> nothing; pure additive UI copy

Guided "what are you monitoring?" chooser
    └──requires──> "Best for..." descriptions (reuses same algorithm-to-use-case mapping)
    └──enhances──> Sensitivity preset + Advanced toggle (guided flow should still land on the same preset/advanced controls, not a separate config path)

Friendly-name search + area/domain categorization
    └──requires──> HA area metadata (already available via WebSocket API, existing data source)
    └──enhances──> Group config UI (grouping is naturally area-scoped)

Light SPA UI rebuild
    └──blocks──> nothing functionally, but is the delivery vehicle for: sensitivity presets, "best for" copy, guided chooser, friendly-name search — sequencing these before/alongside the SPA rebuild matters for roadmap phase ordering
```

### Dependency Notes

- **Both group detection modes require batch-first resampling first:** this is a hard technical prerequisite (unaligned timestamps make any cross-sensor comparison meaningless), and PROJECT.md already sequences it this way. Roadmap should treat resampling as its own early phase, not bundled invisibly into the first detection-mode phase.
- **Per-member attribution for joint-multivariate should NOT be bundled with the base joint-multivariate phase.** It is meaningfully harder (needs per-feature error decomposition, not just an aggregate score) and the base group-level flag/score is independently shippable and valuable on its own. Treat as a follow-on phase or explicit stretch goal.
- **Sensitivity presets and "best for" descriptions both depend on having an expanded algorithm library** — sequence algorithm expansion before or alongside the chooser UX work, not after.
- **The SPA rebuild is a delivery mechanism, not a blocking dependency** — the underlying preset-mapping, area metadata, and algorithm descriptions can be built and tested independently of the frontend framework choice, then wired into the SPA. Don't let SPA-rebuild timeline gate the backend UX-support work.

---

## MVP Definition (for this milestone, not the whole product)

### Launch With (v4.0 core)

- [ ] Batch-first InfluxDB resampling (time-alignment on common grid) — hard prerequisite for everything else in this milestone
- [ ] Peer-divergence group detection, per-member binary_sensor + score output (Option B above) — the headline feature and canonical example (tire pressures)
- [ ] Joint-multivariate group detection, single group-level binary_sensor + score output — the second headline feature (humidity+temp → leak)
- [ ] Group membership config UI (explicit, operator-defined — no auto-discovery)
- [ ] Sensitivity preset (Low/Med/High) + Advanced toggle, applied to at least the new group detectors (existing univariate detectors can adopt it in the same pass if low-cost)
- [ ] Friendly-name search across the sensor list
- [ ] Area/domain categorization of the sensor browse list

### Add After Validation (v4.x)

- [ ] "Best for…" descriptions per algorithm — cheap, but sequence after the algorithm library itself stabilizes so copy doesn't need rewriting
- [ ] Guided "what are you monitoring?" chooser — validate that operators actually want hand-holding before building the flow (this developer is self-directed per profile; may prefer direct selection with good descriptions over a wizard)
- [ ] Per-member attribution for joint-multivariate anomalies (which sensor drove the joint flag) — real value, real complexity; prove the base joint-multivariate output first

### Future Consideration (post-v4.0)

- [ ] Streaming groups (already explicitly deferred in PROJECT.md, pending batch model validation)
- [ ] Sensitivity preset normalized scale extended across every future algorithm added — defer generalized framework until 2-3 more algorithms exist and the pattern is proven, not designed speculatively now

---

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---------|------------|----------------------|----------|
| Batch resampling (time-alignment) | HIGH | MEDIUM | P1 |
| Peer-divergence detection (per-member output) | HIGH | MEDIUM | P1 |
| Joint-multivariate detection (group-level output) | HIGH | MEDIUM | P1 |
| Group config UI | HIGH | LOW–MEDIUM | P1 |
| Sensitivity preset + Advanced toggle | HIGH | LOW–MEDIUM | P1 |
| Friendly-name search | MEDIUM–HIGH | LOW–MEDIUM | P1 |
| Area/domain categorization | MEDIUM | LOW–MEDIUM | P1 |
| "Best for..." descriptions | MEDIUM | LOW | P2 |
| Guided "what are you monitoring?" chooser | MEDIUM | MEDIUM | P2 |
| Per-member attribution for joint-multivariate | MEDIUM–HIGH | HIGH | P2/P3 |
| Streaming groups | MEDIUM | HIGH | P3 (already deferred) |

**Priority key:**
- P1: Must have for v4.0 launch
- P2: Should have, add when possible within v4.0 or immediately after
- P3: Nice to have, explicitly deferred

---

## Competitor / Reference Feature Analysis

No direct competitor is a self-hosted single-operator HA anomaly add-on; the closest references are (a) commercial observability anomaly tools for UX patterns, and (b) HA's own entity-picker for search/browse patterns.

| Feature | Datadog / New Relic / Dynatrace | Azure AI Anomaly Detector / Elastic ML | HA native entity picker | Argus v4.0 Approach |
|---------|----------------------------------|------------------------------------------|--------------------------|----------------------|
| Sensitivity control | Slider mapped to internal threshold; some offer named modes (Basic/Agile/Robust in Datadog) | Auto-selects "best" algorithm from a gallery, less operator control | N/A | Low/Med/High preset + Advanced toggle — more transparent than full auto-select, simpler than a raw slider |
| Algorithm guidance | Minimal — mostly automatic, little exposed rationale | Wizard-driven job creation (pick index/fields/function), one-click jobs for common patterns | N/A | "Best for..." descriptions + optional guided chooser that shows AND explains its pick, never hides it |
| Entity/metric search | Standard text search, tag-based filtering | Field/index picker | Fuzzy search across entity_id, friendly name, integration, device; filter by area/domain | Mirror HA's own pattern directly — it's already the tool this add-on lives inside |
| Group/multi-metric anomaly | "Multi-metric" / correlated-metric monitors exist in most APM tools, output is typically a single incident with contributing-metric breakdown | Multivariate anomaly detection APIs return one anomaly score per multivariate model + optional per-variable contribution scores | N/A | Two distinct output shapes as designed above: per-member for peer-divergence, single group-level for joint-multivariate — deliberately not copying APM's "one incident, contributing signals" blend, because HA's entity model rewards per-entity granularity where it's semantically correct |

## Sources

- [Home Assistant Entity filter card](https://www.home-assistant.io/dashboards/entity-filter/) — MEDIUM confidence (official docs)
- [Home Assistant Selectors docs](https://www.home-assistant.io/docs/blueprint/selectors/) — MEDIUM confidence (official docs)
- [Home Assistant Community — filtering entities by area](https://community.home-assistant.io/t/efficiently-filtering-area-entities/796356) — LOW confidence (community discussion)
- [Anomaly detection based on profile history and peer history (patent)](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/9166993) — LOW confidence (peer-deviation weighted-stddev approach)
- [Correlation-Based Anomaly Detection Method for Multi-sensor System (PMC)](https://pmc.ncbi.nlm.nih.gov/articles/PMC9173954/) — LOW confidence (faulty sensor shows abnormal distance to all peers)
- [Online Multivariate Anomaly Detection and Localization for High-Dimensional Settings (PMC)](https://pmc.ncbi.nlm.nih.gov/articles/PMC9656001/) — LOW confidence (Hotelling's T² approach)
- [Shapley Values of Reconstruction Errors of PCA for Explaining Anomaly Detection (arXiv)](https://arxiv.org/pdf/1909.03495) — LOW confidence (per-feature attribution technique)
- [Anomaly Detection made easy with PyOD (Medium)](https://medium.com/data-reply-it-datatech/anomaly-detection-made-easy-with-pyod-960faf6da4e5) — LOW confidence, but consistent with project's existing PyOD/D10 usage
- [Datadog Anomaly Monitor docs](https://docs.datadoghq.com/monitors/types/anomaly/) — MEDIUM confidence (official product docs, sensitivity/algorithm mode pattern)
- [New Relic anomaly detection docs](https://docs.newrelic.com/docs/alerts/create-alert/set-thresholds/anomaly-detection/) — MEDIUM confidence (official docs, sensitivity slider)
- [Azure AI Anomaly Detector](https://azure.microsoft.com/en-us/products/ai-services/ai-anomaly-detector) — MEDIUM confidence (official product page, auto-algorithm-selection pattern)

**Confidence caveat:** Most ML-technique sources here are LOW confidence per the classify-confidence tier (general web search, not vendor/official docs or verified library documentation). The HA-specific and commercial-vendor-docs sources are MEDIUM. Treat the *shape* of these findings (per-member vs group-level output, preset+advanced pattern, guided-but-transparent chooser) as solid — these patterns are consistent and unsurprising across every source checked — but treat specific algorithm names (Hotelling's T², specific PyOD detectors) as a starting point for the STACK research, not a final decision.

---
*Feature research for: Argus v4.0 — Group & Multivariate Anomaly Detection + UX*
*Researched: 2026-07-02*
