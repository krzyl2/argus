---
phase: 260722-mbx
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - orchestrator/Argus.Orchestrator/Detection/RecentAnomaliesCache.cs
  - orchestrator/Argus.Orchestrator/Batch/BatchRunStatus.cs
  - orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs
  - orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
  - orchestrator/Argus.Orchestrator/Web/HealthProjection.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator.Tests/RecentAnomaliesCacheTests.cs
  - orchestrator/Argus.Orchestrator.Tests/BatchRunStatusTests.cs
  - orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs
  - orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs
  - orchestrator/Argus.Orchestrator.Tests/HealthProjectionTests.cs
  - orchestrator/ui/src/api/types.ts
  - orchestrator/ui/src/state/dashboard.ts
  - orchestrator/ui/src/state/dashboard.test.ts
  - orchestrator/ui/src/components/DashboardPage.tsx
autonomous: true
requirements: [QUICK-dashboard-real-data]

must_haves:
  truths:
    - "The Dashboard 'Home Assistant' KPI shows the real WebSocket connection state plus live entity count from GET /api/health — no 'mocked — no endpoint yet' hint remains."
    - "The 'System health' card lists 5 real components (Home Assistant, Detector, MQTT broker, Last batch run, InfluxDB) with live status/detail from GET /api/health."
    - "The 'Recent anomalies' card shows the last real anomalies newest-first from GET /api/anomalies/recent, and an empty-state row (not a mock banner) when there are none."
    - "A streaming anomaly is recorded only when its binary_sensor flag is actually published AND the reading is anomalous — warm-up/cooldown-suppressed readings are never recorded."
    - "A joint-group (GroupVerdict) anomaly is recorded when its verdict IsAnomaly is true."
    - "GET /api/health never serializes credentials (HaToken, MqttUser/Password, InfluxToken) — only allowlisted non-secret fields."
  artifacts:
    - orchestrator/Argus.Orchestrator/Detection/RecentAnomaliesCache.cs
    - orchestrator/Argus.Orchestrator/Batch/BatchRunStatus.cs
    - orchestrator/Argus.Orchestrator/Web/HealthProjection.cs
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/ui/src/state/dashboard.ts
    - orchestrator/ui/src/components/DashboardPage.tsx
  key_links:
    - "ScoreStreamPipeline.ProcessVerdictAsync (canPublishFlag && isAnomalous) -> IRecentAnomaliesCache.Record"
    - "BatchSchedulerWorker.RunGroupBatchAsync GroupVerdict branch (v.IsAnomaly) -> IRecentAnomaliesCache.Record; end of RunBatchAsync -> IBatchRunStatus.MarkRun"
    - "GET /api/health -> HealthProjection.Build(signals, mqtt.IsConnected, registry.GetAll().Count, settings, batchRunStatus.LastRunUtc)"
    - "GET /api/anomalies/recent -> IRecentAnomaliesCache.GetRecent() (newest-first) -> anomalies JSON"
    - "dashboard.ts loaders -> health/recentAnomalies signals -> DashboardPage KPI + System health + Recent anomalies render"
---

<objective>
Replace the three mocked areas on the Dashboard (orchestrator/ui/src/components/DashboardPage.tsx) with real data: the "Home Assistant" KPI, the "System health" list, and the "Recent anomalies" list. Add two read-only backend endpoints — GET /api/health (composite liveness + HA entity count, one fetch drives both the KPI and the 5-item health list) and GET /api/anomalies/recent (last N anomalies, newest-first) — plus a new in-memory bounded ring-buffer of recent anomaly events fed by the live streaming pipeline and the batch group scorer, and a small last-batch-run timestamp tracker.

Purpose: The Dashboard currently ships hardcoded MOCK_HEALTH / MOCK_ANOMALIES arrays and "mocked — no endpoint yet" banners. Operators cannot tell real system state from example data. This wires the screen to live orchestrator signals that already exist in DI (health flags, MQTT connectivity, HA entity count) and to a new anomaly-history buffer, so the Dashboard reflects the actual running instance.

Output: two new in-memory singletons (RecentAnomaliesCache, BatchRunStatus), a testable HealthProjection allowlist, two new minimal-API endpoints, recording hooks in the streaming pipeline and batch worker, new frontend types + dashboard state loaders, a de-mocked DashboardPage, and backend + frontend tests.
</objective>

<execution_context>
@$HOME/.claude/gsd-core/workflows/execute-plan.md
@$HOME/.claude/gsd-core/templates/summary.md
</execution_context>

<context>
@.planning/STATE.md
@orchestrator/Argus.Orchestrator/Detection/EntityStatusCache.cs
@orchestrator/Argus.Orchestrator/Batch/GroupStatusCache.cs
@orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs
@orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
@orchestrator/Argus.Orchestrator/Health/ArgusHealthSignals.cs
@orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs
@orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs
@orchestrator/Argus.Orchestrator/Web/SettingsProjection.cs
@orchestrator/Argus.Orchestrator/Program.cs
@orchestrator/Argus.Orchestrator.Tests/EntityStatusCacheTests.cs
@orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs
@orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs
@orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs
@orchestrator/ui/src/api/types.ts
@orchestrator/ui/src/api/client.ts
@orchestrator/ui/src/state/dashboard.ts
@orchestrator/ui/src/state/sensors.test.ts
@orchestrator/ui/src/components/DashboardPage.tsx
</context>

<tasks>

<task type="auto">
  <name>Task 1: New in-memory singletons — RecentAnomaliesCache (ring buffer) + BatchRunStatus + unit tests</name>
  <files>orchestrator/Argus.Orchestrator/Detection/RecentAnomaliesCache.cs, orchestrator/Argus.Orchestrator/Batch/BatchRunStatus.cs, orchestrator/Argus.Orchestrator.Tests/RecentAnomaliesCacheTests.cs, orchestrator/Argus.Orchestrator.Tests/BatchRunStatusTests.cs</files>
  <action>
    Create RecentAnomaliesCache.cs in namespace Argus.Orchestrator.Detection, following the EntityStatusCache.cs / GroupStatusCache.cs precedent (record + interface + sealed impl in one file). Define an immutable record `RecentAnomaly(string? EntityId, string? GroupId, double Score, string Detector, DateTimeOffset DetectedAtUtc)` — EntityId is set for single-sensor (streaming) anomalies, GroupId for group anomalies (exactly one of the two is non-null). Define interface `IRecentAnomaliesCache` with `void Record(RecentAnomaly anomaly)` and `IReadOnlyList<RecentAnomaly> GetRecent()` (documented as newest-first). Implement `RecentAnomaliesCache` as a bounded ring buffer: a private `const int Capacity = 20;`, a `LinkedList<RecentAnomaly>` with newest at the front, and a private `object` lock. Record adds to the front (AddFirst) and trims from the back while Count exceeds Capacity, all under the lock. GetRecent returns a point-in-time snapshot copy (a new List) under the lock, newest-first. Use a plain lock (not ConcurrentDictionary) because ordering + fixed capacity are required; writers are the pipeline + batch worker threads, readers are Kestrel threads.

    Create BatchRunStatus.cs in namespace Argus.Orchestrator.Batch. Define interface `IBatchRunStatus` with a `DateTimeOffset? LastRunUtc { get; }` getter and `void MarkRun(DateTimeOffset utc)`. Implement `BatchRunStatus`: back it with a `long _lastRunUtcTicks` field (0 = never run) and use `System.Threading.Interlocked.Exchange`/`Interlocked.Read` for cross-thread visibility (a 64-bit field cannot be marked volatile). LastRunUtc returns null when the backing value is 0, otherwise `new DateTimeOffset(ticks, TimeSpan.Zero)`. MarkRun stores `utc.UtcTicks`.

    Add RecentAnomaliesCacheTests.cs (xUnit, namespace Argus.Orchestrator.Tests), fully offline, mirroring EntityStatusCacheTests structure: (a) GetRecent on a fresh cache returns empty; (b) after recording three anomalies, GetRecent returns them newest-first (last recorded is index 0); (c) recording more than Capacity (record 25) leaves exactly 20 entries and the 5 oldest are evicted (assert the oldest surviving/first-evicted by a distinguishing field such as Score or DetectedAtUtc); (d) a snapshot returned by GetRecent is not mutated by a subsequent Record (proves it is a copy). Encode intent in the test names (newest-first ordering and capacity eviction are the contract the Dashboard depends on).

    Add BatchRunStatusTests.cs: (a) LastRunUtc is null before any MarkRun; (b) after MarkRun(t) LastRunUtc equals t (compare UtcTicks); (c) a later MarkRun replaces the earlier value.
  </action>
  <verify>
    <automated>dotnet test orchestrator/Argus.Orchestrator.sln --filter "FullyQualifiedName~RecentAnomaliesCacheTests|FullyQualifiedName~BatchRunStatusTests"</automated>
  </verify>
  <done>RecentAnomaliesCache is a thread-safe, newest-first, 20-entry bounded ring buffer and BatchRunStatus tracks the last batch-run timestamp with null-until-first-run semantics; both have passing unit tests and the solution builds.</done>
</task>

<task type="auto">
  <name>Task 2: Wire caches into the streaming pipeline + batch worker + DI, with recording-gate tests</name>
  <files>orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs, orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs, orchestrator/Argus.Orchestrator/Program.cs, orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs, orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs</files>
  <action>
    ScoreStreamPipeline.cs: follow the EXACT nullable-optional-ctor-param precedent already used for `IEntityStatusCache? statusCache = null`. Add a trailing optional parameter `IRecentAnomaliesCache? recentAnomalies = null` to BOTH the production constructor and the test constructor, and store it in a new `private readonly IRecentAnomaliesCache? _recentAnomalies;` field (assign null in the test ctor path the same way _gateway is). In ProcessVerdictAsync, inside the existing `if (canPublishFlag)` block (after PublishFlagAsync and the LastPublishedFlag assignment), record the anomaly ONLY when `isAnomalous` is true: `_recentAnomalies?.Record(new RecentAnomaly(reading.EntityId, null, score, "hst", DateTimeOffset.UtcNow));`. Detector is the literal streaming detector name — the bidi ScoreStream path is HST online scoring, so "hst" is accurate (not fabricated). Do NOT record from PublishFrozenAsync and do NOT record outside the canPublishFlag gate — mirroring that exact gate is what keeps warm-up/cooldown-suppressed readings out of the buffer.

    BatchSchedulerWorker.cs: follow the same precedent used for `IGroupStatusCache? groupStatusCache = null`. Append two trailing optional parameters `IRecentAnomaliesCache? recentAnomalies = null` and `IBatchRunStatus? batchRunStatus = null` to BOTH the test constructor and the production constructor, store them in new readonly fields, and forward both through the production ctor's `: this(...)` chain to the test ctor (add them to the chained argument list). In RunGroupBatchAsync, inside the existing `else if (response.GroupVerdict != null)` branch, after the `_groupStatusCache?.Set(...)` call, record a group anomaly when the verdict is anomalous: `if (v.IsAnomaly) _recentAnomalies?.Record(new RecentAnomaly(null, group.GroupId, v.Score ?? 0.0, group.Detector, DateTimeOffset.UtcNow));`. MVP scope decision (per research): record joint GroupVerdict anomalies only; per-member (peer_divergence) anomalies in the PerMember branch are NOT recorded this pass — they are already visible per-sensor and are noisier; this is an explicit, documented boundary, not a silent omission. Add a brief `// MVP: joint GroupVerdict anomalies only; per-member peer flags intentionally not recorded` comment at the recording site. At the very end of RunBatchAsync (after both the entity loop and the group loop complete), add `_batchRunStatus?.MarkRun(DateTimeOffset.UtcNow);`.

    Program.cs: register both new singletons unconditionally, directly after the IEntityStatusCache registration (~line 98): `builder.Services.AddSingleton<IRecentAnomaliesCache, RecentAnomaliesCache>();` and `builder.Services.AddSingleton<IBatchRunStatus, BatchRunStatus>();` (the Argus.Orchestrator.Detection and Argus.Orchestrator.Batch namespaces are already imported). Register them unconditionally — outside the `if (InfluxUrl)` block — because the streaming path records anomalies regardless of InfluxDB, and the health endpoint must read IBatchRunStatus (LastRunUtc stays null when the batch worker never runs). The AddSingleton<ScoreStreamPipeline>() registration needs no change: DI's greedy ctor selection still picks the production ctor and now also fills the new optional IRecentAnomaliesCache param from DI. In the BatchSchedulerWorker production factory registration (~lines 166-175, inside the InfluxUrl block), append two constructor arguments after the IGroupStatusCache argument: `sp.GetRequiredService<IRecentAnomaliesCache>()` and `sp.GetRequiredService<IBatchRunStatus>()`.

    ScoreStreamPipelineTests.cs: append recording-gate tests reusing the existing FakeStatePublisher / MakeLive / MakeReading / MakeVerdict helpers and the test constructor. Construct the pipeline with a real RecentAnomaliesCache passed to the test ctor (use a named argument `recentAnomalies:` if the statusCache param sits between). Case (a): a warmed-up entityState + a non-suppressed reading + an anomalous score (hysteresis crosses the high threshold, as in the existing "publishes flag" test) records exactly one RecentAnomaly whose EntityId matches and GroupId is null. Case (b): a suppressed reading (SuppressBinarySensor true) with the same anomalous score records nothing (GetRecent stays empty) — proving the recording mirrors the canPublishFlag gate.

    GroupBatchSchedulerTests.cs: append one test reusing the existing joint-mode fixtures (the fake group influx source with data + the fake batch detector client that returns a GroupVerdict, as in the existing "joint scored" test). Construct BatchSchedulerWorker with a real RecentAnomaliesCache via a named argument `recentAnomalies:`. Arrange the fake detector's GroupVerdict with IsAnomaly true, run RunBatchForTestAsync, and assert GetRecent returns exactly one entry whose GroupId equals the scored group's id and EntityId is null. (No new fakes — reuse the file's existing ones.)
  </action>
  <verify>
    <automated>dotnet test orchestrator/Argus.Orchestrator.sln --filter "FullyQualifiedName~ScoreStreamPipelineTests|FullyQualifiedName~GroupBatchSchedulerTests|FullyQualifiedName~BatchSchedulerWorkerTests"</automated>
  </verify>
  <done>Both caches are DI-registered singletons; the streaming pipeline records single-sensor anomalies only when the flag is published and anomalous; the batch worker records joint-group anomalies and stamps the last batch-run time; the production BatchSchedulerWorker factory injects both; new recording-gate tests pass and no existing batch/pipeline test regresses.</done>
</task>

<task type="auto">
  <name>Task 3: HealthProjection allowlist + GET /api/health + GET /api/anomalies/recent + backend tests</name>
  <files>orchestrator/Argus.Orchestrator/Web/HealthProjection.cs, orchestrator/Argus.Orchestrator/Program.cs, orchestrator/Argus.Orchestrator.Tests/HealthProjectionTests.cs</files>
  <action>
    Create HealthProjection.cs in namespace Argus.Orchestrator.Web, mirroring SettingsProjection.cs's allowlist discipline (D-07): it is the sole boundary between in-process ConnectionSettings/signals and the health JSON surface, and it MUST expose only non-secret fields. Define public records: `HealthComponent(string Key, string Label, string Status, string Detail)`, `HomeAssistantHealth(bool Connected, int EntityCount)`, and `HealthResponse(HomeAssistantHealth HomeAssistant, IReadOnlyList<HealthComponent> Components)`. Status values are drawn from the set ok | warn | error | idle (these map 1:1 to the SPA StatusDot). Implement `public static HealthResponse Build(ArgusHealthSignals signals, bool mqttConnected, int haEntityCount, ConnectionSettings settings, DateTimeOffset? lastBatchRunUtc, DateTimeOffset now)` composing exactly 5 components in this order:
      1. key "homeAssistant", label "Home Assistant (WebSocket)": status ok when signals.HaConnected else error; detail "Connected · {haEntityCount} entities" / "Disconnected".
      2. key "detector", label "Detector (gRPC, mTLS)": status ok when signals.DetectorConnected else warn; detail includes settings.DetectorEndpoint (already public via /api/settings) — e.g. "{endpoint} · serving" / "{endpoint} · unreachable"; when endpoint is null use "not configured".
      3. key "mqtt", label "MQTT broker": status ok when mqttConnected else warn; detail "Connected" / "Disconnected" — do NOT include MQTT host/user/password (not on the /api/settings allowlist).
      4. key "batch", label "Last batch run": delegate to a separate public static testable method `BuildBatchComponent(string? influxUrl, DateTimeOffset? lastRunUtc, int intervalMinutes, DateTimeOffset now)` returning a HealthComponent. Rules: when influxUrl is null/whitespace → status idle, detail "Disabled — streaming-only"; else when lastRunUtc is null → status warn, detail "Not run yet"; else compute minutesSince = (now - lastRunUtc).TotalMinutes and if minutesSince > intervalMinutes * 1.5 → status warn, detail "Overdue by {round(minutesSince - intervalMinutes)} min (interval {intervalMinutes} min)", otherwise status ok, detail "{round(minutesSince)} min ago (interval {intervalMinutes} min)".
      5. key "influx", label "InfluxDB": when influxUrl null/whitespace → status idle, detail "Not configured — streaming-only"; else status ok, detail includes settings.InfluxUrl (already public via /api/settings), optionally with InfluxBucket.
    Build MUST NOT read HaToken, MqttUser, MqttPassword, InfluxToken, or TLS key/cert fields.

    Program.cs: add two GET handlers alongside the existing endpoints, each opening with the standard `if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);` guard, and using Results.Json (web-defaults camelCase serialization is what the SPA types rely on). Add GET /api/health with parameters `(HttpRequest req, ArgusHealthSignals signals, MqttConnection mqtt, IHaSensorRegistry registry, ConnectionSettings settings, IBatchRunStatus batchRunStatus)` returning `Results.Json(HealthProjection.Build(signals, mqtt.IsConnected, registry.GetAll().Count, settings, batchRunStatus.LastRunUtc, DateTimeOffset.UtcNow))`. Add GET /api/anomalies/recent with parameters `(HttpRequest req, IRecentAnomaliesCache cache)` returning `Results.Json(new { anomalies = cache.GetRecent().Select(a => new { entityId = a.EntityId, groupId = a.GroupId, score = a.Score, detector = a.Detector, detectedAtUtc = a.DetectedAtUtc }) })` (GetRecent is already newest-first). Register both before the MapFallbackToFile line.

    Add HealthProjectionTests.cs (xUnit, offline). Target the interesting logic and the wire contract: (a) BuildBatchComponent with null influxUrl → status "idle" + streaming-only detail; (b) null lastRunUtc + configured influx → status "warn" + "Not run yet"; (c) lastRunUtc = now - 3 min, interval 10 → status "ok"; (d) lastRunUtc = now - 20 min, interval 10 → status "warn" + detail contains "Overdue"; (e) full Build with HaConnected=false yields the homeAssistant component status "error" and HomeAssistant.Connected false and EntityCount echoed; (f) a camelCase-contract test: serialize a Build(...) result with `new JsonSerializerOptions(JsonSerializerDefaults.Web)` and assert the JSON contains "homeAssistant", "entityCount", and "components" (lower-first) and does NOT contain any of "HaToken", the MQTT password value you passed in settings, or "InfluxToken" — locking the allowlist + camelCase contract the frontend depends on.
  </action>
  <verify>
    <automated>dotnet test orchestrator/Argus.Orchestrator.sln --filter "FullyQualifiedName~HealthProjectionTests"</automated>
  </verify>
  <done>GET /api/health returns HA connection + entity count + 5 allowlisted health components (no secrets), GET /api/anomalies/recent returns the ring buffer newest-first, both behind IsAuthorizedRequest; HealthProjection batch-overdue logic and the camelCase/no-secret contract are covered by passing tests.</done>
</task>

<task type="auto">
  <name>Task 4: Frontend types + dashboard state loaders + state test</name>
  <files>orchestrator/ui/src/api/types.ts, orchestrator/ui/src/state/dashboard.ts, orchestrator/ui/src/state/dashboard.test.ts</files>
  <action>
    types.ts: add the new contracts matching the endpoints' camelCase JSON. `export type HealthStatus = 'ok' | 'warn' | 'error' | 'idle';` `export interface HealthComponent { key: string; label: string; status: HealthStatus; detail: string; }` `export interface HealthResponse { homeAssistant: { connected: boolean; entityCount: number }; components: HealthComponent[]; }` `export interface RecentAnomaly { entityId: string | null; groupId: string | null; score: number; detector: string; detectedAtUtc: string; }` `export interface RecentAnomaliesResponse { anomalies: RecentAnomaly[]; }`. Keep all existing types unchanged.

    dashboard.ts: keep the existing trackedCount, groupCount, loadError signals. Add `export const health = signal<HealthResponse | null>(null);` and `export const recentAnomalies = signal<RecentAnomaly[] | null>(null);` (null = not loaded / load failed — never a fabricated empty or zero). Import HealthResponse, RecentAnomaliesResponse, RecentAnomaly from ../api/types. Refactor loadDashboard so the three areas degrade independently — one failing endpoint must not blank the others. Implement three internal async helpers, each with its own try/catch, and have `loadDashboard` await `Promise.all([...])` of them: (1) loadCounts keeps the CURRENT behavior exactly — fetch api/sensors + api/groups, set trackedCount/groupCount, and on failure null both counts and set loadError=true (reset loadError=false at its start); (2) loadHealth fetches api/health into the health signal, null on failure; (3) loadRecentAnomalies fetches api/anomalies/recent and sets recentAnomalies to the response.anomalies array (or null on failure). Use the existing apiGet helper with relative paths (no leading slash). loadError stays scoped to the counts (its banner text is about counts); health/anomalies failures surface as null signals rendered as unavailable/empty states by the page.

    dashboard.test.ts (new, vitest, following state/sensors.test.ts): reset all signals in beforeEach and vi.restoreAllMocks in afterEach. Mock client.apiGet with a path-switch implementation (return the sensors, groups, health, or anomalies fixture based on the path argument). Tests: (a) a fully-successful loadDashboard populates trackedCount (count of isTracked entries), groupCount, health, and recentAnomalies, with loadError false; (b) independent degradation — when only the api/health call rejects, health is null but trackedCount/groupCount/recentAnomalies are still populated and loadError stays false; (c) when the api/sensors call rejects, loadError is true and counts are null but a successful health fetch still populates the health signal (proves the areas are decoupled). Encode the decoupling intent in the test names.
  </action>
  <verify>
    <automated>cd orchestrator/ui && npm test -- --run dashboard</automated>
  </verify>
  <done>The API types for health + recent anomalies exist and match the endpoint JSON; dashboard.ts exposes health + recentAnomalies signals and loads all three areas with independent per-area failure handling (counts keep loadError, health/anomalies null on failure); dashboard state tests pass.</done>
</task>

<task type="auto">
  <name>Task 5: DashboardPage renders real data — remove all mocks</name>
  <files>orchestrator/ui/src/components/DashboardPage.tsx</files>
  <action>
    Remove the mock scaffolding entirely: delete the MockAnomaly and MockHealthItem interfaces, the MOCK_ANOMALIES and MOCK_HEALTH consts, and both "Mocked — no ... endpoint yet" info Banners. Keep the same Card / StatusDot / Badge / KpiTile structure, the argus-* class names, and the page header. Import health and recentAnomalies (alongside the existing trackedCount, groupCount, loadError, loadDashboard) from ../state/dashboard and the HealthComponent / RecentAnomaly types from ../api/types. Keep the existing loadError Banner (counts) as-is.

    "Home Assistant" KpiTile: replace the hardcoded value="Connected" hint="mocked — no endpoint yet" with real data — value is health.value ? (health.value.homeAssistant.connected ? 'Connected' : 'Disconnected') : '—', and hint is health.value ? `${health.value.homeAssistant.entityCount} entities` : undefined. Leave the other three KpiTiles (Monitored sensors, Groups, Active group detectors) unchanged — they are out of scope for this task.

    Keep the Severity type and the severityToStatus / severityToBadgeTone helpers, but drive them from score: add `function scoreToSeverity(score: number): Severity { if (score >= 0.8) return 'high'; if (score >= 0.5) return 'med'; return 'low'; }` (cutoffs per research). Add a small `function formatRelative(iso: string): string` helper: compute the delta from Date.now(); < 60s → "just now"; < 60 min → "{n} min ago"; < 24 h → "{n} hr ago"; else "{n} d ago".

    "Recent anomalies" Card: render from recentAnomalies.value. When null → a single muted argus-list-row reading "Couldn't load recent anomalies." When an empty array → a single muted argus-list-row empty state reading "No recent anomalies." Otherwise map each RecentAnomaly to the SAME row markup shape used before: StatusDot status={severityToStatus(scoreToSeverity(a.score))}; the entity/group id (a.entityId ?? a.groupId) in the mono argus-row-entity-id span; a sub-line `${a.entityId ? 'sensor' : 'group'} · ${a.detector} · ${formatRelative(a.detectedAtUtc)}`; and a Badge tone={severityToBadgeTone(scoreToSeverity(a.score))} showing a.score.toFixed(2). Use a stable key such as `${a.entityId ?? a.groupId}-${a.detectedAtUtc}`.

    "System health" Card: render from health.value. When null → a single muted argus-list-row reading "Health status unavailable." Otherwise map health.value.components (HealthComponent[]) to the existing row markup: StatusDot status={h.status} (the backend status union already matches StatusDot), h.label in argus-row-entity-id, h.detail in argus-row-friendly-name, keyed by h.key. Keep loadDashboard() in the mount useEffect.
  </action>
  <verify>
    <automated>cd orchestrator/ui && npm run build</automated>
  </verify>
  <done>DashboardPage contains no MOCK_ANOMALIES/MOCK_HEALTH/MockAnomaly/MockHealthItem and no "mocked — no endpoint" banners; the Home Assistant KPI, System health list, and Recent anomalies list render live data from the dashboard signals with unavailable/empty states; the SPA builds cleanly.</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| SPA browser → GET /api/health, GET /api/anomalies/recent | New read-only endpoints; both reuse the existing IsAuthorizedRequest guard (Supervisor IP / loopback / dev bypass). No new auth surface. |
| Pipeline + batch-worker threads → IRecentAnomaliesCache / IBatchRunStatus → Kestrel threads | In-memory only; concurrent writes (pipeline + batch) and reads (web). RecentAnomaliesCache uses a lock; BatchRunStatus uses Interlocked. |

## STRIDE Threat Register

| Threat ID | Category | Component | Severity | Disposition | Mitigation Plan |
|-----------|----------|-----------|----------|-------------|-----------------|
| T-mbx-01 | Information disclosure | GET /api/health JSON | medium | mitigate | HealthProjection is a field-by-field allowlist (mirrors SettingsProjection D-07): exposes only connection booleans, HA entity count, batch interval, and hosts already public via /api/settings (detectorEndpoint, influxUrl). It never reads HaToken, MqttUser/Password, InfluxToken, or TLS material. A test asserts the serialized JSON contains no secret values. Behind IsAuthorizedRequest. |
| T-mbx-02 | Denial of service | GET /api/anomalies/recent | low | accept | Response is bounded to the 20-entry ring buffer (GetRecent copies a capped list); no unbounded growth, no DB/gRPC call on read. |
| T-mbx-03 | Tampering | dependencies | low | accept | No new npm or NuGet packages. Reuses existing signals, Card/StatusDot/Badge, ConcurrentDictionary/LinkedList/Interlocked already in the project. No supply-chain surface added. |
</threat_model>

<verification>
- `dotnet test orchestrator/Argus.Orchestrator.sln` — full backend suite green (new RecentAnomaliesCache/BatchRunStatus/HealthProjection tests, extended pipeline + group-batch recording tests, no regressions).
- `cd orchestrator/ui && npm test -- --run` — full vitest suite green (dashboard state loaders + decoupling tests, no regressions).
- `cd orchestrator/ui && npm run build` — SPA builds with the de-mocked DashboardPage.
- Manual sanity (optional, not gating): open the Dashboard under live Ingress — the Home Assistant KPI shows the real connection state + entity count, System health lists 5 live components, and Recent anomalies shows real events (or the empty-state row) newest-first.
</verification>

<success_criteria>
- RecentAnomaliesCache (newest-first, 20-entry bounded) and BatchRunStatus are DI-registered singletons.
- Streaming anomalies are recorded only on published + anomalous verdicts; joint-group anomalies recorded on IsAnomaly; last batch-run time is stamped.
- GET /api/health returns HA connection + entity count + 5 allowlisted components (no secrets); GET /api/anomalies/recent returns the buffer newest-first.
- Frontend types + dashboard signals + loaders exist with independent per-area failure handling.
- DashboardPage renders real data for all three areas with no mock arrays/banners remaining.
- Backend and frontend test suites pass; the SPA builds.
</success_criteria>

<output>
Create `.planning/quick/260722-mbx-dashboard-real-data/260722-mbx-SUMMARY.md` when done.
</output>
