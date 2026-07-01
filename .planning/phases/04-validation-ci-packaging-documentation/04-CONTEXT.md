# Phase 4: Validation, CI Packaging + Documentation - Context

**Gathered:** 2026-07-01
**Status:** Ready for planning

<domain>
## Phase Boundary

UI inputs are fully validated (server + client) with clear error messages before any save; the CI
multi-arch image build bundles UI assets and a gate keeps the image under 2 GB; a `FileSystemWatcher`
with a 300 ms debounce makes external edits to `/data/entities.yaml` reload exactly once; and DOCS.md
documents the full Ingress UI workflow. The add-on can be configured entirely via UI with zero manual YAML.

This is the final v3.0 phase — the hardening + packaging + docs layer on top of the working UI + reload
mechanism from Phases 1–3. Out of scope: any new feature UI; full `validate_session` remains the
interim Supervisor-IP auth from Phase 2 unless trivially completable.
</domain>

<decisions>
## Implementation Decisions

### Input Validation Rules & UX (UI-04)
- entity_id validation: HA-style regex `^[a-z0-9_]+\.[a-z0-9_]+$` (lowercase `domain.object_id`); reject others.
- Detector parameter validation — per-type numeric ranges, reject unknown detector names:
  - HST: `window`≥1, `n_trees`≥1, `high_threshold`/`low_threshold` in [0,1] with `low < high`,
    `min_consecutive`≥1, `frozen_window`≥1, `frozen_variance_threshold`≥0.
  - MAD: `threshold`>0, `window`≥1.
  - STL: `period`≥2, `threshold`>0.
  - Detector `name` must be one of `hst`/`mad`/`stl` (case-insensitive) — reject unknown.
- Client-side: inline per-field error highlight + message; the Save button is DISABLED while any field
  is invalid (SC2). Client validation mirrors the server rules.
- Server-side: validate BEFORE any write to `/data/entities.yaml`; on failure return the picker error
  fragment (htmx) with per-field messages + a banner; NO partial write. Server is the source of truth
  (client validation is a convenience mirror).

### FileSystemWatcher Reload (SC4)
- Add a `FileSystemWatcher` on the `/data` directory listening for `Renamed` events targeting
  `entities.yaml` (the atomic temp→final rename from `ConfigWriter`). A 300 ms timer-reset debounce
  coalesces rapid events so a single atomic write triggers EXACTLY ONE reload (PITFALLS Pitfall 11).
- External-edit reload path: Load + `EntitiesConfigLoader.Validate` + `ILiveEntitiesConfig.Swap` (the
  SAME path as UI save). An invalid external edit is logged and IGNORED (keep the current running config;
  never crash the pipeline).
- Interaction with UI save: the UI save also renames entities.yaml, so the watcher will fire; the 300 ms
  debounce coalesces it and `Swap` is idempotent, so a redundant watcher-triggered reload right after a
  UI save is harmless (no special suppression needed).
- Debounce mechanism: reset a timer on each event; fire the single reload after 300 ms of quiet.

### CI Packaging & DOCS (DOCS-02)
- wwwroot bundling: the Web SDK `dotnet publish` (already in `.github/workflows/build.yml`) emits
  `wwwroot/` into `orchestrator/publish/`, which `argus/Dockerfile` COPYs into the image. Confirm this and
  add a CI (or test) assertion that `wwwroot/js/htmx.min.js` and `wwwroot/css/argus.css` are present in the
  publish output.
- Image-size gate: REUSE the existing `<2 GB` per-arch gate already in `build.yml` (from v2.0); verify it
  still passes with the added UI assets (~65 KB — negligible). Do not add a second gate.
- DOCS.md: add an Ingress UI section per SC5 — how to open the UI, select entities, assign detectors,
  what "apply without restart" means INCLUDING the ~4-minute HST warm-up period (River HST window=250 at
  ~1 reading/s/entity), and how to recover a corrupted `/data/entities.yaml`.
- Warm-up disclosure: a short note near the UI reload banner AND a DOCS subsection.

### Claude's Discretion
- Exact validation error message wording; where the validation helper lives (a shared validator used by
  both the save endpoint and — mirrored — the client); the client-side validation implementation (small
  inline JS / htmx hooks) consistent with the air-gapped no-build constraint.
</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- `orchestrator/Argus.Orchestrator/Program.cs` — the save endpoint (`POST /api/sensors/save`) from
  Phases 2–3 is where server-side validation gates before `ConfigWriter.WriteAsync` + `Swap`.
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` — structural `Validate()` exists
  (non-empty, detectors present); UI-04 adds INPUT validation (entity_id format, param ranges, unknown
  detector) — a new validator, invoked before write and mirrored client-side.
- `orchestrator/Argus.Orchestrator/Config/HstParams.cs` (in EntitiesConfig.cs) — HST default/known params
  to derive HST ranges.
- `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` — atomic temp→rename write (the event the
  FileSystemWatcher keys on) with `SemaphoreSlim`.
- `orchestrator/Argus.Orchestrator/Config/LiveEntitiesConfig.cs` — `Swap()` is the reload trigger the
  watcher calls.
- `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` + `wwwroot/css/argus.css` — extend with
  error highlight states + disabled-Save; error banner already exists from Phase 2.
- `.github/workflows/build.yml` — multi-arch buildx + EXISTING `<2 GB` per-arch size gate (lines ~117+);
  `dotnet publish` step (lines ~31-32).
- `argus/Dockerfile` — COPYs `orchestrator/publish/` (line ~51) → includes wwwroot.
- `argus/DOCS.md` — 269 lines; add the Ingress UI section.

### Established Patterns
- Minimal API endpoints + ConfigWriter + HtmlEncode; volatile-swap singletons; LogEvents EventId
  constants; the reload/ConfigChanged path from Phase 3.

### Integration Points
- New validator invoked in the save handler (before write) + mirrored client-side.
- FileSystemWatcher registered in Program.cs / a hosted service, calling Load+Validate+Swap.
- CI assertion for wwwroot presence; DOCS.md update.
</code_context>

<specifics>
## Specific Ideas

- Keep client-side validation as minimal inline JS / htmx-friendly hooks (air-gapped, no build step).
- The end-to-end acceptance test (configure entirely via UI, zero manual YAML, v2.0 unaffected) is a
  live-HA item → deferred to the end-of-milestone UAT sweep.
</specifics>

<deferred>
## Deferred Ideas

- Full `validate_session` Ingress auth middleware (remains interim Supervisor-IP auth unless trivially
  completable within this phase).
- Any new feature UI beyond validation states.
</deferred>
