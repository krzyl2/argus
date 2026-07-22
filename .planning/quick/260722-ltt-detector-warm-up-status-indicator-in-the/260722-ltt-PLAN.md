---
phase: 260722-ltt
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs
  - orchestrator/Argus.Orchestrator/Detection/EntityStatusCache.cs
  - orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator.Tests/EntityRuntimeStateTests.cs
  - orchestrator/Argus.Orchestrator.Tests/EntityStatusCacheTests.cs
  - orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs
  - orchestrator/ui/src/api/types.ts
  - orchestrator/ui/src/components/DetectorListRow.tsx
  - orchestrator/ui/src/components/DetectorsPage.tsx
  - orchestrator/ui/src/components/DetectorListRow.test.tsx
  - orchestrator/ui/src/components/DetectorsPage.test.tsx
autonomous: true
requirements: [QUICK-warmup-status]

must_haves:
  truths:
    - "On /detectors, a tracked sensor row shows 'Rozgrzewka N/250' while warming and 'Działa' once warmed up."
    - "The reading count advances live (~5s polling) with no manual refresh and no HA restart."
    - "Untracked sensors and group rows never show a warm-up chip."
    - "GET /api/sensors returns warmedUp/readingCount/warmUpWindow for tracked entities only; null for untracked."
  artifacts:
    - orchestrator/Argus.Orchestrator/Detection/EntityStatusCache.cs
    - orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/ui/src/components/DetectorListRow.tsx
    - orchestrator/ui/src/components/DetectorsPage.tsx
  key_links:
    - "ScoreStreamPipeline write-loop -> IEntityStatusCache.Set (per reading) keyed by entityId"
    - "GET /api/sensors -> IEntityStatusCache.Get (tracked-only) -> SensorEntry status fields"
    - "DetectorsPage 5s poll -> loadSensors('') -> sensors signal -> detectorRows -> SensorRow chip"
---

<objective>
Show a per-detector warm-up status chip on the unified /detectors list: "Rozgrzewka {readingCount}/{warmUpWindow}" while an HST-tracked sensor is still calibrating, "Działa" once warmed up. Backend exposes per-entity warm-up state via an in-memory cache mirroring the IGroupStatusCache precedent; the /api/sensors projection surfaces it for tracked entities only; the SPA renders the chip and light-polls every ~5s so the count advances live.

Purpose: Operators currently have no visibility into HST warm-up progress — the binary_sensor flag is silently suppressed until an entity reaches its Window (default 250) readings. This surfaces that state so an operator knows a freshly-tracked sensor is calibrating rather than broken.

Output: Public warm-up getters on EntityRuntimeState, a new EntityStatusCache singleton fed by the pipeline, extended /api/sensors JSON, an extended SensorEntry type, a status chip on the sensor list row, and 5s polling on the Detectors page — with backend + frontend tests.
</objective>

<execution_context>
@$HOME/.claude/gsd-core/workflows/execute-plan.md
@$HOME/.claude/gsd-core/templates/summary.md
</execution_context>

<context>
@.planning/STATE.md
@orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs
@orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs
@orchestrator/Argus.Orchestrator/Batch/GroupStatusCache.cs
@orchestrator/Argus.Orchestrator/Program.cs
@orchestrator/Argus.Orchestrator/Web/SensorTracking.cs
@orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs
@orchestrator/ui/src/components/DetectorListRow.tsx
@orchestrator/ui/src/components/DetectorsPage.tsx
@orchestrator/ui/src/components/Badge.tsx
@orchestrator/ui/src/state/sensors.ts
@orchestrator/ui/src/state/detectors.ts
@orchestrator/ui/src/api/types.ts
@orchestrator/ui/src/components/DetectorListRow.test.tsx
</context>

<tasks>

<task type="auto">
  <name>Task 1: Expose warm-up getters + EntityStatusCache fed by the pipeline</name>
  <files>orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs, orchestrator/Argus.Orchestrator/Detection/EntityStatusCache.cs, orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs, orchestrator/Argus.Orchestrator.Tests/EntityRuntimeStateTests.cs, orchestrator/Argus.Orchestrator.Tests/EntityStatusCacheTests.cs</files>
  <action>
    In EntityRuntimeState.cs expose the two currently-private warm-up fields as public read-only getters: add `public int ReadingCount => _readingCount;` and `public int WarmUpWindow => _warmUpWindow;`. Leave `_readingCount`/`_warmUpWindow` fields, `WarmedUp`, and `RecordReading()` exactly as-is (do not change increment or warm-up semantics).

    Create EntityStatusCache.cs in namespace Argus.Orchestrator.Detection mirroring Batch/GroupStatusCache.cs one-for-one. Define an immutable record `EntityStatusEntry` with members EntityId (string), WarmedUp (bool), ReadingCount (int), WarmUpWindow (int). Define interface IEntityStatusCache with `EntityStatusEntry? Get(string entityId)` and `void Set(EntityStatusEntry entry)`. Implement EntityStatusCache with a `ConcurrentDictionary<string, EntityStatusEntry>` using StringComparer.OrdinalIgnoreCase; Get returns the entry or null; Set stores keyed by entry.EntityId — identical thread-safety shape to GroupStatusCache (single writer = pipeline thread, many readers = Kestrel).

    In ScoreStreamPipeline.cs inject the cache without breaking either constructor: add a trailing optional parameter `IEntityStatusCache? statusCache = null` to BOTH the production constructor and the test constructor, store it in a new `private readonly IEntityStatusCache? _statusCache;` field. DI (registered in Task 2) fills the production constructor's optional param automatically. In the RunAsync(call, entityId, readings, entityState, ct) write loop, immediately after the existing `entityState.RecordReading();` call, publish a snapshot: `_statusCache?.Set(new EntityStatusEntry(entityId, entityState.WarmedUp, entityState.ReadingCount, entityState.WarmUpWindow));`. Null-conditional keeps existing tests that construct the pipeline without a cache working unchanged.

    Add EntityRuntimeStateTests.cs (xUnit, namespace Argus.Orchestrator.Tests): construct EntityRuntimeState with an HstParams whose Window is a known value (e.g. 3 for a fast test, and separately assert the default 250 via `new HstParams()`); assert WarmUpWindow equals the configured Window; assert ReadingCount starts at 0, increments by one per RecordReading(), and that WarmedUp flips from false to true exactly when ReadingCount reaches WarmUpWindow.

    Add EntityStatusCacheTests.cs mirroring GroupStatusCacheTests.cs: Get on an unknown id returns null; Set then Get round-trips all four fields; Set replaces a prior entry for the same id; keys are case-insensitive (Set "sensor.X", Get "SENSOR.X" returns the entry).
  </action>
  <verify>
    <automated>dotnet test orchestrator/Argus.Orchestrator.sln --filter "FullyQualifiedName~EntityRuntimeStateTests|FullyQualifiedName~EntityStatusCacheTests"</automated>
  </verify>
  <done>EntityRuntimeState exposes public ReadingCount/WarmUpWindow; EntityStatusCache singleton exists and round-trips EntityStatusEntry; the pipeline write loop populates the cache per reading via null-safe Set; new backend tests pass and the solution still builds.</done>
</task>

<task type="auto">
  <name>Task 2: Register cache in DI + project warm-up status in GET /api/sensors</name>
  <files>orchestrator/Argus.Orchestrator/Program.cs, orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs</files>
  <action>
    In Program.cs register the new cache as a singleton directly after the IGroupStatusCache registration (currently line ~93): add `builder.Services.AddSingleton<IEntityStatusCache, EntityStatusCache>();`. The Argus.Orchestrator.Detection namespace is already imported. This registration is what makes DI fill ScoreStreamPipeline's optional cache param with the same singleton the endpoint reads.

    Extend the GET /api/sensors handler (currently ~lines 248-279). Add an `IEntityStatusCache statusCache` parameter to the lambda so minimal-API injects it. Keep isTracked sourced from SensorTracking.TrackedIds(liveCfg.Get()) exactly as today — do NOT regress the G-14-1 config-sourced tracked derivation. Inside the Select, compute `var tracked = trackedIds.Contains(e.EntityId);` (reuse the value you already assign to isTracked). Look up status ONLY for tracked entities: `var status = tracked ? statusCache.Get(e.EntityId) : null;`. Add three fields to the projected anonymous object: `warmedUp = status?.WarmedUp` (bool?), `readingCount = status?.ReadingCount` (int?), `warmUpWindow = status?.WarmUpWindow` (int?). For untracked entities status is null so all three serialize as null; for a tracked entity the pipeline has not scored yet, status is also null (chip simply will not render until the first reading) — this is acceptable MVP behavior.

    Update SensorsEndpointJsonTests.cs so its inline ProjectEntries mirror matches the handler. Add a trailing optional parameter to the helper: `IEntityStatusCache? cache = null`, defaulting so existing 2-arg call sites keep compiling. In the helper's Select, replicate the handler: compute tracked from trackedIds, then `var status = tracked ? cache?.Get(e.EntityId) : null;` and project warmedUp/readingCount/warmUpWindow the same way. Add tests: (a) a tracked-in-config entity plus a cache holding EntityStatusEntry(id, WarmedUp:false, ReadingCount:100, WarmUpWindow:250) projects warmedUp==false, readingCount==100, warmUpWindow==250; (b) same entity with WarmedUp:true projects warmedUp==true; (c) an entity absent from config (untracked) projects warmedUp/readingCount/warmUpWindow all null even when a cache entry exists for it; (d) a tracked entity with an empty cache projects all three null. Use a real EntityStatusCache instance in these tests (Set the entries), not a fake.
  </action>
  <verify>
    <automated>dotnet test orchestrator/Argus.Orchestrator.sln --filter "FullyQualifiedName~SensorsEndpointJsonTests"</automated>
  </verify>
  <done>IEntityStatusCache is registered as a singleton next to IGroupStatusCache; GET /api/sensors emits warmedUp/readingCount/warmUpWindow for tracked entities only (null otherwise) while isTracked stays config-sourced; the test mirror is updated and all SensorsEndpointJsonTests pass.</done>
</task>

<task type="auto">
  <name>Task 3: SensorEntry status fields + warm-up chip + 5s polling on Detectors page</name>
  <files>orchestrator/ui/src/api/types.ts, orchestrator/ui/src/components/DetectorListRow.tsx, orchestrator/ui/src/components/DetectorsPage.tsx, orchestrator/ui/src/components/DetectorListRow.test.tsx, orchestrator/ui/src/components/DetectorsPage.test.tsx</files>
  <action>
    In api/types.ts extend the SensorEntry interface with three optional fields: `warmedUp?: boolean | null;`, `readingCount?: number | null;`, `warmUpWindow?: number | null;`. Keep existing fields unchanged. These map to the null-or-value JSON the endpoint now returns.

    In DetectorListRow.tsx add a warm-up chip to the SensorRow variant only (leave GroupRow untouched). Render the chip inside the existing `argus-row-meta` div, before the existing tracked Badge, and only when status data is present — gate on `entry.readingCount != null && entry.warmUpWindow != null` (the `!= null` check covers both null and undefined). When present: if `entry.warmedUp` render `<Badge tone="ok">Działa</Badge>`, otherwise render a warm-up Badge with tone="warn" whose text is the word Rozgrzewka followed by a space then `{entry.readingCount}/{entry.warmUpWindow}`. Reuse the existing Badge component (tones ok and warn already exist in Badge.tsx). Do not alter the tracked Badge or the Edit link. The Polish chip labels are per the task spec (user-facing strings are Polish; identifiers stay English).

    In DetectorsPage.tsx add light polling inside the existing mount useEffect. Keep the current mount calls (loadGroups(); loadSensors('')). After them, create `const id = setInterval(() => { loadSensors(''); }, 5000);` and return a cleanup `() => clearInterval(id);` from the effect. Poll loadSensors('') only (full-set, empty query — same as mount) so warm-up counts advance; do not poll loadGroups (out of scope). Keep the dependency array empty so the interval is created once and cleared on unmount.

    Extend DetectorListRow.test.tsx: add sensor-variant cases using the existing makeSensor helper — (a) warming: makeSensor({ warmedUp: false, readingCount: 100, warmUpWindow: 250 }) renders text matching Rozgrzewka 100/250 (use a regex matcher like /Rozgrzewka\s*100\/250/ to be robust against split text nodes) and does NOT render Działa; (b) warmed: makeSensor({ warmedUp: true, readingCount: 250, warmUpWindow: 250 }) renders Działa and no Rozgrzewka; (c) no-status: default makeSensor() with the fields undefined renders neither chip; (d) confirm the existing group variant renders neither Rozgrzewka nor Działa.

    Add DetectorsPage.test.tsx verifying the polling wiring deterministically (avoid fake timers — prior phases hit vitest/preact microtask desync with vi.advanceTimersByTime). Partial-mock the state modules so real fetches never fire while preserving the signals the detectors computed depends on: `vi.mock('../state/sensors', async (orig) => ({ ...(await orig()), loadSensors: vi.fn() }))` and the equivalent for '../state/groups' mocking loadGroups. Spy on `globalThis.setInterval` and `globalThis.clearInterval`. Render DetectorsPage and assert loadSensors was called on mount and setInterval was called with a 5000 ms delay; then unmount and assert clearInterval was called with the id setInterval returned. Restore spies/mocks in afterEach.
  </action>
  <verify>
    <automated>cd orchestrator/ui && npm test -- --run DetectorListRow DetectorsPage</automated>
  </verify>
  <done>SensorEntry carries optional warmedUp/readingCount/warmUpWindow; the sensor row shows "Rozgrzewka N/250" while warming and "Działa" once warmed, with no chip for untracked-status or group rows; DetectorsPage sets a 5s loadSensors('') interval cleared on unmount; DetectorListRow and DetectorsPage tests pass.</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| SPA browser → GET /api/sensors | Reuses the existing IsAuthorizedRequest guard (Supervisor IP / loopback / dev bypass). No new endpoint, no new auth surface. |
| Pipeline thread → IEntityStatusCache → Kestrel thread | In-memory only; concurrent writes (pipeline) and reads (web) via ConcurrentDictionary, same pattern as GroupStatusCache. |

## STRIDE Threat Register

| Threat ID | Category | Component | Severity | Disposition | Mitigation Plan |
|-----------|----------|-----------|----------|-------------|-----------------|
| T-ltt-01 | Information disclosure | GET /api/sensors status fields | low | mitigate | Warm-up counters are projected for tracked entities only (untracked → null); no new data class beyond an internal reading counter, behind the pre-existing IsAuthorizedRequest guard. |
| T-ltt-02 | Denial of service | 5s SPA polling loop | low | accept | Single lightweight interval per open page, cleared on unmount; reads an in-memory snapshot (no gRPC/DB call). Consistent with existing AttributionPanel 60s polling precedent. |
| T-ltt-03 | Tampering | dependencies | low | accept | No new npm/NuGet packages installed; reuses Badge, signals, ConcurrentDictionary already in the project. No supply-chain surface added. |
</threat_model>

<verification>
- `dotnet test orchestrator/Argus.Orchestrator.sln` — full backend suite green (new EntityRuntimeState/EntityStatusCache tests + updated SensorsEndpointJson tests).
- `cd orchestrator/ui && npm test -- --run` — full vitest suite green (DetectorListRow chip + DetectorsPage polling, no regressions).
- Manual sanity (optional, not gating): with a freshly-tracked HST sensor, /detectors shows "Rozgrzewka N/250" and N climbs across ~5s refreshes, flipping to "Działa" at 250.
</verification>

<success_criteria>
- EntityRuntimeState.ReadingCount and .WarmUpWindow are public and correct.
- IEntityStatusCache is DI-registered and populated by the pipeline per reading.
- GET /api/sensors returns warmedUp/readingCount/warmUpWindow for tracked entities only; null for untracked; isTracked stays config-sourced (G-14-1 not regressed).
- The Detectors sensor row shows the Polish warm-up/working chip; group and no-status rows are unaffected.
- DetectorsPage polls loadSensors('') every 5s and clears the interval on unmount.
- Both test suites pass.
</success_criteria>

<output>
Create `.planning/quick/260722-ltt-detector-warm-up-status-indicator-in-the/260722-ltt-SUMMARY.md` when done.
</output>
