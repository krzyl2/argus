# Pitfalls Research

**Domain:** Adding group/multivariate anomaly detection + a light-SPA UI to an existing HA add-on (Argus v4.0)
**Researched:** 2026-07-02
**Confidence:** HIGH for codebase-grounded pitfalls (proto shape, MQTT discovery, config model, Ingress middleware — read directly from source); MEDIUM for general ML/time-series/SPA-ecosystem pitfalls (web-sourced, cross-checked across independent sources per the verification protocol).

This file is scoped to what v4.0 is *adding*. It assumes the existing v3.0 Ingress/htmx pitfalls (`X-Ingress-Path` middleware, Kestrel bind address, atomic config writes, `FileSystemWatcher` debounce) are already solved and live-verified in `.planning/research/PITFALLS.md`'s prior pass — do not re-solve those; this file covers what breaks when you build *on top* of that foundation.

---

## Critical Pitfalls

### Pitfall 1: Univariate Proto Forces an Awkward Multivariate Bolt-On

**What goes wrong:**
`proto/argus.proto` is single-series end to end: `Point { entity_id, value, timestamp }`, `Verdict { entity_id, score, ... }`, and every RPC (`ScoreStream`, `Fit`, `ScoreBatch`, `SaveModel`, `LoadModel`) takes one `entity_id` and one `repeated Point window`. If group detection is implemented by looping the existing `ScoreBatchRequest` once per group member and comparing verdicts in the .NET orchestrator, you get peer-divergence approximated post-hoc from independently-fit univariate models — not real multivariate detection (PyOD's `ECOD`/`COPOD`/`PCA`/HBOS multivariate detectors need a single feature matrix `X[n_samples, n_features]` fit jointly). This looks like it works (you get *a* score per member) but is statistically a different, weaker technique than what "joint multivariate" in the v4.0 goal actually means.

**Why it happens:**
The path of least resistance is to keep the existing gRPC surface unchanged and fake groups in the orchestrator by fanning out N `ScoreBatchRequest` calls and eyeballing the results. It compiles, it ships, and nobody notices the difference between "N independent univariate models compared after the fact" and "one multivariate model over the joint feature vector" until the joint-anomaly detection mode (e.g., humidity+temp+pressure jointly abnormal → leak) simply never fires because no component is individually anomalous.

**How to avoid:**
Extend the proto with a first-class multi-series message before writing any group detection code: a `GroupPoint`/`GroupWindow` message carrying `repeated string member_ids` + a matrix (or repeated per-member `Point` lists sharing a timestamp grid) and a `GroupVerdict` carrying both a joint anomaly score and a per-member attribution score. Keep the existing univariate messages untouched (additive proto change, not a breaking one) — old single-sensor `ScoreStream`/`ScoreBatch`/`Fit` callers must keep compiling and running unmodified.

**Warning signs:**
- Group "joint multivariate" mode never flags anything that peer-divergence mode doesn't already catch.
- Detector-side Python code has no import of a genuinely multivariate PyOD estimator (`pyod.models.pca`, `.ecod`, `.copod`, `.hbos`) fit on a 2D array — only loops calling the same univariate detector N times.
- Code review finds group scoring implemented entirely in the .NET orchestrator (comparing independently-computed univariate verdicts) with no new gRPC message type.

**Phase to address:**
Proto/detector-contract phase (first phase of v4.0). This is a foundational decision — retrofitting a real multivariate message type after peer-divergence ships on the fake approach means redoing the detector interface and the orchestrator's group-scoring call sites.

---

### Pitfall 2: Feature Scaling Ignored — Pressure Dominates Humidity/Temperature in Joint Scores

**What goes wrong:**
Argus's v1 entities mix units with wildly different numeric ranges: pressure (~950–1050 hPa), humidity (0–100 %RH), temperature (~-20 to 40 °C). Any joint multivariate detector (PyOD PCA, HBOS, ECOD, kNN, LOF) computes distances or densities over the raw feature vector. Without per-feature standardization, pressure's ~100-unit range and larger absolute magnitude will dominate the anomaly score; a genuine humidity spike (leak scenario) gets drowned out by normal day-to-day pressure drift. This is the single most common way a "joint humidity+pressure" detector silently reduces to "a pressure detector."

**Why it happens:**
PyOD's `fit()` API accepts a raw 2D array and returns scores without validating or requiring pre-scaled input — nothing errors out, so an unscaled group model looks like it "just works" and produces plausible-looking scores. The failure only surfaces as a subtle accuracy problem (wrong root cause, missed leaks) that's hard to catch without a labeled test case.

**How to avoid:**
Standardize every feature (z-score: `(x - rolling_mean) / rolling_std`, computed per-member from that member's own history, not a fixed global constant) before it enters any joint multivariate model. Store the per-member scaler alongside the group model (same lifecycle as `SaveModel`/`LoadModel` — the scaler is part of the model's persisted state, not recomputed ad hoc). Do this in the Python detector, not the .NET orchestrator, since scaling parameters are a model-fitting concern (D2: all ML in Python).

**Warning signs:**
- Joint anomaly scores correlate almost 1:1 with the pressure sensor's raw z-score and barely move with humidity/temperature swings.
- A synthetic test (spike humidity only, holding pressure/temp flat) fails to trigger the joint detector.
- No scaler object exists in the persisted group-model bytes; every `Fit` call recomputes normalization from scratch using the current window's mean/std (masks drift, breaks if the window is short).

**Phase to address:**
Detector implementation phase for joint multivariate mode. Add a scaling unit test (mixed-unit synthetic group, verify each feature contributes comparably to the score) as an explicit acceptance criterion before this phase is called done.

---

### Pitfall 3: Time-Alignment Treated as an Afterthought — Batch Groups Silently Compare Misaligned Readings

**What goes wrong:**
`InfluxDbReader.QueryAsync` today returns one entity's raw, irregularly-timestamped points sorted ascending — HA's `state_changed` events fire only on value change, so cadence varies per entity (a stable pressure sensor may report every 10 minutes; a twitchy humidity sensor every 30 seconds). For group detection, naively zipping two members' point lists by index (`points[i]` from sensor A paired with `points[i]` from sensor B) compares readings from different wall-clock times — a "joint anomaly" at index 47 may be comparing sensor A's value from 09:00 with sensor B's value from 09:47. This produces false positives (values look inconsistent because they were never simultaneous) and false negatives (the actual coincident anomaly falls between misaligned samples).

**Why it happens:**
The existing single-sensor code has never needed cross-entity alignment — each entity's window is self-contained. Group detection is the first place two independent, irregularly-sampled time series must be reconciled onto a shared axis, and it is easy to underestimate this because "just zip the lists" compiles and produces a result that looks reasonable on eyeball inspection with clean synthetic data.

**How to avoid:**
Resample every group member onto a common fixed-interval grid before joint scoring — the v4.0 scope already commits to this ("Batch-first: InfluxDB resampling for time-alignment"). Concretely: pick a grid interval (start conservative — e.g. 5 min, matching realistic sensor cadence) and use InfluxDB's own `aggregateWindow()` in the Flux query (mean or last-value per bucket) rather than pulling raw points and resampling in .NET/Python — InfluxDB is already the batch source and doing the resampling at the query layer avoids shipping raw irregular data across the wire twice. For gaps (a sensor didn't report in a bucket — HA only emits on state change), use last-value-carried-forward with an explicit staleness cap (e.g., don't carry forward more than 3 buckets / 15 min) rather than unlimited forward-fill, which would let a stale/dead sensor silently masquerade as "no change" indefinitely. Never blindly index-zip two independently-fetched point lists.

**Warning signs:**
- Group Flux query still filters by `entity_id` per member and returns raw (non-`aggregateWindow`) points that get zipped by list position in .NET/Python.
- No explicit gap/staleness handling — a group member that stops reporting (dead battery, HA restart) keeps contributing its last known value forever with no flag.
- Synthetic test with two sensors at deliberately different cadences (one every 30s, one every 10min) produces plausible-looking but wrong-timestamp-paired joint scores.

**Phase to address:**
Batch-groups phase (InfluxDB resampling phase, per v4.0 scope). The `aggregateWindow` + staleness-cap resampling should be a named, tested component — not inlined into the group scoring loop — since streaming groups later will need the same alignment logic.

---

### Pitfall 4: Unit Mismatch Within a "Group" Isn't Validated at Config Time

**What goes wrong:**
`EntityConfig.Groups` is currently a parsed-and-ignored `object?` placeholder with zero validation. Once activated, nothing stops an operator from grouping semantically incompatible sensors (e.g., outdoor temp °C with a battery-percentage sensor, or three temperature sensors where one reports °F due to a misconfigured integration). `HaStateDto` already carries `unit_of_measurement` (added in v3.0 for the sensor registry) — if group activation doesn't cross-check unit compatibility across a group's members using that existing data, the detector will silently compute nonsense joint statistics on incompatible units, and peer-divergence mode will flag every member of a mixed-unit group as "diverging" permanently.

**Why it happens:**
The `Groups`/`Covariates` fields were designed purely as YAML placeholders in v1–v3 (parsed to avoid schema errors, never read). There is no existing validation pathway for cross-entity semantic consistency because until now every config validation concern was per-entity, not per-group.

**How to avoid:**
When activating `Groups`, validate at config-load/UI-save time (not detector-fit time) that all members share `unit_of_measurement` (exact match, or an explicit allowed-conversion table if you want to support °C/°F mixing — simplest is to just reject mixed units). Surface this validation in the SPA UI as a hard error on group creation, not a silent skip. Reuse `IHaSensorRegistry`'s existing per-entity `unit_of_measurement` data — it's already fetched from HA and cached; this is a config-time cross-reference, not a new HA API call.

**Warning signs:**
- Group config schema/loader accepts a `Groups` list with no unit-compatibility check.
- A group containing a `%` humidity sensor and a `hPa` pressure sensor is accepted as a "peer-divergence" group (peer-divergence assumes members are comparable — pressure and humidity are never comparable peers, only valid as *joint* multivariate features, not as divergence peers).
- No test exercises a mixed-unit group and asserts rejection or an explicit warning.

**Phase to address:**
Config-model activation phase (where `Groups`/`Covariates` go from parsed-and-ignored to live). This is a cheap, purely-validation fix — much cheaper to add here than to debug "why does peer-divergence always fire" later.

---

### Pitfall 5: Small-N Groups Make Peer-Divergence Statistically Meaningless

**What goes wrong:**
Peer-divergence ("which member diverges from its group") implicitly assumes a group large enough that a robust "normal" consensus can be computed and one outlier doesn't skew it. Argus's own example in PROJECT.md — "one tire pressure rising unlike the others" — implies groups as small as N=2–4 (e.g., 4 tire sensors, or 2–3 sensors in one room). With N=2, "divergence from the group" is mathematically just "which of the two values is bigger" — there is no robust consensus to diverge from, and standard techniques (z-score against group mean/std, leave-one-out comparison) become unstable or degenerate at N≤3. Shipping peer-divergence without an explicit small-N floor produces a feature that behaves erratically for the most common real-world group sizes.

**Why it happens:**
Textbook peer-comparison/ensemble techniques (and most PyOD/River examples) are demonstrated on datasets with many features or many samples; the literature doesn't call out that N=2–4 groups need a fundamentally different (or at least floor-guarded) approach. It's easy to implement "z-score vs. group mean, flag outliers beyond threshold" and not notice it degenerates until a real 2-member group is configured.

**How to avoid:**
Set and document an explicit minimum group size for peer-divergence (e.g., require N≥3, ideally N≥4-5 for meaningful leave-one-out consensus) and use a robust statistic (median + MAD, not mean + stddev, since a single outlier in a small group otherwise corrupts the very consensus you're comparing against — reuse the existing MAD detector logic/params already in the codebase for single-sensor mode, D-10). For N=2, either refuse to run peer-divergence (surface as a UI validation warning: "peer-divergence needs at least 3 members") or fall back to a simple documented rule (e.g., flag if the pairwise difference exceeds a threshold — but label this explicitly as a degraded mode, not standard peer-divergence).

**Warning signs:**
- No minimum-group-size check anywhere in group config validation or detector code.
- Peer-divergence math uses mean/stddev instead of median/MAD (mean is not robust — the one anomalous member pulls the "consensus" toward itself).
- A 2-member group test always flags exactly one member as "the anomaly" even when both are behaving normally relative to their own history (there's no way to prove a real anomaly with N=2 alone).

**Phase to address:**
Peer-divergence detection phase. Add the minimum-N guard and median/MAD-based consensus as explicit, tested requirements — not left to the detector's default statistical choice.

---

### Pitfall 6: Peer-Divergence "Which Member" Attribution Presented as Certain When It's a Ranking

**What goes wrong:**
Attribution ("which sensor is the anomalous one") in any multi-member comparison is inherently a *ranking under uncertainty*, not a ground-truth fact — especially when features are correlated (e.g., two indoor temp sensors in adjacent rooms naturally move together; a real HVAC event could make *both* look like they diverge from a three-member group, and whichever crosses the threshold first "wins" attribution somewhat arbitrarily). If the MQTT/HA-facing binary_sensor for a group is named/worded as if it definitively identifies the faulty sensor ("Kitchen humidity is the anomaly"), users will over-trust it, especially with correlated members or the small-N problem from Pitfall 5.

**Why it happens:**
The single-sensor binary_sensor UX (D8: Polish friendly names, "is_anomaly" boolean) sets a precedent of confident, binary framing that doesn't map cleanly onto attribution, which is fundamentally probabilistic. Carrying the same UX pattern forward to groups without adjustment implies false certainty.

**How to avoid:**
Expose the attribution as a score/rank (already have the `Verdict.score` field pattern — reuse it: publish a per-member "divergence score" sensor, not just a single boolean "this one is the anomaly"), and word the HA entity attributes/name to reflect "most likely divergent member" rather than a flat assertion. Cover the correlated-features case explicitly in whatever documentation/UI copy accompanies groups: warn that tightly correlated members reduce attribution confidence, and (if feasible) surface a correlation warning at group-config time using the same historical data already pulled for Fit.

**Warning signs:**
- The only group-level MQTT output is a single `binary_sensor` naming one specific member with no accompanying confidence/score.
- Group members that are known to be physically correlated (e.g., 2 sensors in the same room) get "confidently" blamed one at a time across different events with no consistency, and nobody flags this as expected behavior of a ranking method.

**Phase to address:**
Peer-divergence detection phase (algorithm design) and the SPA UI's group-detail view (surfacing attribution as a ranked/scored list, not a single verdict).

---

## High-Risk Pitfalls

### Pitfall 7: Group MQTT Entity Churn When Group Membership Changes

**What goes wrong:**
Today, `UniqueId.AnomalyId`/`ScoreId` are deterministic functions of `(entity_id, detector)` — stable as long as config doesn't change, and v3.0 already built `DiscoveryPublisher.RetractAsync` + hot-reload diffing specifically for entity add/remove. Groups introduce a second dimension of churn: a group's `unique_id` will need to be some deterministic function of its *member set* (or a stable group name/ID). If group membership changes (operator adds/removes a member via the SPA), and the group's `unique_id` is derived from the member list (e.g., a hash or sorted concatenation of member entity_ids), then editing membership silently mints a *brand-new* `unique_id` — the old group entity is orphaned in HA (never retracted, because the reload-diffing logic only knows how to diff *entity* config, not *group* config) and a new "stale" group entity appears alongside it.

**Why it happens:**
The existing hot-reload/retraction machinery (`ILiveEntitiesConfig`, `ConfigChanged` diffing in `HaListenerWorker`) was built and tested against the mental model "entities are added/removed," which is a flat list diff. Groups add a second nested collection whose *identity* is ambiguous — is a group identified by a stable operator-assigned name (survives membership edits) or by its member set (churns on every edit)? If this isn't decided explicitly, whichever engineer implements it first will pick membership-derived IDs because it's the "obvious" deterministic choice, without realizing it breaks retraction semantics.

**How to avoid:**
Give every group a stable, operator-assigned (or UI-generated-once) `group_id` that is independent of its member list — analogous to how HA's own `device.identifiers` stays stable across entity changes. `unique_id` for group MQTT entities derives from `group_id` + detector, never from the member list. When membership changes, the same group entity is republished (retained MQTT overwrite, same topic) with updated `sw_version`/attributes if needed — no retract/recreate. Only retract a group's discovery topics when the *group itself* is deleted, exactly mirroring the existing per-entity retract path (`RetractAsync` already has the right shape — extend it to accept group configs, don't reinvent it).

**Warning signs:**
- Group `unique_id` generation code takes the member list (or its hash) as an input.
- Editing a group's members in the SPA causes a new HA device/entity to appear rather than the existing one updating.
- No `group_id` field exists anywhere in the (to-be-designed) group config schema — only a `members: [...]` list.

**Phase to address:**
Config-model + MQTT-discovery phase for groups (early — this is a schema decision, not an implementation detail to patch later). Verify with the same retraction test pattern used in v3.0 (T-03-01 style: change membership, assert old topic untouched, same topic re-published).

---

### Pitfall 8: Group Detection Waiting on the Slowest Member Blocks the Streaming Path

**What goes wrong:**
The Core Value's <2s latency target is explicitly scoped to single-sensor streaming in v4.0 ("group latency needs a separate, looser target — groups wait for member alignment"), which correctly anticipates the risk — but the risk is *implementation leakage*, not just a documented exception. If group scoring (even batch-only, per v4.0's batch-first scope) is wired into the *same* `ScoreStreamPipeline`/`HaListenerWorker` fan-out loop that serves single-sensor streaming — e.g., a shared channel, a shared gRPC client, or a shared per-tick loop — then a slow/blocked group computation (waiting on InfluxDB resampling, or a member whose Influx data hasn't landed yet) can back up that shared resource and degrade the untouched single-sensor path's actual latency, even though group detection is "just" batch and "separate" on paper.

**Why it happens:**
It is architecturally convenient to reuse `BatchSchedulerWorker`'s existing per-tick loop (`RunBatchAsync` iterating `_liveConfig.Get().Entities`) for group scoring too, since the batch infrastructure (InfluxDB reader, `IBatchDetectorClient`, timer) already exists. But `BatchSchedulerWorker` already fully owns the batch path and is decoupled from `ScoreStreamPipeline`/`HaListenerWorker` (the streaming path) — the risk is specifically if a shared/blocking resource (gRPC channel pool, InfluxDB client connection pool sized too small, or a single `DetectionGateway` health gate) is shared between the two paths and a slow group query exhausts it.

**How to avoid:**
Keep group batch scoring as its own scheduled loop (either a new `BackgroundService` or an extension of `BatchSchedulerWorker` that iterates groups *after* or in a separate cycle from per-entity batch scoring, with independent error isolation — the existing per-entity try/catch pattern in `RunBatchAsync` already isolates entity failures from each other; extend that same isolation to groups). Verify the gRPC channel used for group `ScoreBatch`/`Fit` calls doesn't share a bounded connection pool with the streaming path's `ScoreStream` calls in a way that lets one starve the other (check `Grpc.Net.Client` channel configuration — a shared `GrpcChannel` with default HTTP/2 multiplexing is fine; a shared bounded thread pool or semaphore gating both paths is not). Add a latency/health metric distinguishing "single-sensor streaming latency" from "group batch latency" so a regression in one is visible independent of the other.

**Warning signs:**
- Single-sensor streaming latency (already the Core Value's verified <2s metric) regresses after group batch scoring is added, even though group scoring is "batch, not streaming."
- Group scoring code lives inside `HaListenerWorker` or `ScoreStreamPipeline` rather than alongside `BatchSchedulerWorker`.
- A single shared `SemaphoreSlim` or bounded channel gates both per-entity streaming sends and group batch requests.

**Phase to address:**
Group batch-scoring implementation phase. Add a regression check (measure single-sensor streaming latency before/after group batch feature lands) as an explicit verification step, not just a functional test of groups themselves.

---

### Pitfall 9: Introducing a Node Build Step Breaks the Multi-Stage Image Discipline Established in v2.0/v3.0

**What goes wrong:**
The existing add-on Dockerfile already juggles a multi-arch (amd64+aarch64) build with a CI gate asserting compressed size < 2 GB and "torch-free." A Node.js SPA build step is a *second* language toolchain added to an already dense build pipeline (`.NET publish` + Python pip install + now `npm ci && npm run build`). The generic risk (Node build tools bloating the final image) is already documented in the existing v3.0 PITFALLS.md (Pitfall 6/Technical Debt table) for the htmx-era decision that ultimately *avoided* a Node step — v4.0 is now deliberately taking on the thing v3.0 avoided. The specific new risk for v4.0 is: since the project builds locally via `buildx` (not CI, per "Local buildx→GHCR release (not CI)" — a v3.0 Key Decision), an `npm ci` step running for aarch64 emulated under QEMU (matching the existing v2.0 GH Actions two-job QEMU pattern, if still used, or local buildx multi-arch) can be extremely slow or flaky, turning what was previously a fast local release process into a multi-minute-per-arch bottleneck, and any accidental inclusion of `node_modules`/dev dependencies in the final stage silently re-inflates the image past the existing 2 GB gate.

**Why it happens:**
The multi-stage pattern (`FROM node:20-slim AS ui-builder ... COPY --from=ui-builder /dist/ ...`) is well understood in principle (already documented in the existing PITFALLS.md as the correct approach), but is easy to get subtly wrong on the first real implementation: forgetting `--omit=dev` / running `npm ci` without `NODE_ENV=production`, or accidentally `COPY`-ing the entire `ui/` source tree (including `node_modules` if `.dockerignore` isn't updated) into the final stage instead of just the built `dist/` output.

**How to avoid:**
Reuse the exact multi-stage pattern already validated in the v3.0 research (builder stage on `node:20-slim`, `npm ci` there only, `COPY --from=ui-builder /ui/dist/ wwwroot/` into the final stage). Add `ui/node_modules` to `.dockerignore` defensively even though the builder stage handles it correctly (defense in depth against future Dockerfile edits). Keep the existing CI/local image-size assertion (`docker image inspect ... jq '.[0].Size'` < budget) as a release gate, but *revise the budget number* — v4.0 explicitly drops the old 2 GB target as a stated tradeoff ("the add-on image grows... dropped" per PROJECT.md), so pick and document a new explicit ceiling rather than silently letting the gate rot or get deleted. Time-box a test build for aarch64 early (before committing to a specific SPA framework) to catch QEMU-emulation slowness before it's baked into the release workflow.

**Warning signs:**
- Local `buildx` release process for a new version takes dramatically longer than the v3.0 baseline, specifically during the aarch64 leg.
- `docker history` on the shipped image shows an `npm` or `node_modules` layer larger than the built `dist/` output alone would justify.
- The old 2 GB CI gate either still exists unmodified (will start failing builds) or was silently deleted (no size regression protection at all) — both are wrong; the correct fix is a deliberate, documented new number.

**Phase to address:**
SPA build/deploy integration phase (whichever phase wires the chosen SPA framework into the Dockerfile). Re-baseline the image-size gate explicitly as part of this phase's acceptance criteria, don't let it be an incidental side effect.

---

### Pitfall 10: SPA Breaks the X-Ingress-Path Handling That htmx Deliberately Solved

**What goes wrong:**
The current server-rendered htmx UI (v3.0) solved Ingress's dynamic base-path problem cleanly because every page is rendered server-side with the `X-Ingress-Path` value already known to Kestrel (`PathBase` middleware) at render time — links, forms, and htmx `hx-get`/`hx-post` attributes can simply be relative or explicitly prefixed using the value read from the request. A client-side-routed SPA (React Router / Vue Router in "history" mode, or any bundler with a baked-in `base` config) typically has its asset base path and route base fixed at **build time**, not per-request — but HA Ingress's prefix (`/api/hassio_ingress/{token}/...`) is dynamic per session/install and cannot be known at build time. This is exactly the problem the v3.0 architecture avoided by staying server-rendered; the SPA migration reintroduces it from scratch.

**Why it happens:**
SPA tooling (Vite, CRA, Vue CLI) is designed around a single, mostly-static deployment base path (e.g., serving from `/` or a known subpath configured once in `vite.config.js`). It has no built-in concept of "the base path is different on every single page load, decided by an HTTP request header." Developers reach for a build-time `base: '/some/path'` config out of habit; it works in local dev (served at `/`) and breaks only when opened through the real HA Ingress panel — the same "works on direct port access, breaks in Ingress" failure signature the v3.0 research already caught for htmx, but harder to fix in an SPA because it's not just link hrefs, it's bundler-emitted asset URLs *and* the client router's internal state.

**How to avoid:**
Pick one of two known-working strategies, decided explicitly in the SPA framework/build phase rather than discovered by trial and error:
1. **Hash-based routing + relative asset paths.** Configure the SPA's router in hash mode (`/#/groups` instead of `/groups`) so the client-side router never depends on the server-visible path at all, and set the bundler's asset base to `./` (relative) so all JS/CSS/image URLs resolve relative to wherever `index.html` was actually served from (which Kestrel's existing `PathBase` + `UseStaticFiles()` already handles correctly, per the existing v3.0 middleware). This sidesteps the dynamic-base-path problem entirely — no runtime templating needed.
2. **Runtime index.html templating.** If hash routing is rejected for UX reasons, have Kestrel serve `index.html` through a small handler (not raw `UseStaticFiles()` for that one file) that injects the request's actual `X-Ingress-Path` value into a `<base href="...">` tag or a global JS variable (`window.__INGRESS_BASE__`) at request time, and configure the SPA's router/fetch calls to read that value instead of a build-time constant. This is strictly more complex (a second SPA-specific pitfall: the API client's `fetch()` base URL must also read the same runtime value, not a bundler env var).

Either way, verify by opening the SPA exclusively through "Open Web UI" in a real HA Supervisor (never direct port access) before considering the SPA phase done — this is the same verification discipline the existing PITFALLS.md already prescribes for htmx, and it applies unchanged.

**Warning signs:**
- SPA works when accessed via direct port (`http://addon-host:8099/`) but shows a blank page or broken assets/routes only through the HA Ingress panel.
- Bundler config (`vite.config.js` / `vue.config.js`) has a hardcoded `base: '/'` or any fixed non-relative path.
- Client-side router is in "history"/"browser" mode with no hash, and no runtime base-path injection exists anywhere in the served `index.html`.
- The SPA's API client constructs request URLs from an `import.meta.env.VITE_API_BASE` (build-time) rather than a runtime-read value.

**Phase to address:**
SPA scaffolding phase (before any feature UI is built on top of it) — this must be solved and verified against real Ingress before the algorithm-chooser or friendly-name-search UI work begins, exactly as the v3.0 research treated the equivalent htmx pitfall as Phase-1, blocking work.

---

### Pitfall 11: SPA API Calls Bypass or Duplicate the Existing Supervisor-IP Auth Check

**What goes wrong:**
The current `IsAuthorizedRequest` check in `Program.cs` gates server-rendered page routes (`GET /sensors`) by `RemoteIpAddress` (Supervisor IP or loopback). An SPA architecture typically restructures the backend into a set of JSON API endpoints consumed by client-side `fetch()`/`axios` calls, often introducing new endpoints (`GET /api/groups`, `POST /api/groups`, `GET /api/detectors/catalog`, etc.) for the algorithm chooser and friendly-name search. Each new endpoint must independently apply the same `IsAuthorizedRequest` gate — it is easy to add a new `app.MapGet`/`MapPost` for SPA data and forget the auth check that was previously applied uniformly to the (small) set of server-rendered routes, especially since minimal-API endpoint definitions don't share middleware by default unless deliberately grouped.

**Why it happens:**
The existing pattern applies `IsAuthorizedRequest(req.HttpContext)` as the first line inside each handler (seen in the `/sensors` handler) rather than as route-group middleware. This is fine for 2-3 routes but doesn't scale safely to the larger API surface an SPA needs (groups CRUD, detector catalog, search) — a new contributor adding "just one more small endpoint" for SPA data has no structural reminder to add the auth line, unlike a middleware-based approach which is automatically applied to every route in a group.

**How to avoid:**
Before adding the SPA's API surface, refactor the ad-hoc per-handler `IsAuthorizedRequest` calls into route-group middleware (`app.MapGroup("/api").AddEndpointFilter(...)` or a dedicated middleware applied to a `/api` prefix) so every current and future API endpoint is covered by construction, not by each handler author remembering to add a line. This is a good moment to also close out the "Phase 4 full validate_session" TODO already flagged in the `Program.cs` comment (`// Full validate_session cookie-based auth is scheduled for Phase 4`) if v4.0 is that phase — check whether that deferred work is now due.

**Warning signs:**
- A new `/api/...` endpoint for SPA data has no `IsAuthorizedRequest` call and no group-level filter covering it.
- `grep` for `IsAuthorizedRequest` shows it called inconsistently — some handlers have it, newer SPA-era ones don't.
- The `ARGUS_DEV_TRUST_ALL_REQUESTS` dev bypass flag is still checked per-handler rather than centrally, risking a handler that forgets the check entirely (bypass or not).

**Phase to address:**
SPA API-surface design phase (the phase that defines the JSON API contract for groups/detectors/search). Convert to group-level middleware as part of that phase, not retrofitted after several endpoints already exist ad hoc.

---

## Moderate Pitfalls

### Pitfall 12: `Groups`/`Covariates` Activation Silently Changes Behavior for Existing Configs

**What goes wrong:**
`EntitiesConfig.Covariates`/`Groups` are typed as `object?` today specifically so that any YAML shape can be parsed-and-ignored without breaking existing configs (the comment says exactly this: "Parsed but ignored in Phase 1"). The moment these fields become live, every existing `entities.yaml` on disk (written by the v3.0 UI, with these fields either absent or containing whatever ad hoc placeholder shape a user or `gen-entities.py` happened to write) must deserialize into whatever *typed* schema v4.0 introduces (e.g., `List<GroupConfig>` instead of `object?`). If the new type is stricter than the old `object?` catch-all, existing configs with a malformed or unexpected `groups:`/`covariates:` YAML block (even an empty `{}` versus expected `[]`) can fail to load entirely — regressing the exact "hard failure on startup with no entities configured" bug that v3.0's `EntitiesConfigLoader` softening was built to prevent.

**Why it happens:**
`object?` is forgiving by construction; any concrete replacement type is not. The people who wrote existing `entities.yaml` files (or `gen-entities.py`, if it ever emits placeholder `groups:`/`covariates:` keys) had no schema to conform to, so real-world files may contain inconsistent or absent representations of these fields.

**How to avoid:**
Treat this exactly like the v3.0-established pattern for schema evolution: keep the loader tolerant (`groups: null`/absent → empty list, not a load failure), add an explicit test loading a *real, pre-v4.0* `entities.yaml` sample (captured from the live-verified 2.0.9 bring-up) through the new loader before shipping, and keep `IgnoreUnmatchedProperties()`-style tolerance for the transition period rather than hard-failing on unexpected shapes. Since YamlDotNet schema drift was already flagged as a named risk in the existing PITFALLS.md (Pitfall 4c) for the UI/loader relationship generally, extend that same discipline explicitly to the `Groups`/`Covariates` activation rather than treating it as a fresh problem.

**Warning signs:**
- No test loads a config file captured from an actual pre-v4.0 installation (only newly hand-written fixtures with the new schema already in the expected shape).
- `EntitiesConfigLoader` throws (rather than defaulting to empty) when `groups:` is absent or `null` in an old file.
- The add-on regresses to the exact "crashes on boot" class of bug that the v3.0 `EntitiesConfigLoader` softening fixed — but for the new fields instead of the old `entities` list.

**Phase to address:**
Config-model activation phase. Add the real-file regression test as an explicit gate before merging the typed `Groups`/`Covariates` schema.

---

### Pitfall 13: Detector-Chooser "Presets" Encode Assumptions That Don't Hold for Groups

**What goes wrong:**
The v4.0 UX goal introduces "readable parameter presets (Sensitivity Low/Med/High)" as a simplification layer over raw detector parameters. This preset abstraction was almost certainly designed against single-sensor detectors (MAD, HST, STL) where "sensitivity" maps cleanly to one threshold knob. Multivariate/group detectors (PyOD PCA/ECOD/HBOS, or a peer-divergence MAD-variant) may have parameters that don't collapse into a single Low/Med/High sensitivity axis (e.g., PCA's `n_components`, or a group detector's minimum-N floor from Pitfall 5) — if the preset UI is built generically first and detector-specific parameter needs are discovered afterward, either the presets become misleading (a "sensitivity" slider that doesn't actually control what the user thinks) or group detectors get bolted onto the UI awkwardly.

**Why it happens:**
It's natural to design the friendly chooser UI first (since it's the more visible, user-facing win) and treat "which parameters exist per detector" as a data-driven afterthought. But group/multivariate detectors have qualitatively different parameter shapes than single-sensor ones, and this mismatch is more visible in a UI explicitly designed for simplicity/friendliness.

**How to avoid:**
Design the preset schema to be per-detector-type from the start: each detector declares its own mapping from `{Low, Medium, High}` (or "N/A, not sensitivity-shaped") to its actual parameter dict, rather than a single global slider definition the UI assumes applies everywhere. For group detectors, decide explicitly whether "sensitivity" even makes sense as a concept, or whether groups need a different simple-mode axis (e.g., "how many members must agree" or "how strict is joint scoring").

**Warning signs:**
- The preset-to-parameter mapping table is defined once, globally, rather than per detector/mode.
- Group detector configuration in the SPA reuses the exact same Low/Med/High UI component with no adaptation, and "Medium" silently maps to defaults that don't reflect the min-N or scaling concerns from Pitfalls 2/5.

**Phase to address:**
Algorithm-chooser UX phase, ideally sequenced *after* group/multivariate detector parameters are known (not before), so the preset abstraction is designed against the full parameter surface, not just single-sensor detectors.

---

## Technical Debt Patterns

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| Fake "multivariate" by looping univariate `ScoreBatch` per member and diffing in .NET | No proto change, ships fast | Joint anomalies (leak-style, no single member individually abnormal) never detected; misrepresents the feature | Never for "joint multivariate" mode; acceptable only as an interim peer-divergence-only MVP if explicitly labeled as such |
| Skip per-feature scaling in first multivariate detector pass | Simpler Fit/Score code | Pressure/high-magnitude features dominate scores; wrong root cause on leak-style anomalies | Never — add a mixed-unit synthetic test before shipping any joint detector |
| Zip group members' Influx points by list index instead of resampling | Fast to write | Compares non-simultaneous readings; false positives/negatives on real cadence mismatches | Never |
| Membership-derived group `unique_id` | "Obvious" deterministic choice | Orphaned MQTT entities on every membership edit | Never — use a stable operator-assigned `group_id` |
| Mean/stddev consensus for peer-divergence instead of median/MAD | Simpler math | Degenerates badly at small N; one outlier corrupts its own detection baseline | Never for N<10; acceptable for large groups where a single outlier can't skew the mean much |
| Build-time-fixed SPA base path (no hash routing, no runtime templating) | Standard SPA tooling defaults | Breaks under HA's dynamic Ingress prefix; blank page in production | Never for an Ingress-hosted add-on |
| Ad hoc per-handler auth checks for new SPA API endpoints | Fast to add one endpoint | Auth coverage gaps as API surface grows | Acceptable only until the API surface exceeds ~3-4 routes; convert to group middleware before then |

## Integration Gotchas

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| InfluxDB (group batch) | Query raw points per member, zip by index | Use Flux `aggregateWindow()` per member onto a shared grid; cap forward-fill staleness |
| PyOD multivariate detectors | Fit on raw mixed-unit feature matrix | Standardize per-feature (z-score using each member's own rolling stats); persist scaler with the model |
| HA MQTT discovery (groups) | Derive `unique_id` from member list/hash | Stable `group_id` independent of membership; retract only on group deletion, not on every membership edit |
| HA Ingress + SPA | Build SPA with default history-mode router and absolute build-time base path | Hash-based routing + relative asset base, or runtime `X-Ingress-Path` templating of `index.html` |
| gRPC proto (group scoring) | Reuse univariate `ScoreBatchRequest`/`Verdict` looped per member | Add an additive multi-series message type; keep existing univariate RPCs unchanged |
| `EntitiesConfig.Groups`/`Covariates` activation | Replace `object?` with a strict typed schema with no back-compat test | Tolerant loader (null/absent → empty), regression test against a real pre-v4.0 `entities.yaml` |
| SPA API auth | New `/api/*` endpoints added without the existing `IsAuthorizedRequest`-equivalent check | Route-group middleware applied to all `/api/*` endpoints, not a per-handler line to remember |

## Performance Traps

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| Group batch scoring sharing a resource (gRPC channel, semaphore) with the streaming path | Single-sensor <2s latency regresses after groups ship | Independent scheduling loop + independent connection/pool sizing for group batch calls | As soon as a group's Influx query or Fit call is slow (large window, cold cache) |
| Unbounded forward-fill for missing group-member readings | Dead/unreachable sensor silently "agrees" with the group forever | Staleness cap on carried-forward values (e.g., 3 buckets) | Any sensor outage during a group evaluation window |
| Node build step under QEMU aarch64 emulation | Local `buildx` release time balloons from minutes to tens of minutes | Test aarch64 build time early; consider native ARM build runner if it becomes a bottleneck | First real multi-arch SPA build |
| Re-fitting group scaler from scratch on every batch tick | Normalization drifts if window is short; inconsistent scores tick-to-tick | Persist scaler as part of the saved model state (same lifecycle as detector params) | Any group with a batch interval shorter than its natural signal drift period |

## Security Mistakes

| Mistake | Risk | Prevention |
|---------|------|------------|
| New SPA `/api/*` endpoints missing the Supervisor-IP/loopback auth gate | LAN peers other than HA Supervisor can read/write group config or trigger detector actions | Route-group middleware enforcing the same `IsAuthorizedRequest`-equivalent check across all `/api/*` routes by construction |
| SPA API client hardcodes `Authorization`/API-key headers to "fix" ingress auth confusion | Reintroduces the auth-model confusion the v3.0 research explicitly warned against (Ingress does not need app-level auth) | Do not add token/API-key auth; rely on Supervisor-IP + Ingress session model, documented explicitly in code as v3.0 already does |
| Group config accepts arbitrary member `entity_id` values without validating they exist in `IHaSensorRegistry` | Detector Fit/Score calls reference nonexistent entities, or (Flux injection surface) unsanitized entity_ids reach InfluxDB queries | Validate group members against the existing sensor registry at config-save time; reuse the existing `_safeFluxString` allowlist guard in `InfluxDbReader` for any new group-query construction |

## UX Pitfalls

| Pitfall | User Impact | Better Approach |
|---------|-------------|------------------|
| Group binary_sensor states a single member as "the anomaly" with full confidence | Users chase the wrong sensor when attribution is a probabilistic ranking, especially with correlated members | Publish a per-member score/rank sensor alongside (or instead of) a single flat "this one" boolean; word entity names as "most likely" |
| Sensitivity Low/Med/High preset reused unchanged for group/multivariate detectors | "Medium" silently means something different (or nothing coherent) for a joint multivariate detector vs. a single-sensor one | Per-detector-type preset-to-parameter mapping, decided after group detector parameters are known |
| No UI feedback when a group is too small (N<3) for peer-divergence | Users configure a 2-member group expecting meaningful divergence detection, get erratic/meaningless flags | Explicit UI validation warning at group-creation time with the minimum-N rule stated plainly |
| No UI feedback on mixed-unit groups | Users group temperature + humidity for "peer-divergence" (a category error) and get permanently-flagging nonsense | Hard validation error at group-save time using existing `unit_of_measurement` data already in the sensor registry |

## "Looks Done But Isn't" Checklist

- [ ] **Multivariate detection**: Verify the detector-side code actually fits a joint multivariate PyOD model on a 2D feature matrix — not N independent univariate `ScoreBatch` calls diffed in .NET.
- [ ] **Feature scaling**: Run a synthetic mixed-unit test (spike one low-magnitude feature like humidity while holding pressure flat) and confirm the joint score responds — not just the high-magnitude feature.
- [ ] **Time alignment**: Test two group members at deliberately different cadences (e.g., 30s vs 10min reporting); confirm resampled/aligned comparison, not index-zipped raw points.
- [ ] **Small-N peer-divergence**: Test a 2-member group; confirm either a UI-level rejection/warning or an explicitly-labeled degraded mode — not silent, unstable divergence math.
- [ ] **Group MQTT stability**: Edit a group's membership via the SPA; confirm the *same* HA entity updates in place (no orphaned old entity, no duplicate new one).
- [ ] **Single-sensor latency preserved**: Measure streaming latency before and after group batch scoring ships; confirm no regression on the existing <2s Core Value path.
- [ ] **SPA under real Ingress**: Open the SPA exclusively via "Open Web UI" in a real HA Supervisor install (not direct port access); confirm all routes, assets, and API calls work — including on a fresh page reload mid-route (not just initial navigation from `/`).
- [ ] **SPA API auth coverage**: Enumerate every `/api/*` route introduced for the SPA; confirm each is covered by the Supervisor-IP/loopback auth gate (ideally via shared middleware, not per-handler checks).
- [ ] **Config back-compat**: Load a real, pre-v4.0 `entities.yaml` (from a live 2.0.9-era install) through the new loader with typed `Groups`/`Covariates`; confirm it loads without error and without silently dropping the file's existing per-entity config.
- [ ] **Image size re-baselined**: Confirm the CI/local release process asserts a new, explicit image-size ceiling (not the stale 2 GB v3.0 number, and not silently removed).

## Recovery Strategies

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|-----------------|
| Fake multivariate (looped univariate) shipped as "joint detection" (P1) | HIGH | Requires proto extension + new detector code path; effectively redo the feature. Cheaper to catch in design review than after shipping. |
| Unscaled joint detector dominated by one feature (P2) | MEDIUM | Add scaler to Python detector, refit affected group models; no proto/schema change needed, but users may have been getting wrong root-cause attributions in the interim |
| Misaligned time-zipped group comparisons (P3) | MEDIUM | Swap in `aggregateWindow`-based resampling; refit; no data loss, but historical group verdicts before the fix were unreliable |
| Membership-derived group `unique_id` causing MQTT churn (P7) | MEDIUM | Introduce stable `group_id`, migrate existing groups (one-time config migration to assign IDs), retract truly-orphaned old entities once |
| SPA breaks under real Ingress (P10) | MEDIUM | Switch to hash routing or add runtime base-path templating; rebuild and re-verify against live Ingress; no data loss |
| New `/api/*` endpoint missing auth check (P11) | LOW–HIGH depending on exposure window | Add the missing check immediately; audit logs/config for unauthorized access during the exposure window if the add-on was ever installed with that gap live |
| Config load failure on `Groups`/`Covariates` activation (P12) | LOW | Loosen the loader back to tolerant parsing; this is exactly the class of regression v3.0's `EntitiesConfigLoader` softening already has a fix pattern for |

## Pitfall-to-Phase Mapping

| Pitfall | Prevention Phase | Verification |
|---------|------------------|---------------|
| Fake multivariate via looped univariate calls (P1) | Proto/detector-contract phase (first v4.0 phase) | Code review confirms a real 2D-matrix PyOD fit exists for joint mode; proto diff is additive, not a rewrite of existing messages |
| Unscaled mixed-unit joint scoring (P2) | Detector implementation phase (joint multivariate) | Synthetic mixed-unit test: low-magnitude feature spike must move the joint score |
| Misaligned group time series (P3) | Batch-groups / InfluxDB resampling phase | Differing-cadence synthetic test; confirm `aggregateWindow`-based alignment, staleness cap present |
| Unvalidated mixed-unit groups (P4) | Config-model activation phase | Test: mixed-unit group config is rejected or explicitly warned at save time |
| Small-N peer-divergence instability (P5) | Peer-divergence detection phase | Test: N=2 group either rejected or explicitly degraded-mode; median/MAD (not mean/stddev) used |
| Overconfident "which member" attribution (P6) | Peer-divergence phase + SPA group-detail UI | Group MQTT/UI surfaces a score/rank, not a single flat boolean; correlated-member test documented |
| Group MQTT entity churn on membership change (P7) | Config-model + MQTT-discovery phase for groups | Test: edit membership, assert same entity updates in place, no orphan, mirrors existing T-03-01 retraction test pattern |
| Group batch blocking single-sensor streaming path (P8) | Group batch-scoring implementation phase | Latency regression test: streaming <2s path measured before/after group feature lands |
| Node build step image/QEMU bloat (P9) | SPA build/deploy integration phase | Re-baselined, explicit image-size gate; aarch64 build-time check |
| SPA breaks HA Ingress dynamic base path (P10) | SPA scaffolding phase (before feature UI work) | Manual + scripted check: open exclusively via "Open Web UI," including deep-link reload, not just initial `/` load |
| SPA API endpoints missing auth gate (P11) | SPA API-surface design phase | Route-group middleware covers all `/api/*`; enumerate routes and confirm coverage |
| `Groups`/`Covariates` schema activation breaks old configs (P12) | Config-model activation phase | Regression test loads a real captured pre-v4.0 `entities.yaml` |
| Detector-chooser presets don't fit group detector parameter shapes (P13) | Algorithm-chooser UX phase (sequence after group detector params are known) | Per-detector-type preset mapping table exists; group detectors have an explicit (possibly "N/A sensitivity") mapping, not a copy-pasted single-sensor one |

## Sources

- Codebase (read directly, HIGH confidence): `proto/argus.proto`, `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs`, `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs`, `orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs`, `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs`, `orchestrator/Argus.Orchestrator/Program.cs`, `.planning/PROJECT.md`, `.planning/MILESTONES.md`, prior `.planning/research/PITFALLS.md` (v3.0 Ingress/htmx pass)
- [PyOD: A Python Toolbox for Scalable Outlier Detection (JMLR)](https://jmlr.org/papers/volume20/19-011/19-011.pdf)
- [Time Series: The problem with resampling — TotalEnergies Digital Factory](https://medium.com/totalenergies-digital-factory/time-series-the-problem-with-resampling-7baea5a3873c)
- [Sync Without Guesswork: Incomplete Time Series Alignment (arXiv)](https://arxiv.org/pdf/2512.18238)
- [Conditional Attribution for Root Cause Analysis in Time-Series Anomaly Detection (arXiv)](https://arxiv.org/pdf/2604.17616)
- [Root Cause Identification for Collective Anomalies given an Acyclic Summary Causal Graph (arXiv)](https://arxiv.org/pdf/2303.04038)
- [Explainable correlation-based anomaly detection for Industrial Control Systems (PMC)](https://www.ncbi.nlm.nih.gov/pmc/articles/PMC11832479/)
- [Home Assistant MQTT integration docs — discovery removal via empty retained payload](https://www.home-assistant.io/integrations/mqtt)
- [MQTT discovery fails to completely remove a deleted entity — home-assistant/core#32509](https://github.com/home-assistant/core/issues/32509)
- [How to use X-Ingress-Path in an add-on — community.home-assistant.io](https://community.home-assistant.io/t/how-to-use-x-ingress-path-in-an-add-on/276905)
- [Trouble with static assets in custom addon with ingress — community.home-assistant.io](https://community.home-assistant.io/t/trouble-with-static-assets-in-custom-addon-with-ingress/712298)
- [How can I dynamically configure VUE_APP_URL_BASE_API for a HA addon with ingress — lune.dev](https://www.lune.dev/questions/7693/how-can-i-dynamically-configure-vueappurlbaseapi-for-a-home-assistant-addon-with)
- [Single Page Application Routing Using Hash or URL — dev.to](https://dev.to/thedevdrawer/single-page-application-routing-using-hash-or-url-9jh)
- [Ability to detect slow/blocked client on streaming RPC — grpc/grpc#18739](https://github.com/grpc/grpc/issues/18739)
- [Should I add backpressure on stream sender side? — grpc/grpc-go#2747](https://github.com/grpc/grpc-go/issues/2747)

---
*Pitfalls research for: Argus v4.0 — Group & Multivariate Anomaly Detection + SPA UX*
*Researched: 2026-07-02*
