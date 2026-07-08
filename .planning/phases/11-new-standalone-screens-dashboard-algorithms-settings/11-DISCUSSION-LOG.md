# Phase 11: New Standalone Screens (Dashboard, Algorithms, Settings) - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-08
**Phase:** 11-New Standalone Screens (Dashboard, Algorithms, Settings)
**Areas discussed:** Dashboard (real vs mock split), Settings (scope & editability)
**Resolved at Claude's discretion (not selected):** Algorithms (read-only vs editable), Landing route + nav

---

## Area selection

| Option | Description | Selected |
|--------|-------------|----------|
| Algorithms: read-only vs editable | ROADMAP↔ui_kit conflict (read-only group catalog vs editable + single-sensor) | |
| Dashboard: real vs mock split | Which KPIs/sections use real APIs vs explicit mock+TODO | ✓ |
| Settings: scope & editability | Display-only vs editable; new backend needed? | ✓ |
| Landing route + nav | Dashboard as default vs keep /sensors; enable placeholders | |

---

## Dashboard — real vs mock split

### KPI tiles
| Option | Description | Selected |
|--------|-------------|----------|
| Real where possible, HA=mock+TODO | 3 tiles from real APIs; HA tile explicit mock+TODO; no backend | ✓ |
| All real (+HA-status endpoint) | Build minimal HA-connection endpoint; scope risk | |
| All mock+TODO | Simplest but wastes 3 trivially-real values | |

### Recent anomalies section
| Option | Description | Selected |
|--------|-------------|----------|
| All mock+TODO | Realistic mocks + explicit TODO banner; matches success criteria | ✓ |
| Real aggregate of group verdicts | Build from `/api/groups/{id}/status`; groups only, no history, N+1 | |
| Hybrid | Real group verdicts + explicit single-sensor mock | |

### System health section
| Option | Description | Selected |
|--------|-------------|----------|
| Mock+TODO | Mocked HA/detector/MQTT indicators + explicit TODO; no backend | ✓ |
| Minimal /api/health endpoint | Real reachability signals; scope risk | |

**User's choice:** Dashboard is frontend-only — real KPI data where an existing API already
supplies it, everything else explicit mock+TODO.
**Notes:** No new backend for Dashboard. Mock regions must be visibly marked TODO.

---

## Settings — scope & editability

### Backend need
| Option | Description | Selected |
|--------|-------------|----------|
| Zero backend: values mock+TODO | Config fields read-only mock; only theme functional | |
| Read-only + new GET /api/settings | New read endpoint (env/ConnectionSettings), no write | ✓ |
| Editable + POST /api/settings | Full edit+persist; conflicts with Supervisor env-var model | |

### Sections
| Option | Description | Selected |
|--------|-------------|----------|
| All 3 per kit | Connections + Batch&detection read-only, Appearance functional | ✓ |
| Appearance only | Minimal; theme toggle only | |
| Appearance + Connections | Theme + read-only Connections | |

### Theme control
| Option | Description | Selected |
|--------|-------------|----------|
| No logic change: Light/Dark shared with Sidebar | Second control bound to same localStorage; no 'System' | ✓ |
| Add 'System' option | System/Light/Dark; extends Phase 10 bootstrap; more work | |

**User's choice:** Read-only Settings backed by a new `GET /api/settings`, all 3 kit sections,
theme control reusing Phase 10's shared Light/Dark state.
**Notes:** Deliberate scope expansion (one read endpoint). Secret redaction required — no
tokens/passwords in the response. Endpoint follows existing `IsAuthorizedRequest` guard.

---

## Claude's Discretion

- **Algorithms** — resolved per ROADMAP: read-only browse of the 5 group detectors only, sourced
  from `/api/detectors/catalog`. Rejected the reference ui_kit's editable/SaveBar behavior and its
  extra "Single sensors" section (backend has no such endpoint). Flagged as a conflict in CONTEXT.
- **Landing route + nav** — default stays `#/sensors`; enable the 3 disabled Sidebar placeholders
  and add hash-routes `#/dashboard`, `#/algorithms`, `#/settings` to the hand-rolled router.

## Deferred Ideas

- Editable Algorithms defaults with persistence (own phase).
- Single-sensor (hst/mad/stl) catalog browse — no backend endpoint (own phase).
- `GET /api/health` endpoint → would make Dashboard health/HA-tile real (own phase).
- `POST /api/settings` write/persistence — conflicts with env-var config model (own phase).

## Flagged inconsistency

- `ROADMAP.md` marks Phase 11 `[x] completed 2026-07-08` while STATE.md shows it `executing` and
  no plans/context exist. Checkbox is premature — correct to `[ ]`.
