# Stack Research — Argus v4.0: Group & Multivariate Detection + Light-SPA UI

**Domain:** New multivariate/group anomaly-detection additions to an existing .NET 8 + Python gRPC detector; UI rebuild from server-rendered htmx to a built SPA.
**Researched:** 2026-07-02
**Confidence:** MEDIUM overall — PyOD/InfluxDB facts cross-checked against official docs and the repo's own pinned dependencies (HIGH); frontend bundle-size figures are LOW-confidence web-search snapshots (directional, re-verify at implementation time).

**Supersedes:** `.planning/research/STACK.md` (v3.0, 2026-06-30) for UI concerns only. v3.0's "Decision B — server-rendered HTMX vs SPA" recommended htmx specifically **because** the config UI was simple. PROJECT.md's v4.0 milestone explicitly overrides that decision: the algorithm chooser + friendly-name search UX needs client-side interactivity htmx can't cleanly provide, so v4.0 intentionally reintroduces a Node build step and drops the air-gapped/no-build guarantee. Everything else in the v3.0 file (ASP.NET Core hosting model, HA Ingress header handling, Kestrel co-hosting) is unchanged infrastructure and still applies — this file only covers what's NEW.

This file covers only new v4.0 additions. River, Darts, joblib, NetDaemon.Client, MQTTnet, Grpc.Net.Client/Tools remain unchanged and out of scope.

## Recommended Stack

### Core Technologies (new for v4.0)

| Technology | Version | Purpose | Why Recommended |
|------------|---------|---------|-----------------|
| PyOD (existing dependency, new usage) | 3.6.0 — already pinned in `detector/requirements.txt`, verified in repo | Joint-multivariate group detection: ECOD, COPOD, PCA, IForest | Same library already shipped for univariate MAD detection. All four multivariate detectors share PyOD's unified `fit(X)` / `decision_function(X)` / `predict(X)` API where `X` is a 2D array `(n_samples, n_features)` — no new dependency, no new API surface to learn; feed a matrix (one column per group member) instead of a vector. All four are CPU-only. License BSD-2-Clause (already an approved dependency). No `pyod` version bump needed — 3.6.0 already includes these classes. |
| Preact | 10.x (current 10.29.3, MIT) | Light SPA UI runtime | Recommended framework — see rationale below. |
| Vite | 8.x (current 8.1.2, MIT) | SPA build tool | Standard bundler for Preact; compiles to a static `dist/` of plain JS/CSS/HTML with zero runtime dependency on Node or a CDN. Node is build-time only — never present in the shipped container. |

### Supporting Libraries

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| numpy / scipy / scikit-learn / numba | transitive via `pyod==3.6.0` (already resolved by pip today) | Backing math for ECOD/COPOD/PCA/IForest | No explicit pin needed beyond what `pyod==3.6.0` already resolves in the existing `requirements.txt`. All BSD-licensed (numpy BSD-3, scipy BSD-3, scikit-learn BSD-3, numba BSD-2). No GPU extras (`torch`, `xgboost`) pulled in — those only apply to PyOD's deep-learning detectors, which v4.0 does not use. |
| (no new library — custom function) | — | Peer-divergence detection (which group member diverges) | Per-timestamp: compute group consensus (mean/median across members), then per-member deviation (z-score or MAD-based robust z-score) from that consensus, flag member(s) over threshold. Pure numpy arithmetic — reuse the same MAD math already implemented for the existing single-sensor MAD detector, applied against the cross-sectional group statistic instead of a member's own history. No PyOD class models "who diverged from peers" directly; this is a ~20-line function, not an import. |
| Flux (InfluxDB 2.x query language — not a package) | InfluxDB 2.x server-side, already deployed | Time-alignment of group members onto a common grid | `aggregateWindow(every, fn, createEmpty: true)` downsamples each member's series onto fixed windows; chained with `pivot(rowKey: ["_time"], columnKey: ["_field","entity_id"], valueColumn: "_value")` produces one row per aligned timestamp with one column per group member. Server-side (InfluxDB does the resampling) — no new NuGet package. |

### Development Tools

| Tool | Purpose | Notes |
|------|---------|-------|
| npm (or pnpm) | Installs Preact/Vite build toolchain, runs `vite build` | Build-time only, in a Docker multi-stage build stage or on the dev machine before `docker build` — never present in the final runtime image. Mirrors the existing pattern where `dotnet publish` runs before `docker build`, and `argus/Dockerfile` just `COPY`s `orchestrator/publish/`. |

## Installation

```bash
# Detector (Python) — NO new packages. ECOD/COPOD/PCA/IForest already ship inside
# pyod==3.6.0, which is already pinned in detector/requirements.txt. No edits needed there.

# SPA build (new, separate toolchain from detector/orchestrator)
npm create vite@latest argus-ui -- --template preact
cd argus-ui
npm install
npm run build   # outputs to dist/ (configure outDir to feed the orchestrator's wwwroot/)
```

```csharp
// InfluxDB.Client 5.0.0 (.NET) — already a dependency, new query pattern only.
// Build the aggregateWindow + pivot Flux query as a string, hand it to the existing QueryApi.
var flux = $@"
from(bucket: ""{bucket}"")
  |> range(start: {start})
  |> filter(fn: (r) => r._measurement == ""{measurement}"" and ({entityFilter}))
  |> aggregateWindow(every: {window}, fn: mean, createEmpty: true)
  |> pivot(rowKey: [""_time""], columnKey: [""_field"", ""entity_id""], valueColumn: ""_value"")";
```

```dockerfile
# argus/Dockerfile — new multi-stage build addition (illustrative)
FROM node:22-slim AS ui-build
WORKDIR /ui
COPY ui/package*.json ./
RUN npm ci
COPY ui/ ./
RUN npm run build   # outputs to /ui/dist

FROM ${BUILD_FROM}
# ... existing .NET + Python install steps unchanged ...
COPY --from=ui-build /ui/dist /opt/argus/orchestrator/wwwroot
# Node/npm never appear in this final stage — only the compiled static output does.
```

## Alternatives Considered

| Recommended | Alternative | When to Use Alternative |
|-------------|-------------|--------------------------|
| PyOD ECOD/COPOD/PCA/IForest (same lib, multivariate mode) | PyOD `KNN`, `LOF`, `OCSVM` | Also accept multivariate `X`, already BSD, already in the same `pyod` install — good candidates to expose later in the "expanded algorithm library" chooser (a separate v4.0 target feature), additive to ECOD/COPOD/PCA/IForest rather than a replacement for the MVP. |
| Custom z-score/MAD peer-divergence function | PyOD `HBOS` scored per-timestamp across members | If peer-divergence needs to handle non-Gaussian group distributions robustly, HBOS (already BSD, already in `pyod`) can score "how unusual is this member's value among its peers right now" without custom math. For v1 of this feature, a manual z-score/MAD comparison is simpler and matches the existing MAD-detector code style already in the repo. |
| Preact | Vue 3 (MIT) | If the UI grows into a more complex, multi-view stateful app needing built-in routing/state conventions (SFCs, Pinia), Vue's heavier footprint (~22KB) and stronger conventions pay off. Not justified for a config/chooser/search UI. |
| Preact | Svelte (MIT) | If raw bundle size is the only criterion, Svelte edges out Preact at small scale (compiles away the framework at build time, roughly 2-7KB for a small app vs Preact's ~3-4KB runtime) — but Svelte is a different authoring paradigm (compiler-driven, no runtime vdom, its own template syntax) with less "drop-in React-like" familiarity. Preact is recommended here because its React-compatible API (JSX, hooks) keeps the learning curve low for a developer who has only built server-rendered htmx UI so far (v3.0). If already comfortable with Svelte, it's an equally valid MIT choice with a marginal size edge. |
| Server-side Flux `aggregateWindow` + `pivot` | .NET-side resampling (manual bucket-and-average over raw points) | Only if resampling logic needs to be decoupled from InfluxDB entirely (e.g., for the deferred streaming-groups feature, where there's no Influx behind the window). For v4.0's stated batch-first scope, server-side Flux is strictly simpler — no new .NET resampling code, no risk of window-boundary mismatch between what Influx stored and what .NET recomputes. |

## What NOT to Use

| Avoid | Why | Use Instead |
|-------|-----|--------------|
| PyOD deep-learning detectors (AutoEncoder, VAE, DeepSVDD) for v4.0 groups | Pull in `torch` as an optional extra — GPU-oriented, large image footprint, conflicts with the CPU-only constraint carried into v4.0's batch-first scope; overkill for small (2-10 member) sensor groups | ECOD, COPOD, PCA, IForest — classical, CPU-only, already covered by `pyod==3.6.0` |
| ADTK, or any MPL-2.0-licensed library, for peer-divergence math | License constraint (BSD/Apache/MIT only) unchanged from v1-v3 | Plain numpy/scipy z-score/MAD computation (no new dependency) |
| A dedicated "group anomaly detection" / distributional-divergence package | Those target "is this whole group's distribution unusual vs. a reference," a different problem from "which single member diverges from its peers right now." No mature, permissively-licensed, actively maintained package fits the peer-divergence framing; adding one would import a dependency to solve what is a ~20-line function | Custom statistical computation (see Supporting Libraries) |
| .NET-side custom resampling library/NuGet package | InfluxDB's own `aggregateWindow`/`pivot` already solves alignment server-side; a second resampling implementation risks producing windows that don't match what's stored, and adds an unneeded dependency | Flux query string via the existing `QueryApi.QueryAsync` |
| React for the SPA | Larger baseline bundle (~42-45KB per current comparisons) than Preact for equivalent functionality, and its ecosystem gravitationally pulls in more packages (react-router, state libraries) that add further build surface and image size than a config/chooser UI needs | Preact — same JSX/hooks API, smaller footprint |
| Loading Preact/Vite output (or fonts, icon sets, any JS) from a public CDN at runtime | Violates "no cloud / self-hosted only" (D9) and the air-gapped LAN operation the add-on has run under since v1; a CDN reference breaks for any operator without internet-reachable HA and silently reintroduces an external runtime dependency | Bake the Vite `dist/` build output into the Docker image at build time — same `COPY orchestrator/publish/` idiom the orchestrator itself already uses; the runtime container makes zero outbound calls for UI assets |
| Installing Node/npm inside the final HA add-on image | Bloats image size and attack surface for a tool only needed to *produce* static files, never to *run* them; also not present in the current base image (`ghcr.io/home-assistant/base-debian:bookworm`) | Multi-stage Docker build: a `node:xx` stage runs `npm run build`, then `COPY --from=ui-build /ui/dist ./wwwroot` copies only the compiled static output into the final stage — mirrors how `orchestrator/publish/` is already built externally and just `COPY`'d in today |
| htmx (v3.0's choice) for the v4.0 chooser/search UI | v3.0's STACK.md correctly recommended htmx for a *simple config form* — that rationale doesn't hold once the UI needs a stateful algorithm chooser with live "best for" descriptions and fuzzy friendly-name search, which is much more naturally expressed as component state than server round-trips per keystroke | Preact SPA (this file) |

## Stack Patterns by Variant

**If group size stays small (2-10 members — typical HA groups like "all room humidity sensors" or "4 tire pressures"):**
- ECOD and COPOD (parameter-light, distribution-based, no training-set-size sensitivity) are the safest joint-multivariate defaults.
- PCA needs `n_features < n_samples` in the fit window to stay numerically stable — worth a guard if a group ever has very few historical samples in the batch window.

**If the algorithm chooser needs more "best for X" variety later (post-MVP):**
- IForest handles nonlinear/non-Gaussian joint relationships better than PCA/COPOD but is more expensive to refit repeatedly; a reasonable "Advanced" option, not a default preset.

**If peer-divergence groups mix units/scales per member (e.g., temperature °C and humidity %):**
- Normalize (z-score) each member's own history first, then compare normalized deviations across members — otherwise a member with naturally larger absolute variance always looks "most different" even when behaving normally.

**If the SPA later needs client-side routing beyond a single page (separate chooser/search/dashboard views):**
- Preact has a small official router (`preact-router`, MIT) that stays consistent with the "light SPA" framing — avoid a heavier meta-framework (Next/Nuxt-style) that assumes server-rendering infrastructure this project doesn't want.

## Version Compatibility

| Package A | Compatible With | Notes |
|-----------|------------------|-------|
| `pyod==3.6.0` | Python 3.11 (add-on base image, per `argus/Dockerfile`) and Python 3.12 (standalone `deploy/` image) | PyOD 3.6.x officially supports Python 3.9-3.13 — both images already in use are covered; no version bump needed for multivariate work. |
| InfluxDB.Client 5.0.0 (.NET) | InfluxDB OSS 2.x (already deployed) | `aggregateWindow`/`pivot` are core Flux stdlib functions, not client-library features — any Flux string works through the existing `QueryApi.QueryAsync`; no client upgrade needed. |
| Vite 8.x | Node >=20 | Build-stage/dev-machine requirement only; does not affect the runtime image's Python/.NET version requirements. |
| Preact 10.x | Vite 8.x via `@preact/preset-vite` | Standard, actively maintained combination; JSX handled by the preset without a separate Babel config. |

## Sources

- [PyOD official docs](https://pyod.readthedocs.io/) — confirmed ECOD/COPOD/PCA/IForest classes, unified fit/decision_function/predict API, `contamination` param — MEDIUM confidence (official docs, cross-checked across versions 2.0.7-3.6.1)
- [PyOD GitHub repo — LICENSE](https://github.com/yzhao062/pyod/blob/master/LICENSE) — BSD-2-Clause confirmed directly — HIGH confidence (primary source fetched)
- [PyOD on PyPI](https://pypi.org/project/pyod/) — 3.6.1 latest upstream (project pins 3.6.0, already verified installed in repo's `detector/requirements.txt`); Python 3.9-3.13 support; CPU-only core detectors confirmed; numpy/scipy/scikit-learn/numba deps (all BSD) — MEDIUM confidence
- [InfluxDB Flux `aggregateWindow()` docs](https://docs.influxdata.com/flux/v0/stdlib/universe/aggregatewindow/) — window/resample semantics — MEDIUM confidence (official InfluxData docs)
- [InfluxDB v2 window-aggregate guide](https://docs.influxdata.com/influxdb/v2/query-data/flux/window-aggregate/) — `aggregateWindow` + `pivot` pattern for multi-series alignment — MEDIUM confidence
- [InfluxDB.Client C# GitHub / NuGet](https://github.com/influxdata/influxdb-client-csharp) — `QueryApi.QueryAsync` streaming Flux query pattern confirmed for the 5.0.0 line — LOW-MEDIUM confidence (web-search summary, not directly fetched changelog)
- [Preact GitHub / npm](https://github.com/preactjs/preact) — 10.29.3 current, MIT license, ~3KB gzipped — LOW confidence (web-search snapshot; re-verify exact version at implementation time)
- [Vite releases](https://vite.dev/releases) — 8.1.2 current, MIT — LOW confidence (web-search snapshot)
- Bundle-size comparison sources (WeBridge, StackShare, Sentry Engineering blog) — Preact ~3-4KB, Svelte ~2-7KB, Vue ~22KB, React ~42-45KB gzipped for comparable small apps — LOW confidence (secondary blog sources, directional only, not benchmarked against this project's actual UI)
- [ASP.NET Core static files docs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files?view=aspnetcore-8.0) — `UseStaticFiles` + `MapFallbackToFile` pattern for serving a built SPA from Kestrel — MEDIUM confidence (official Microsoft docs)
- Repo files read directly: `detector/requirements.txt` (confirms `pyod==3.6.0` pinned today), `argus/Dockerfile` (confirms orchestrator is published externally and just `COPY`'d — the pattern this file recommends reusing for the SPA's `dist/` output) — HIGH confidence (primary source, read directly)
- `.planning/research/STACK.md` (v3.0, 2026-06-30) — prior htmx decision and ASP.NET Core/Ingress hosting facts, superseded for UI framework choice only — HIGH confidence (internal, previously verified)

---
*Stack research for: Argus v4.0 — group/multivariate anomaly detection + light-SPA UI*
*Researched: 2026-07-02*
