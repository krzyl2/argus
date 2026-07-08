# Phase 12: Sensors Screen Rebuild - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-08
**Phase:** 12-Sensors Screen Rebuild
**Areas discussed:** Detector-type picker, Row interaction model, Area/domain browse, Component adoption depth

---

## Detector-type picker (SC3)

| Option | Description | Selected |
|--------|-------------|----------|
| Keep `<Select>` dropdown | Current hst/mad/stl dropdown | |
| Radio-card via `AlgorithmCard` | Reuse Phase 10 AlgorithmCard, 2px accent selection, timing caption | ✓ |
| Radio-card via `SensitivityPreset` | Alternate shared radio-card component | |
| New bespoke detector card | Purpose-built card | |

**User's choice:** Radio-card via `AlgorithmCard` (all recommendations approved).
**Notes:** SC3 mandates a radio-card with 2px accent-border selection; per-detector model preserved (multiple detectors per sensor, each with its own type picker).

---

## Row interaction model

| Option | Description | Selected |
|--------|-------------|----------|
| Per-row independent disclosures | Current: every tracked row has its own `<details>` open simultaneously | |
| Single-select-and-expand | DS reference: click row → highlight (accent-soft) → only selected+tracked shows detector editor | ✓ |

**User's choice:** Single-select-and-expand (DS reference model).
**Notes:** Matches "single-sensor detector assignment" framing; more readable at 400+ entities. Selection is local UI state, not persisted.

---

## Area/domain browse

| Option | Description | Selected |
|--------|-------------|----------|
| Flat filtered list | Per DS reference kit mockup | |
| Enable `groupByArea` sections | Existing (unused) SRCH-02 grouped sections + domain fallback | ✓ |

**User's choice:** Enable `groupByArea`.
**Notes:** Roadmap SC1 requires "area/domain browse"; code already supports it (flag flip). DS kit's flat list is a mockup simplification — conflict resolved in favor of SC1 (Rule 7).

---

## Component adoption depth

| Option | Description | Selected |
|--------|-------------|----------|
| Restyle bespoke markup only | Keep raw `<input>`/`argus-pill`, apply DS styling via CSS | |
| Full adoption of Phase 10 primitives | Card list, Badge, shared Input (label+error) — refactor markup | ✓ |

**User's choice:** Full adoption.
**Notes:** Preserve unchanged: `detectorParams.ts` logic/messages, `entityIdx` sort correlation, `state/sensors.ts` save flow.

---

## Claude's Discretion

- Exact grid/spacing/typography per Phase 10 library + `ui_kits/admin/Sensors.jsx` visual reference.
- Param-grid column layout (2-col retained vs DS `1fr 1fr`) — styling only; field set/order/defaults/validation unchanged.

## Deferred Ideas

- StatusDot per sensor (no health/availability signal in `SensorEntry`).
- Backend single-sensor detector catalog endpoint (`singleCatalog` — already deferred in Phase 11).
- Sensitivity presets (Low/Med/High) for single-sensor detectors (no data source today).
