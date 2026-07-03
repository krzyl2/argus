---
gsd_state_version: 1.0
milestone: v4.0
milestone_name: Group & Multivariate Anomaly Detection + UX
current_phase: 999.1
current_phase_name: BACKLOG
status: verifying
stopped_at: "08-04-PLAN.md Tasks 1-2 complete (algorithm chooser, attribution, area suggestions); Task 3 checkpoint:human-verify pending"
last_updated: "2026-07-03T10:24:56.820Z"
last_activity: 2026-07-03
last_activity_desc: Phase 9 complete, transitioned to Phase 999.1
progress:
  total_phases: 6
  completed_phases: 1
  total_plans: 22
  completed_plans: 18
  percent: 17
---

# Project State: Argus

## Current Status

- Milestone: **v4.0 Group & Multivariate Anomaly Detection + UX — in progress** (Phases 5-9; Phases 5-8 shipped 2026-07-02, Phase 9 planned 2026-07-03)
- Previous: **v3.0 Ingress Configuration UI — SHIPPED & archived 2026-07-02** (add-on 2.0.9)
- Next: Execute Phase 9 with `/gsd-execute-phase 9`
- Last action: Phase 9 planned — 3 plans (09-01/02/03), all Wave 1, plan-checker VERIFICATION PASSED

## Deferred Items

Items acknowledged and deferred at v3.0 milestone close on 2026-07-02 (operator chose to skip formal
UAT after live bring-up of add-on 2.0.9 in real HA confirmed core flows work):

| Category | Item | Status |
|----------|------|--------|
| uat | Phase 01 — 01-UAT.md (4 scenarios) | testing (deferred) |
| uat | Phase 02 — 02-UAT.md (3 scenarios) | testing (deferred) |
| uat | Phase 03 — 03-UAT.md (3 scenarios) | testing (deferred) |
| uat | Phase 04 — 04-UAT.md (4 scenarios) | testing (deferred) |
| verification | Phase 01 — 01-VERIFICATION.md | human_needed (deferred) |
| verification | Phase 02 — 02-VERIFICATION.md | human_needed (deferred) |
| verification | Phase 03 — 03-VERIFICATION.md | human_needed (deferred) |
| verification | Phase 04 — 04-VERIFICATION.md | human_needed (deferred) |

Live bring-up on 2026-07-02 informally confirmed: add-on starts, Ingress UI serves, HA WebSocket
connects, entity save + hot-reload work. The deferred items are the formal wall-clock/propagation
checks (sub-2s reload latency, MQTT retraction within 30s, detector pre-fill) — not blockers, but
not formally signed off.

## Project Reference

See: .planning/PROJECT.md

**Core value:** Anomalies appear in HA as live binary_sensor + score entities within 2 seconds (single-sensor). Group detection has its own, looser latency target (v4.0).
**Current focus:** Phase 9 — 2-Member Groups + Algorithm Guidance Correction

## Phase Status (v4.0)

| Phase | Name | Status |
|-------|------|--------|
| 5 | Group Detection Core (Proto + Python Detectors) | Not started |
| 6 | Batch Group Pipeline | Not started |
| 7 | SPA Scaffolding | Not started |
| 8 | Group Config UI + Algorithm Chooser | Not started |

```
Progress: [░░░░░░░░░░] 0%
```

v1.0 + v2.0 + v3.0 archived under `.planning/milestones/` and `.planning/archive/`.

## Accumulated Context

### v2.0 outcomes (relevant to v3)

- Add-on is a single-container image `ghcr.io/krzyl2/argus` (amd64+arm64), built locally via buildx + QEMU and pushed to GHCR (CI workflow also present in `.github/workflows/build.yml`).
- HA connection: orchestrator uses a raw WebSocket client (`HaWebSocketClient`) to the Supervisor proxy `ws://supervisor/core/websocket` with `Authorization: Bearer SUPERVISOR_TOKEN` on the upgrade — NetDaemon.Client could not (its WS factory is internal); direct `homeassistant:8123` is refused for add-ons.
- Config today: `config-gen` (`10-config-gen.sh`) turns add-on options → env + `/data/entities.yaml`; `gen-entities.py` builds entities **only** from the explicit `entities` list and hardcodes the `hst` detector. `include_patterns`/`exclude_patterns` are currently **ignored** (v3 closes this).
- `EntitiesConfigLoader` already supports per-entity `detectors: [{name, params}]` — the data model for v3's detector-assignment UI exists; the UI + config-gen wiring is what's missing.
- `SelectDiscoverableSensors` + the startup `get_states` discovery already enumerate live numeric sensors — reuse for the v3 selection UI.
- Detector binds `0.0.0.0` in local mode (watchdog reachability); InfluxDB batch path is skipped when `influx_url` is empty (streaming-only).
- Add-on image base: `ghcr.io/home-assistant/base-debian:bookworm`, Python 3.11 (no apt python3.12 on Debian), .NET 8 runtime via `dotnet-install.sh`.

### v3.0 architecture decisions (from research)

- **SDK migration:** `Microsoft.NET.Sdk.Worker` → `Microsoft.NET.Sdk.Web`; `Host.CreateApplicationBuilder` → `WebApplication.CreateBuilder`. All existing `AddHostedService`/`AddSingleton` registrations are identical under `WebApplication` — no service registration changes.
- **Co-host in orchestrator process:** Kestrel + Minimal API inside the existing process; no second s6 service. UI reads same singletons as workers (EntitiesConfig, health signals).
- **UI technology:** Server-rendered HTML + htmx 2.0.10 (14 KB, BSD 0-Clause, committed to `wwwroot/`). No SPA, no Node.js build step, no CDN. Air-gapped safe.
- **Config source of truth:** `/data/entities.yaml` unchanged — UI reads and writes it via `YamlDotNet` (already in project). No new config format.
- **Reload mechanism:** `ILiveEntitiesConfig` singleton with `Interlocked.Exchange` swap + `ConfigChanged` event. `HaListenerWorker` cancels inner CTS (not host-level stoppingToken) and restarts `ScoreStreamPipeline.RunAsync` loop only. MQTT + gRPC transport stays alive. Streaming gap < 1 second.
- **Kestrel bind:** `0.0.0.0:8099` (not loopback). Supervisor connects from `172.30.32.2`.
- **Docker base image:** `mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled` (replaces `runtime:8.0-jammy-chiseled`; ~10 MB larger; same distroless base).
- **No `ports:` entry in config.yaml:** Ingress-only; exposing the port bypasses HA auth.

### Critical pre-conditions (must not be deferred)

- **Phase 1:** `EntitiesConfigLoader.Validate()` must change from `throw` to `LogWarning` on empty entities — otherwise orchestrator crashes on first-boot with no entities configured, blocking the UI from loading.
- **Phase 1:** Atomic config write (temp-then-rename + SemaphoreSlim(1)) must be in place from the start.
- **Phase 2 (start):** `gen-entities.py` guard (`_source: ui` marker or `.ui_config_present` lock file) must land BEFORE the first UI save endpoint is wired — otherwise an add-on restart after a UI save silently erases user config.
- **Phase 3 (before planning):** Source-read `BatchSchedulerWorker` to determine whether it captures `EntitiesConfig.Entities` at construction or per-cycle. This decides whether it is in the "must change" list for Phase 3.

### Live research gaps (resolve during phase)

- **Phase 1:** X-Ingress-Path / UsePathBase conflict — live HA OS test required. Safe implementation: set PathBase per-request AND emit `<base href="{ingressPath}/">`. Verify via "Open Web UI" (never direct port).
- **Phase 2:** Supervisor `validate_session` API shape — probe live Supervisor before implementing `IngressAuthMiddleware`. Fallback: accept from 172.30.32.2 in Phase 2 MVP, complete auth middleware in Phase 4.

### Locked decisions (historical, still in force)

- .NET 8 orchestrator + Python gRPC detector (D2); gRPC mTLS for remote, insecure loopback for local (D4)
- MQTT discovery for HA entity creation (D6); PyOD MAD + STL + River HST detection engines
- Mono-repo: proto/, orchestrator/, detector/, deploy/, argus/ (add-on)
- Licenses: BSD/Apache/MIT only (no GPL, no ADTK/MPL-2.0)

### Plan 01-01 decisions (config seam)

- Null YAML deserialization returns `new EntitiesConfig()` instead of throwing — maintains no-crash guarantee for all first-boot scenarios
- `ConfigWriter` not registered in DI by Plan 01 — Plan 02 owns `Program.cs` to avoid parallel-wave file conflict
- `ConfigWriter.WriteAsync` writes verbatim strings; YAML serialization deferred to Phase 2+ callers (keeps writer focused and testable)

### Plan 01-02 decisions (SDK migration + Kestrel + Ingress scaffold)

- Kestrel bound via `ConfigureKestrel(IPAddress.Any, 8099)` — not `UseUrls` or `ASPNETCORE_URLS`
- Dual PathBase + `<base href>` defense handles both Supervisor-strips and Supervisor-does-not-strip behaviors (STACK-vs-PITFALLS conflict deferred to live-HA verification)
- `ArgusHealthSignals.DetectorConnected` volatile bool added — cached by HealthPublisherWorker every ~15 s for zero-latency UI reads (no gRPC call on page load)
- `ingressPath` HTML-encoded via `WebUtility.HtmlEncode` before `<base href>` interpolation (T-01-08)
- argus/Dockerfile unchanged — add-on uses base-debian:bookworm + dotnet-install.sh; Web SDK publish carries ASP.NET DLLs

### Decisions

- [Phase 02-03]: Root GET / redirects to /sensors; placeholder replaced by entity picker
- [Phase 02-03]: Single YamlDotNet root-dict serialization (_patterns + entities) — never string-format YAML (T-02-08)
- [Phase 02-03]: Empty checkbox selection writes entities: [] (valid, Pitfall 5)
- [Phase 02-03]: Interim auth: X-Ingress-Path OR RemoteIpAddress=172.30.32.2/loopback (T-02-09); Phase 4 completes validate_session
- [Phase ?]: RetractAsync delegate overload for testability — mirrors PublishAllAsync pattern, avoids IMqttConnection interface
- [Phase ?]: HaListenerWorker inner-CTS restart loop: virtual seams for testability; null-before-dispose Pitfall 3 guard; MakeLive() test wrapper pattern; fire-and-forget ConfigChanged republish in MqttPublisherWorker
- [Phase ?]: BuildDetectorEntry is public static on EntityPickerPage for direct test access and reuse by /api/detectors/new-entry
- [Phase ?]: DetectorFieldParser extracted as internal static — directly testable, accepts IEnumerable<KVP> for offline tests
- [Phase ?]: Validate-before-Swap: EntitiesConfigLoader.Load runs Validate() before Swap; bad config cannot crash live pipeline
- [Phase 05-01]: Dispatch peer-divergence vs joint-multivariate mode server-side purely on the detector string field, no separate mode enum — Matches 05-RESEARCH.md Open Question 2 recommendation and existing ScoreBatchRequest.detector convention
- [Phase 05-01]: Reused existing Verdict message for per_member and group_verdict fields in GroupScoreResponse — Plan explicitly avoids a parallel score message
- [Phase 05]: 0.7979 meanAD-fallback constant documented as Iglewicz-Hoaglin statistics convention in code comment (RESEARCH A2 resolved as Claude's discretion)
- [Phase 05]: Below-floor no-verdict kept representationally distinct from MAD=0 all-normal case per RESEARCH Pitfall 4
- [Phase ?]: [Phase 05-03]: Extended joint-anomaly test fixture from RESEARCH.md's 5 rows to 10 (same correlated pattern) - PCA/COPOD produced divide-by-zero/near-tie on the tiny original fixture; production code unchanged, copied verbatim from RESEARCH.md
- [Phase ?]: [Phase 05-04]: is_anomaly for joint-multivariate group detectors derived from score > model._model.threshold_ (not predict()) — avoids a second decision_function() call that would corrupt ECOD/COPOD's mutable self.O attribution matrix
- [Phase ?]: [Phase 05-04]: PeerDivergenceDetector constructed fresh per ScoreGroupBatch call rather than read from registry — stateless, no fit() needed; registry entry exists only for FitGroup no-op symmetry with the stl pattern
- [Phase ?]: [Phase 06-01]: EntityConfig.Covariates/Groups placeholders removed entirely (not deprecated-in-place) — IgnoreUnmatchedProperties() makes this safe for any stray YAML on existing installs
- [Phase ?]: [Phase 06-01]: IHaSensorRegistry threaded as an optional 3rd parameter (default null) on EntitiesConfigLoader.Load rather than a new overload, keeping all existing 2-arg call sites unchanged
- [Phase ?]: [Phase 06-01]: Peer-divergence unit rejection only fires when 2+ distinct non-null units are observed; registry null or under-resolved units degrades to skip-check-and-keep (cold boot)
- [Phase 06-02]: GroupInfluxReader is a new class reusing the existing IInfluxQueryApi seam, keeping InfluxDbReader untouched
- [Phase 06-02]: Reader surfaces LastSeenUtc + null cells only; staleness_cap exclusion policy deferred to Plan 06-04
- [Phase 06-02]: Rule 3: stubbed ScoreGroupBatchAsync/FitGroupAsync in BatchSchedulerWorkerTests.FakeBatchDetectorClient to keep test project compiling
- [Phase ?]: [Phase 06-03]: Peer/joint discovery dispatch via string.Equals(group.Mode, peer_divergence, OrdinalIgnoreCase) — matches existing server-side dispatch convention
- [Phase ?]: [Phase 06-03]: RetractGroupAsync takes IEnumerable<string?> removedMembers — single null entry retracts joint group pair, non-null entries retract specific peer members
- [Phase ?]: [Phase 06-04]: BuildGroupMatrix shared between score and fit paths (isPeer flag branches joint-vs-peer staleness policy) - one place owns the exclusion logic
- [Phase ?]: [Phase 06-04]: Default staleness_cap of 30 minutes applied when a group's Params omits the key or fails TimeSpan.TryParse - degrade-safely rather than throw at scoring time
- [Phase ?]: [Phase 06-04]: MqttPublisherWorker ConfigChanged ordering locked: retract-removed-groups -> republish-entities -> republish-groups -> update _lastGroups snapshot
- [Phase 07-01]: argus.css moved to orchestrator/ui/public/css/ as new canonical source; old wwwroot copy untracked (Vite regenerates it)
- [Phase 07-01]: SaveRequest uses natural nested entities:[{entityId, detectors:[{name, params}]}] shape (RESEARCH Open Q2) - must match 07-02's C# DTO exactly
- [Phase 07-02]: SaveRequest.Include/Exclude are raw strings (not arrays) — matches types.ts exactly, SPA sends raw textarea content
- [Phase 07-02]: DetectorDefaults extracted as standalone testable static class rather than inlined in Program.cs endpoint
- [Phase ?]: [Phase 07-03]: COPY --from=ui-build sources from /src/Argus.Orchestrator/wwwroot/ (Vite's actual configured outDir per 07-01), not the plan-assumed /src/ui/dist/
- [Phase ?]: [Phase 07-03]: ARG BUILD_FROM moved to a single global declaration before the first FROM - a stage-scoped ARG between FROM lines is not visible to the next FROM in Docker's build model
- [Phase ?]: [Phase 07-03]: -SkipPublish kept as a documented no-op in build-push.ps1 for CLI back-compat rather than removed
- [Phase 08]: peer_divergence threshold moved from module constant to instance field via from_params; multivariate contamination/n_estimators threaded through _DETECTOR_FACTORY; PCA standardization=False stays hardcoded as correctness constant, not a knob
- [Phase 08]: registry._create_detector and fit_one gained optional params dict (default None), mirroring D-06-01 precedent; servicer.py ScoreGroupBatch/FitGroup forward dict(request.params) end-to-end
- [Phase 08]: [08-02] HaSensorEntry.UpdateSnapshot's 3rd param (entityAreaNames) is optional/defaulted to null on the single interface method rather than a second overload — keeps every pre-existing fake IHaSensorRegistry implementation compiling without touching files outside this plan's scope
- [Phase 08]: [08-02] _patterns: is re-derived from the raw on-disk YAML on every group save rather than modeled in EntitiesConfig, since IgnoreUnmatchedProperties would otherwise drop it on load
- [Phase 08]: [08-02] GroupInputValidator extracted as its own file mirroring InputValidator.cs convention, rather than inlining validation logic into Program.cs's POST /api/groups/save handler
- [Phase 08-03]: HaSensorRegistry.GetFiltered extended to match friendly_name OR entity_id -- SRCH-01 could not work end-to-end from client copy alone since #/sensors search is server-filtered via GET /api/sensors?q=
- [Phase 08-03]: GET /api/sensors now serializes areaName/domain -- HaSensorEntry carried these since 08-02 but Program.cs never put them in the JSON, so SRCH-02 area grouping had no data to render
- [Phase 08-03]: MemberPicker renders its own lightweight checkbox rows instead of wrapping SensorListRow, since SensorListRow's detector-disclosure UI does not apply to member selection
- [Phase 08-03]: deleteGroup composes on the same saveGroup POST path (full-list-replace minus one group) rather than a dedicated delete endpoint
- [Phase 08-04]: state/groupEditor.ts's chooser state machine (chooserMode/selectedDetector/guidedRecommended) kept independent from state/groups.ts's draftDetector/draftParams — AlgorithmChooser is the single sync point (one useEffect), keeping the state machine unit-testable without duplicating the persisted draft shape
- [Phase 08-04]: draftPresetLabel + pendingPrefillMembers added as new signals in state/groups.ts — UI-only bookkeeping for the "customized" indicator and the SRCH-03 approve-only pre-fill handoff, never sent to the server
- [Phase 08-04]: AttributionPanel polls at a fixed 60s interval and swallows poll errors into last-known-state rendering (soft, best-effort display) rather than an error banner
- [Phase ?]: [Phase 09-01] Config-validation member floor lowered to 2 for BOTH joint and peer_divergence modes (uniform, no mode branching) — required to unblock 2-member peer_divergence groups routed to the pairwise-delta path in Plan 09-02/09-03
- [Phase ?]: [Phase 09-01] Guided chooser together answer -> copod (was ecod); DetectorCatalog BestFor copy rewritten per empirical PyOD false-positive findings, flagged as draft pending operator sign-off

### Blockers

- 08-04 Task 3: checkpoint:human-verify (live-HA Ingress round-trip for the new group/algorithm-chooser/attribution UI) awaiting operator execution — see 08-04-SUMMARY.md "Pending Human Checkpoint"

### Roadmap Evolution

- Phase 9 added: 2-Member Groups + Algorithm Guidance Correction — lower the joint-mode member floor to 2, add a pairwise-delta path (existing single-entity MAD detector on member_a − member_b) for 2-member peer_divergence, switch the guided chooser's "together" default from ecod to copod, and rewrite DetectorCatalog.cs BestFor copy. Raised 2026-07-03 during live verification of Phase 8, from two operator use cases (2 front-tire pressures; 2-sensor water pressure+temperature pair) plus empirical PyOD testing that found the existing "together" guidance produces ~90% false positives on correlated-pair relationship-break scenarios. See Phase 9 section in ROADMAP.md for full research context.

## Performance Metrics

| Metric | Target | Current |
|--------|--------|---------|
| Plans completed | — | 0/TBD |
| Phases completed | 4 | 0/4 |
| Requirements mapped | 9/9 | 9/9 |
| Phase 01 P01-01 | 2 | 2 tasks | 5 files |
| Phase 01 P01-02 | 231 | - tasks | - files |
| Phase 02 P02-01 | 4m | 3 tasks | 7 files |
| Phase 02 P02-02 | 8m | 2 tasks | 3 files |
| Phase 02 P02-03 | 5m | 2 tasks | 6 files |
| Phase 03 P03-01 | 10m | 2 tasks | 5 files |
| Phase 03 P03-02 | 9m10s | 3 tasks | 8 files |
| Phase 03 P03-03 | 8m43s | 2 tasks | 7 files |
| Phase 04 P04 | 6m | 2 tasks | 2 files |
| Phase 05 P01 | 6min | 2 tasks | 2 files |
| Phase 05 P02 | 6m | 2 tasks | 3 files |
| Phase 05 P03 | 12min | 3 tasks | 5 files |
| Phase 05 PP04 | 8min | 3 tasks | 3 files |
| Phase 06 P01 | 6min | 3 tasks | 4 files |
| Phase 06 P02 | 12min | 3 tasks | 6 files |
| Phase 06 P03 | 3min | 3 tasks | 7 files |
| Phase 06 P04 | 14min | 3 tasks | 6 files |
| Phase 07 P01 | 25min | 2 tasks | 34 files |
| Phase 07 P02 | 35min | 2 tasks | 11 files |
| Phase 07 P03 | 20min | 2 tasks | 3 files |
| Phase 08 P01 | 6min | 2 tasks | 7 files |
| Phase 08 P02 | 10min | 3 tasks | 20 files |
| Phase 08 P03 | 15min | 3 tasks | 22 files |
| Phase 08 P04 | 35min | 2 tasks | 17 files |
| Phase 09 P01 | 12min | 2 tasks | 7 files |

## Session Continuity

**Last session:** 2026-07-03T09:35:26.382Z
**Stopped at:** 08-04-PLAN.md Tasks 1-2 complete (algorithm chooser, attribution, area suggestions); Task 3 checkpoint:human-verify pending
**Resume file:** .planning/phases/08-group-config-ui-algorithm-chooser/08-04-SUMMARY.md

- Last session: 2026-06-30 — Plan 01-02 complete: SDK migration (Worker → Web), Kestrel 0.0.0.0:8099, X-Ingress-Path PathBase middleware, placeholder page (PlaceholderPage.cs), wwwroot assets (htmx 2.0.10, argus.css), config.yaml ingress keys. Live-HA verification deferred to operator.
- Resume point: Live-HA verification per 01-02-SUMMARY.md "Pending Live-HA Verification" section, then Phase 2 planning.

## Operator Next Steps

- Plan Phase 5 with /gsd-plan-phase 5

## Current Position

Phase: 999.1 — Algorithm tester/simulator in group config UI (BACKLOG)
Plan: Not started
Status: Phase complete — ready for verification
Last activity: 2026-07-03 — Phase 9 complete, transitioned to Phase 999.1
