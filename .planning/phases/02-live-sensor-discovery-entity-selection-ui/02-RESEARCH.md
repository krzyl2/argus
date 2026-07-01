# Phase 2: Live Sensor Discovery + Entity Selection UI - Research

**Researched:** 2026-07-01
**Domain:** ASP.NET Minimal API, htmx 2.0.10, YamlDotNet 16.3.0, HA WebSocket state shape, IHaSensorRegistry design
**Confidence:** HIGH

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- Flat list with client-side text search on `entity_id` (SC1). No domain/device_class grouping.
- Each row shows: `entity_id` + current value + unit of measurement (+ `friendly_name` when present).
- Tracked vs untracked distinction: checkbox reflects tracked state AND tracked rows carry a "tracked" pill/badge (SC2). Single list, not split sections.
- Selection is per-row checkboxes committed by an explicit **Save** button (atomic batch write via the Phase-1 `ConfigWriter`). No instant-save-per-toggle.
- Pattern syntax: glob on `entity_id` (fnmatch-style, e.g. `sensor.*temp*`) — matches HA include/exclude conventions.
- Combine model (authoritative): resolved tracked set = `(entities matching include-globs − entities matching exclude-globs) ∪ manually-checked − manually-unchecked`. Manual selection overrides patterns.
- Persist BOTH the raw patterns AND the resolved concrete entity list, so re-opening the UI shows the patterns and the resulting selection (round-trips).
- Patterns are expanded server-side at save time into the concrete entity list written to `entities.yaml` (SC4).
- Save writes the resolved `entities.yaml` via the Phase-1 atomic `ConfigWriter` (temp + `File.Move(overwrite)` + `SemaphoreSlim`). Raw patterns are stored as metadata (top-level `_patterns:` block or sidecar — planner decides exact shape, must not break `EntitiesConfigLoader`).
- gen-entities.py restart guard (SC5): a `.ui_config_present` lock file in `/data`. `10-config-gen.sh` checks for it and SKIPS regeneration when present; the UI save creates it. This guard MUST land before the first save endpoint is wired.
- Newly-selected entities default to the `hst` detector with default params.
- Interim Ingress auth: accept connections from the Supervisor IP `172.30.32.2` in Phase 2; the full `validate_session` middleware is deferred to Phase 4.

### Claude's Discretion
- Exact htmx interaction wiring, endpoint routes, and the on-disk shape of the persisted patterns metadata (must remain backward-compatible with `EntitiesConfigLoader`).

### Deferred Ideas (OUT OF SCOPE)
- Detector assignment + editable params + live config hot-reload → Phase 3.
- Full `validate_session` Ingress auth middleware → Phase 4.
- Server-side / client-side input validation polish, CI packaging, DOCS.md → Phase 4.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| UI-02 | The UI lists live HA numeric sensors (reusing `get_states` + `SelectDiscoverableSensors`), filterable, and lets the user select which entities Argus tracks. | IHaSensorRegistry design (§1), GET /api/sensors endpoint (§2), htmx search pattern (§7) |
| CFG-02 | Entity selection (incl. `include_patterns`/`exclude_patterns` honored as filters) persists to the config and is consumed by the orchestrator — replacing the manual `entities` list and closing the v2.0 patterns-ignored gap. | Glob matching in .NET 8 (§3), patterns metadata in YAML (§4), gen-entities.py guard (§5), pipeline pickup (§6) |
</phase_requirements>

---

## Summary

Phase 2 adds three cooperating capabilities on top of Phase 1's Kestrel + htmx foundation: (1) a thread-safe `IHaSensorRegistry` singleton that caches the live HA sensor snapshot from the existing `NetDaemonHaEventSource`; (2) a three-endpoint Minimal API (GET `/sensors` full page, GET `/api/sensors` htmx fragment, POST `/api/sensors/save`); and (3) a gen-entities.py restart guard that must land before the save endpoint goes live.

The primary complexity areas are: extending `HaStateDto` to capture `unit_of_measurement` and `friendly_name` from HA state attributes (the existing record does not parse the `attributes` object); designing the registry so `NetDaemonHaEventSource` populates it on each `get_states` call without opening a second WebSocket; choosing the safe shape for persisting include/exclude patterns alongside the entities list in `entities.yaml` without breaking `EntitiesConfigLoader`; and wiring the restart guard before the first write can occur.

The pipeline does NOT hot-reload in Phase 2. A UI save writes a new `entities.yaml` and creates the lock file; the new config takes effect on the next add-on restart (or after Phase 3 adds the inner-CTS loop). The UI subheading "Changes take effect on the next pipeline cycle" accurately describes this behavior.

**Primary recommendation:** Implement `HaSensorEntry` record + `HaSensorRegistry` singleton; extend `HaStateDto` with `UnitOfMeasurement` and `FriendlyName`; persist patterns in a top-level `_patterns:` key (safe because `IgnoreUnmatchedProperties()` is already set on the deserializer); use `FileSystemName.MatchesSimpleExpression(ignoreCase: true)` for glob matching; add the lock-file guard to `10-config-gen.sh` before wiring the save endpoint.

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Sensor snapshot / registry | Backend singleton (in-process) | — | Populated by existing WebSocket worker; no extra transport needed |
| Entity picker UI (list, search, filter) | Frontend server (SSR) | htmx fragment swap | Server renders list; htmx swaps fragments on search — no client-side state |
| Pattern expand (glob → entity set) | Backend (save endpoint) | — | Server is source of truth per SC4; client may show preview but does not own resolution |
| Config persistence | Backend (ConfigWriter) | Filesystem (/data) | Atomic writer from Phase 1; YAML format unchanged |
| Restart guard | Add-on init script | Backend (save creates lock) | `10-config-gen.sh` (cont-init.d) checks for lock; orchestrator creates it |
| Ingress auth (interim) | Backend middleware | — | Source IP check on `X-Forwarded-For` / `RemoteIpAddress`; full auth Phase 4 |

---

## Standard Stack

### Core (all already in project — no new packages)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| YamlDotNet | 16.3.0 (pinned) | Serialize/deserialize entities.yaml | Already in csproj; only YAML lib in scope |
| htmx | 2.0.10 (committed) | Fragment swaps, form POST, live search | Already in wwwroot/js; BSD 0-Clause; no build step |
| System.IO.Enumeration.FileSystemName | Built-in (net8.0) | Glob matching for include/exclude patterns | Zero-dep; verified working; supports `*` and `?` |
| Microsoft.AspNetCore.Http | Built-in (ASP.NET Core 8) | Minimal API request/response | Already in use via WebApplication |

### No New Dependencies
Phase 2 adds zero new NuGet packages. All required capabilities are covered by existing dependencies or the .NET 8 BCL. This is intentional (CLAUDE.md: "no new deps preferred").

---

## Architecture Patterns

### System Architecture Diagram

```
  HA WebSocket (supervisor proxy)
         │
         ▼
  NetDaemonHaEventSource.RunConnectionLoopAsync()
         │
         ├─── GetStatesAsync() ──────────► HaSensorRegistry.UpdateSnapshot(states)
         │    [on every connect]                    │ (IHaSensorRegistry)
         │                                          │
         ▼                                          ▼
  HaListenerWorker → ScoreStreamPipeline    GET /sensors  ──► EntityPickerPage.Build()
                                            GET /api/sensors    (reads registry snapshot)
                                                   │
                                                   ▼ (htmx fragment)
                                             Browser (checkbox state)
                                                   │
                                                   ▼ POST /api/sensors/save
                                            GlobExpander.Resolve(patterns, snapshot)
                                                   │
                                             ConfigWriter.WriteAsync()  ─► /data/entities.yaml
                                                   │
                                             File.WriteAllText(".ui_config_present")
                                                   │
                                             return .argus-banner--success
```

### Recommended Project Structure (new files only)

```
orchestrator/Argus.Orchestrator/
├── Ha/
│   ├── HaWebSocketClient.cs        [EXTEND: add UnitOfMeasurement, FriendlyName to HaStateDto and GetStatesAsync]
│   ├── IHaSensorRegistry.cs        [NEW: interface — GetAll(), GetFiltered(q)]
│   └── HaSensorRegistry.cs         [NEW: thread-safe singleton implementation]
├── Config/
│   └── GlobExpander.cs             [NEW: pure static — expand include/exclude patterns against a snapshot]
└── Web/
    └── EntityPickerPage.cs         [NEW: server-render full page + list fragment + banner fragment]
```

Note: `Program.cs` gains three new endpoint registrations and the `HaSensorRegistry` singleton registration.

---

## Key Research Findings (8 questions)

### 1. IHaSensorRegistry Design

**Finding (VERIFIED: codebase):**

`NetDaemonHaEventSource.RunConnectionLoopAsync` calls `client.GetStatesAsync(ct)` on EVERY connect (first connect + every reconnect). The result is an `IReadOnlyList<HaStateDto>`. Currently `HaStateDto` is:

```csharp
internal sealed record HaStateDto(string EntityId, string? State, DateTime LastChangedUtc);
```

The HA WebSocket `get_states` result payload has an `attributes` object that contains `unit_of_measurement` and `friendly_name`, but `HaWebSocketClient.GetStatesAsync` currently does NOT parse `attributes`. This must be extended.

**Required extension to `HaStateDto`:**

```csharp
// EXTEND: add two nullable fields (both optional in HA responses)
internal sealed record HaStateDto(
    string EntityId,
    string? State,
    DateTime LastChangedUtc,
    string? UnitOfMeasurement,   // from attributes.unit_of_measurement
    string? FriendlyName);        // from attributes.friendly_name
```

**Required extension to `HaWebSocketClient.GetStatesAsync`** (parse `attributes`):

```csharp
// Inside the foreach (var st in arr.EnumerateArray()) loop:
string? unit = null;
string? friendlyName = null;
if (st.TryGetProperty("attributes", out var attrs))
{
    if (attrs.TryGetProperty("unit_of_measurement", out var u)) unit = u.GetString();
    if (attrs.TryGetProperty("friendly_name", out var fn)) friendlyName = fn.GetString();
}
list.Add(new HaStateDto(entityId, state, ParseUtc(st, "last_changed"), unit, friendlyName));
```

**IHaSensorRegistry design:**

```csharp
public interface IHaSensorRegistry
{
    /// <summary>All cached numeric-sensor entries, ordered by entity_id.</summary>
    IReadOnlyList<HaSensorEntry> GetAll();

    /// <summary>Entries whose entity_id contains <paramref name="q"/> (case-insensitive).</summary>
    IReadOnlyList<HaSensorEntry> GetFiltered(string q);

    /// <summary>Replaces the snapshot. Thread-safe — called by NetDaemonHaEventSource on connect.</summary>
    void UpdateSnapshot(IReadOnlyList<HaStateDto> states, HashSet<string> trackedEntityIds);
}

public record HaSensorEntry(
    string EntityId,
    double CurrentValue,
    string? UnitOfMeasurement,
    string? FriendlyName,
    bool IsTracked);
```

**Thread-safety:** The registry is written by `NetDaemonHaEventSource` (worker thread) and read by the HTTP endpoints (Kestrel thread-pool threads). Use `lock` or `Interlocked.Exchange` on a `volatile` reference to an immutable array.

**Recommended implementation pattern:**

```csharp
public sealed class HaSensorRegistry : IHaSensorRegistry
{
    private volatile IReadOnlyList<HaSensorEntry> _snapshot = Array.Empty<HaSensorEntry>();

    public IReadOnlyList<HaSensorEntry> GetAll() => _snapshot;

    public IReadOnlyList<HaSensorEntry> GetFiltered(string q) =>
        string.IsNullOrWhiteSpace(q)
            ? _snapshot
            : _snapshot.Where(e => e.EntityId.Contains(q, StringComparison.OrdinalIgnoreCase))
                       .ToList();

    public void UpdateSnapshot(IReadOnlyList<HaStateDto> states, HashSet<string> trackedEntityIds)
    {
        var entries = states
            .Where(s => double.TryParse(s.State, NumberStyles.Any,
                CultureInfo.InvariantCulture, out _))
            .Select(s =>
            {
                double.TryParse(s.State, NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var value);
                return new HaSensorEntry(
                    s.EntityId,
                    value,
                    s.UnitOfMeasurement,
                    s.FriendlyName,
                    trackedEntityIds.Contains(s.EntityId));
            })
            .OrderBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _snapshot = entries; // volatile write — atomic reference swap
    }
}
```

**Where to call `UpdateSnapshot`:** `NetDaemonHaEventSource.RunConnectionLoopAsync` already calls `client.GetStatesAsync(ct)` before the if/else (first connect vs reconnect). After that call, inject `_sensorRegistry.UpdateSnapshot(states, _configuredEntities)`. This populates the registry on every connect without any second WebSocket (ADR-4 compliant).

**Numeric filter logic:** Reuse the same `double.TryParse(state, NumberStyles.Any, CultureInfo.InvariantCulture, out _)` from `SelectDiscoverableSensors`. Do NOT filter by domain (`sensor.`) — the existing logic passes any entity whose state is a parseable double, which is the correct behavior for all numeric sensors.

**Is registry pre-populated before the UI starts?** The `HaListenerWorker` waits for `_gateway.WaitForHealthyAsync(stoppingToken)` before calling `GetStatesAsync`. If the user opens the UI before HA connects, `GetAll()` returns an empty list and the UI shows the "No sensors found" empty state. This is correct behavior documented in `02-UI-SPEC.md`.

---

### 2. Minimal API Endpoints in Program.cs

**Finding (VERIFIED: codebase — Program.cs):**

Phase 1 registered one endpoint: `app.MapGet("/", ...)`. Phase 2 adds three endpoints. The X-Ingress-Path PathBase middleware runs before routing, so all endpoints are served under the correct ingress prefix automatically.

**Endpoints to add:**

```csharp
// GET /sensors — full picker page (replaces placeholder)
app.MapGet("/sensors", (HttpRequest req, IHaSensorRegistry registry,
    EntitiesConfig config, ArgusHealthSignals health) =>
{
    var ip = req.Headers["X-Ingress-Path"].FirstOrDefault() ?? "";
    var q = req.Query["q"].FirstOrDefault() ?? "";
    return Results.Content(
        EntityPickerPage.BuildFullPage(ip, registry, config, health, q), "text/html");
});

// GET /api/sensors — htmx fragment (list rows only, no shell)
app.MapGet("/api/sensors", (HttpRequest req, IHaSensorRegistry registry, EntitiesConfig config) =>
{
    var q = req.Query["q"].FirstOrDefault() ?? "";
    return Results.Content(
        EntityPickerPage.BuildListFragment(registry, config, q), "text/html");
});

// POST /api/sensors/save — save selection + patterns
app.MapPost("/api/sensors/save", async (HttpRequest req, IHaSensorRegistry registry,
    EntitiesConfig config, ConfigWriter writer, ConnectionSettings settings,
    ILogger<Program> logger, CancellationToken ct) =>
{
    // parse form, expand globs, write YAML, create lock file
    // return banner fragment
});
```

**Route naming note:** The UI-SPEC defines routes as `/sensors` and `/api/sensors`. The Phase 1 placeholder is at `/`. Phase 2 should keep `GET /` pointing at the placeholder (or redirect to `/sensors`) to avoid a regression when accessing the root.

**PathBase behavior:** The X-Ingress-Path middleware already sets `ctx.Request.PathBase` before routing, so `LinkGenerator`, `UseStaticFiles`, and form action URLs are all relative to the ingress prefix. No additional changes needed.

---

### 3. Glob Matching in .NET 8 Without New Deps

**Finding (VERIFIED: live test + official docs):**

`System.IO.Enumeration.FileSystemName.MatchesSimpleExpression` is available in `net8.0` (BCL, no NuGet). It supports `*` (any sequence) and `?` (any single character) wildcards. Backslash `\` escapes. No `[...]` bracket patterns. For HA entity_id patterns like `sensor.*temp*`, `*humidity*`, `sensor.outdoor_*` — this is sufficient.

**Verified behavior (live test):**

| Pattern | Entity | Match |
|---------|--------|-------|
| `sensor.*temp*` | `sensor.living_room_temp` | `True` |
| `sensor.*temp*` | `sensor.outdoor_humidity` | `False` |
| `sensor.*temp*` | `sensor.LIVING_ROOM_TEMP` | `True` (ignoreCase=true) |
| `sensor.*` | `sensor.outdoor_humidity` | `True` |
| `sensor.*` | `binary_sensor.something` | `False` |
| `*humidity*` | `sensor.outdoor_humidity` | `True` |

**Case sensitivity:** Pass `ignoreCase: true` (the default). HA entity_ids are conventionally lowercase, but the UI spec does not restrict input, so case-insensitive matching is safer and matches HA conventions.

**Recommended helper:**

```csharp
// GlobExpander.cs — pure static, no deps
public static class GlobExpander
{
    /// <summary>
    /// Applies include/exclude glob patterns to a sensor snapshot.
    /// Returns entity_ids matching: (include matches) − (exclude matches) ∪ manuallyChecked − manuallyUnchecked.
    /// All matching is case-insensitive (ignoreCase: true).
    /// </summary>
    public static HashSet<string> Resolve(
        IReadOnlyList<HaSensorEntry> snapshot,
        IEnumerable<string> includePatterns,
        IEnumerable<string> excludePatterns,
        IEnumerable<string> manuallyChecked,
        IEnumerable<string> manuallyUnchecked)
    {
        var allIds = snapshot.Select(e => e.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includes = includePatterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var excludes = excludePatterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        // Pattern-selected set
        HashSet<string> patternSelected;
        if (includes.Count == 0)
        {
            // No include patterns: all sensors are candidates
            patternSelected = new HashSet<string>(allIds, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            patternSelected = allIds
                .Where(id => includes.Any(p =>
                    FileSystemName.MatchesSimpleExpression(p, id, ignoreCase: true)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Remove exclude matches
        foreach (var id in allIds.Where(id =>
            excludes.Any(p => FileSystemName.MatchesSimpleExpression(p, id, ignoreCase: true))))
            patternSelected.Remove(id);

        // Apply manual overrides: manual check overrides exclusion; manual uncheck overrides inclusion
        foreach (var id in manuallyChecked) patternSelected.Add(id);
        foreach (var id in manuallyUnchecked) patternSelected.Remove(id);

        return patternSelected;
    }
}
```

**Source:** [CITED: learn.microsoft.com/en-us/dotnet/api/system.io.enumeration.filesystemname.matchessimpleexpression]

---

### 4. Persisting Include/Exclude Patterns Without Breaking EntitiesConfigLoader

**Finding (VERIFIED: codebase — EntitiesConfigLoader.cs line 24):**

`EntitiesConfigLoader` uses:
```csharp
var deserializer = new DeserializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .IgnoreUnmatchedProperties()   // <-- already set
    .Build();
```

`.IgnoreUnmatchedProperties()` tells YamlDotNet to silently skip any top-level key that does not map to a property on `EntitiesConfig`. This is confirmed to be set on line 24 of `EntitiesConfigLoader.cs`.

**Recommended shape — top-level `_patterns:` key:**

Add a `_patterns:` block at the top of `entities.yaml`. Because the field name starts with `_` and `EntitiesConfig` has no matching property, YamlDotNet will skip it silently. The orchestrator is unaffected.

```yaml
# Written by the UI save endpoint. Read back at GET /sensors to populate pattern fields.
_patterns:
  include:
    - sensor.*temp*
    - sensor.*humidity*
  exclude:
    - sensor.*test*

entities:
  - entity_id: sensor.living_room_temp
    friendly_name: Salon temperatura
    detectors:
      - name: hst
        params: {}
  - entity_id: sensor.outdoor_humidity
    friendly_name: Zewnatrz wilgotnosc
    detectors:
      - name: hst
        params: {}
```

**Alternative considered — sidecar file `/data/entities_meta.yaml`:** Would avoid any YAML co-mingling but requires a second file write and two-file round-trip logic. Rejected: the `_patterns:` key is cleaner and avoids the stale-sidecar problem.

**Reading `_patterns:` back in the UI:** The save endpoint writes `_patterns:` into the YAML verbatim. On `GET /sensors`, the full-page handler needs to re-read `entities.yaml` to populate the pattern textareas. Either add a `PatternsMeta` deserializer class that reads only `_patterns:`, or store patterns in a separate in-memory field on the registry populated at startup and updated on each save. The in-memory approach (registry holds last-known patterns) avoids a second file read on every page load and is simpler.

**YAML serialization:** Use `YamlDotNet.Serialization.SerializerBuilder` with `UnderscoredNamingConvention` to produce the YAML string passed to `ConfigWriter.WriteAsync`. The `ConfigWriter` writes verbatim strings; YAML construction is the caller's responsibility (already noted in ConfigWriter.cs comments: "YAML serialization is a Phase 2+ caller concern").

---

### 5. gen-entities.py Restart Guard

**Finding (VERIFIED: codebase — 10-config-gen.sh line 112):**

The current final line is:
```bash
python3 /usr/local/bin/gen-entities.py /data/options.json > /data/entities.yaml
```

This unconditionally overwrites `/data/entities.yaml` on every add-on boot. If the user saves via the UI and then restarts the add-on, this line erases their config.

**Exact edit required:**

Replace the last generation line (and the comment above it) with a guard block:

```bash
# ── entities.yaml Generation (UICFG-08) ────────────────────────────────────
printf "/data/entities.yaml" > /var/run/s6/container_environment/ARGUS_ENTITIES_PATH
if [ -f /data/.ui_config_present ]; then
    bashio::log.info "UI config present — skipping gen-entities.py (entities.yaml preserved)."
else
    python3 /usr/local/bin/gen-entities.py /data/options.json > /data/entities.yaml
fi
```

**Lock file creation in the orchestrator:** The save endpoint creates `/data/.ui_config_present` after a successful `ConfigWriter.WriteAsync`:

```csharp
// After successful ConfigWriter.WriteAsync:
await File.WriteAllTextAsync(
    Path.Combine(Path.GetDirectoryName(entitiesPath)!, ".ui_config_present"),
    string.Empty, ct);
```

**Why `/data` is the correct directory:** The HA add-on `/data` directory is the persistent add-on volume (mapped from the host's addon_config directory). `ARGUS_ENTITIES_PATH` is set to `/data/entities.yaml` by `10-config-gen.sh` line 111. The lock file must live alongside `entities.yaml` so both checks resolve to the same directory.

**Guard timing:** This guard MUST be added to `10-config-gen.sh` BEFORE the save endpoint is wired. The CONTEXT.md states this explicitly. The planner must sequence the guard edit as its own task before any save endpoint task.

---

### 6. Pipeline Config Pickup After Save

**Finding (VERIFIED: codebase — ScoreStreamPipeline.cs, HaListenerWorker.cs, BatchSchedulerWorker.cs):**

**ScoreStreamPipeline:** `BuildEntityStates()` is called once when `RunAsync(IAsyncEnumerable<HaReading>, CancellationToken)` is called at line 80. This call happens in `HaListenerWorker.ExecuteAsync` (once per worker lifetime). There is no inner retry loop in Phase 2 — `RunAsync` runs until `stoppingToken` fires. Implication: a config change written to `entities.yaml` does NOT affect the running pipeline in Phase 2.

**BatchSchedulerWorker:** `RunBatchAsync` reads `_entities.Entities` per-cycle (line 127). Since `_entities` is the singleton `EntitiesConfig` captured at constructor time, and `EntitiesConfig` is immutable (its `Entities` list is not replaced), per-cycle reads do not pick up a new selection either.

**Phase 2 behavior:** A UI save creates a new `entities.yaml` but does NOT restart the pipeline. The new config takes effect on the next add-on restart. The UI subheading in `02-UI-SPEC.md` says: "Changes take effect on the next pipeline cycle." For Phase 2, "next pipeline cycle" = next add-on restart.

**Phase 3 change required:** Phase 3 will introduce `ILiveEntitiesConfig` with an atomic swap + `ConfigChanged` event. `HaListenerWorker` will cancel an inner `CancellationTokenSource` and loop, creating a new `ScoreStreamPipeline.RunAsync` call with the new config. `BatchSchedulerWorker` will need to inject `ILiveEntitiesConfig` instead of `EntitiesConfig` to pick up changes per-cycle. This is out of scope for Phase 2.

**Phase 2 planner note:** Do NOT add any live-reload mechanism. The pipeline reads the singleton `EntitiesConfig` loaded at startup; a restart is required to pick up the new YAML.

---

### 7. htmx Patterns (Phase 2 interactions)

**Finding (VERIFIED: 02-UI-SPEC.md, Phase 1 PlaceholderPage.cs, htmx 2.0.10 committed):**

All htmx patterns are specified in `02-UI-SPEC.md`. Key implementation details:

**Live search (keyup filter):**
```html
<input type="search"
       name="q"
       class="argus-search__input"
       aria-label="Filter entities"
       placeholder="Filter by entity ID…"
       hx-get="/api/sensors"
       hx-target="#argus-sensor-list"
       hx-trigger="keyup changed delay:200ms"
       hx-push-url="false">
```
The `hx-include="[name='q']"` attribute is listed in the UI-SPEC htmx interaction table but is redundant here — `hx-get` on the input itself will serialize `name="q"` naturally. Simpler to omit `hx-include` and rely on htmx's standard behavior of including the triggering element's value.

**Save form:**
```html
<form id="argus-picker-form"
      hx-post="/api/sensors/save"
      hx-target="#argus-flash"
      hx-swap="innerHTML"
      hx-indicator="#argus-spinner"
      hx-push-url="false">
  <!-- checkboxes: name="entities" value="{entity_id}" -->
  <!-- patterns: name="include_patterns", name="exclude_patterns" -->
  <button type="submit" class="argus-btn argus-btn--primary">Save configuration</button>
</form>
```

Multi-value form fields: htmx POSTs form data using standard form encoding. Multiple checked `name="entities"` checkboxes produce `entities=id1&entities=id2&...`. In ASP.NET Minimal API, bind via:
```csharp
// In the POST handler:
var form = await req.ReadFormAsync(ct);
var selectedEntityIds = form["entities"].ToList();  // StringValues → List<string>
var includeRaw = form["include_patterns"].FirstOrDefault() ?? "";
var excludeRaw = form["exclude_patterns"].FirstOrDefault() ?? "";
```

**Spinner:** htmx adds `htmx-request` class to the `<body>` when any request is in flight. The indicator pattern uses `hx-indicator="#argus-spinner"` on the form, which adds `.htmx-request` to `#argus-spinner` (not body). The CSS in `02-UI-SPEC.md` uses `.htmx-request #argus-spinner` — this requires the spinner to be a DESCENDANT of the form (or use `hx-indicator` properly). The correct htmx pattern:
```html
<div id="argus-spinner" class="argus-spinner" aria-hidden="true"></div>
```
Place `#argus-spinner` inside `#argus-picker-form`. htmx adds `htmx-request` to the indicator element itself (not a parent selector).

**Correct CSS for spinner visibility:**
```css
#argus-spinner { display: none; }
#argus-spinner.htmx-request { display: inline-block; }
```
(not `.htmx-request #argus-spinner` — htmx adds the class TO the indicator, not to an ancestor)

**Banner swap:** The POST response returns a bare HTML fragment (no page shell). htmx swaps it into `#argus-flash` via `hx-swap="innerHTML"`.

**Empty state:** Server returns the `.argus-empty` block inside the list fragment when `GetFiltered(q)` returns empty. No client-side JavaScript needed.

---

### 8. Interim Ingress Auth (Phase 2)

**Finding (VERIFIED: codebase — Program.cs, 10-config-gen.sh):**

Kestrel binds `0.0.0.0:8099`. The Supervisor connects from `172.30.32.2` (fixed internal add-on network address). The Supervisor sets the `X-Ingress-Path` header on forwarded requests.

**Phase 2 MVP auth:** Accept all requests that arrive with an `X-Ingress-Path` header, OR accept all requests from `172.30.32.2`. Since there is no exposed port (no `ports:` in config.yaml), all external traffic must go through the Supervisor proxy. The Supervisor validates the user session. Phase 2 can rely on this without implementing `validate_session`.

**Implementation for Phase 2 (minimal, from CONTEXT.md locked decision):**

No explicit middleware needed in Phase 2. The `X-Ingress-Path` header presence is already a proxy signal. If the planner wants belt-and-suspenders, check `RemoteIpAddress`:

```csharp
// Optional: guard in each endpoint handler (not a middleware) for Phase 2
var remoteIp = ctx.Connection.RemoteIpAddress?.ToString();
// Accept if: from 172.30.32.2 (Supervisor), or 127.0.0.1 (loopback/local dev)
```

**Source IP detection:** In Kestrel behind a reverse proxy, use `ctx.Connection.RemoteIpAddress` (the actual TCP connection IP) rather than `X-Forwarded-For` (which can be spoofed). The Supervisor connects from `172.30.32.2` directly — the TCP connection IP IS the Supervisor. There is no additional forwarding hop that would hide it.

**Phase 4 auth:** Full `validate_session` HTTP call to Supervisor API. CONTEXT.md defers this to Phase 4. Phase 2 does not implement it.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Atomic config write | Custom temp+rename | `ConfigWriter.WriteAsync` (Phase 1) | Already written, tested, serialized by SemaphoreSlim |
| Glob matching | Regex-from-glob converter | `FileSystemName.MatchesSimpleExpression` | BCL, zero deps, verified working, supports `*` and `?` |
| YAML serialization | String interpolation | `YamlDotNet` (already in project) | Untrusted entity_ids require safe serialization (T-1-05 precedent in gen-entities.py) |
| htmx library | Custom fetch + DOM manipulation | `htmx.min.js` (already committed) | Already in wwwroot; Phase 1 foundation |
| Thread-safe reference swap | Custom lock + copy | `volatile` field + reference assignment | Sufficiently safe for single-writer + many-readers; matches `ArgusHealthSignals` pattern in project |

---

## Common Pitfalls

### Pitfall 1: Opening a Second WebSocket for the Sensor List
**What goes wrong:** A naive implementation opens a fresh `HaWebSocketClient` per HTTP request to `GET /api/sensors` to get current state.
**Why it happens:** The developer treats the sensor list as a live data fetch.
**How to avoid:** `IHaSensorRegistry` is the in-memory cache. `NetDaemonHaEventSource` populates it on every `GetStatesAsync` call (which already happens on every connect). The registry is purely a read cache for the UI — no WebSocket in the HTTP handler.
**Warning signs:** Any `new HaWebSocketClient()` or `GetStatesAsync()` call inside an HTTP endpoint method.

### Pitfall 2: Forgetting to Guard 10-config-gen.sh Before Wiring the Save Endpoint
**What goes wrong:** The save endpoint is wired and user saves config; on the next add-on restart, `10-config-gen.sh` overwrites `entities.yaml` with the options.json-generated version, erasing the UI config silently.
**Why it happens:** The tasks are authored without strict ordering — save endpoint lands first, guard lands second.
**How to avoid:** The planner MUST sequence the guard task before the save endpoint task. They should be in different waves with the guard in Wave 1 and the save endpoint in Wave 2.
**Warning signs:** The planner has both tasks in the same wave, or save endpoint is in Wave N-1 and guard in Wave N.

### Pitfall 3: Incorrect htmx Spinner CSS Selector
**What goes wrong:** Using `.htmx-request #argus-spinner` (descendant selector) when `htmx-request` is added to `#argus-spinner` itself (not a parent).
**Why it happens:** Misreading the htmx indicator behavior — `hx-indicator` adds `htmx-request` class to the named element, not its parent.
**How to avoid:** Use `#argus-spinner.htmx-request { display: inline-block; }` (class on the same element).
**Warning signs:** Spinner never appears during Save POST.

### Pitfall 4: YamlDotNet Deserializer DOES NOT Skip `_patterns:` Key
**What goes wrong:** If `IgnoreUnmatchedProperties()` is NOT set, YamlDotNet throws `YamlDotNet.Core.YamlException: Property '_patterns' not found on type EntitiesConfig`.
**Why it happens:** Trusting the `_patterns:` approach without verifying the deserializer config.
**How to avoid:** VERIFIED — `EntitiesConfigLoader.cs` line 24 already calls `.IgnoreUnmatchedProperties()`. The top-level `_patterns:` key will be silently skipped. No change to `EntitiesConfigLoader` required.
**Warning signs:** If someone removes `IgnoreUnmatchedProperties()` in a future refactor, all existing UI-saved entities.yaml files will break on load. Consider adding a test.

### Pitfall 5: Multi-Value Form Field `entities` Lost When Zero Checkboxes Are Checked
**What goes wrong:** When the user unchecks all entities and saves, the POST body contains no `entities` key at all (unchecked checkboxes are not submitted in HTML forms). A naïve `form["entities"]` returns `StringValues.Empty`, which the handler interprets as "field not provided" and does nothing.
**Why it happens:** Standard HTML form behavior — only checked checkboxes are included.
**How to avoid:** Treat `form["entities"]` returning empty as "zero entities selected" — a valid state (user wants to track nothing). Do not return an error; write `entities: []` to YAML and let `EntitiesConfigLoader.Validate` log its existing empty-entities warning on next startup.
**Warning signs:** Save button does nothing when all checkboxes are unchecked.

### Pitfall 6: HaStateDto Constructor Positional Arg Break
**What goes wrong:** `HaStateDto` is a `record` with positional constructor. Adding new fields changes the constructor signature. Any test or call site using `new HaStateDto(entityId, state, lastChangedUtc)` will fail to compile.
**Why it happens:** Records use positional primary constructors — all parameters required.
**How to avoid:** Update ALL `HaStateDto` construction sites when adding `UnitOfMeasurement` and `FriendlyName`. Search for `new HaStateDto(` in the codebase — currently only in `HaWebSocketClient.cs` (lines 74 and 125). Tests using `HaStateDto` must also be updated.
**Warning signs:** Build errors on `new HaStateDto(` after the record is extended.

---

## Code Examples

### HaSensorRegistry — volatile reference swap (thread-safe single-writer pattern)

```csharp
// Source: matches ArgusHealthSignals volatile bool pattern in orchestrator
public sealed class HaSensorRegistry : IHaSensorRegistry
{
    // volatile ensures the latest reference is visible to all threads
    // without needing a lock on the read path (single-writer guarantee holds
    // because NetDaemonHaEventSource's RunConnectionLoopAsync is single-threaded)
    private volatile IReadOnlyList<HaSensorEntry> _snapshot = Array.Empty<HaSensorEntry>();

    public IReadOnlyList<HaSensorEntry> GetAll() => _snapshot;

    public IReadOnlyList<HaSensorEntry> GetFiltered(string q) =>
        string.IsNullOrWhiteSpace(q)
            ? _snapshot
            : _snapshot
                .Where(e => e.EntityId.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();

    public void UpdateSnapshot(IReadOnlyList<HaStateDto> states, HashSet<string> trackedEntityIds)
    {
        var entries = states
            .Where(s => double.TryParse(s.State, NumberStyles.Any,
                CultureInfo.InvariantCulture, out _))
            .Select(s =>
            {
                double.TryParse(s.State, NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var v);
                return new HaSensorEntry(s.EntityId, v,
                    s.UnitOfMeasurement, s.FriendlyName,
                    trackedEntityIds.Contains(s.EntityId));
            })
            .OrderBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _snapshot = entries;
    }
}
```

### YamlDotNet serialization — write entities.yaml with `_patterns:` block

```csharp
// Source: YamlDotNet 16.3.0 — already in project
// Build the config POCO, then inject a _patterns: literal header before serializing

var serializer = new SerializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .Build();

// Serialize the entity list
var configYaml = serializer.Serialize(new EntitiesConfig { Entities = resolvedEntities });

// Prepend the _patterns block as a literal YAML comment-style header
// (YamlDotNet cannot serialize anonymous dicts with underscore keys cleanly,
//  so build the patterns section as a string and prepend it)
var patternsBlock = BuildPatternsYaml(includePatterns, excludePatterns);
var fullYaml = patternsBlock + configYaml;

await _configWriter.WriteAsync(entitiesPath, fullYaml, ct);

// BuildPatternsYaml produces:
// _patterns:\n  include:\n  - sensor.*temp*\n  exclude:\n  - sensor.*test*\n
```

### 10-config-gen.sh guard edit

```bash
# BEFORE (line 111-112):
printf "/data/entities.yaml" > /var/run/s6/container_environment/ARGUS_ENTITIES_PATH
python3 /usr/local/bin/gen-entities.py /data/options.json > /data/entities.yaml

# AFTER (replace those two lines):
printf "/data/entities.yaml" > /var/run/s6/container_environment/ARGUS_ENTITIES_PATH
if [ -f /data/.ui_config_present ]; then
    bashio::log.info "UI config present — skipping gen-entities.py (entities.yaml preserved)."
else
    python3 /usr/local/bin/gen-entities.py /data/options.json > /data/entities.yaml
fi
```

### POST save endpoint skeleton

```csharp
app.MapPost("/api/sensors/save", async (HttpRequest req,
    IHaSensorRegistry registry, ConfigWriter writer,
    ConnectionSettings settings, ILogger<Program> logger,
    CancellationToken ct) =>
{
    try
    {
        var form = await req.ReadFormAsync(ct);
        var selectedIds = form["entities"].Where(s => !string.IsNullOrEmpty(s)).ToList();
        var includeRaw = form["include_patterns"].FirstOrDefault() ?? "";
        var excludeRaw = form["exclude_patterns"].FirstOrDefault() ?? "";

        var includePatterns = includeRaw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var excludePatterns = excludeRaw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        // Resolve final entity set using GlobExpander
        // (selectedIds are "manually checked"; patterns applied to full snapshot)
        var snapshot = registry.GetAll();
        var resolved = GlobExpander.Resolve(snapshot, includePatterns, excludePatterns,
            selectedIds, Enumerable.Empty<string>());

        // Build EntityConfig list with hst defaults for new entities
        var entities = resolved.Select(id =>
        {
            var entry = snapshot.FirstOrDefault(e => e.EntityId == id);
            return new EntityConfig
            {
                EntityId = id,
                FriendlyName = entry?.FriendlyName ?? "",
                Detectors = new List<DetectorConfig>
                    { new() { Name = "hst", Params = new() } }
            };
        }).ToList();

        var yaml = BuildEntitiesYaml(entities, includePatterns, excludePatterns);
        var entitiesPath = settings.EntitiesPath ?? "/data/entities.yaml";
        await writer.WriteAsync(entitiesPath, yaml, ct);

        // Create the lock file (must succeed before returning success to user)
        var lockPath = Path.Combine(Path.GetDirectoryName(entitiesPath)!, ".ui_config_present");
        await File.WriteAllTextAsync(lockPath, string.Empty, ct);

        logger.LogInformation("UI save: {Count} entities written to {Path}", entities.Count, entitiesPath);

        return Results.Content(
            $"""<div class="argus-banner argus-banner--success" role="status" aria-live="polite">
            Configuration saved. {entities.Count} entities tracked.</div>""",
            "text/html");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "UI save failed");
        var reason = ex is IOException ? "disk error" : "unexpected error";
        return Results.Content(
            $"""<div class="argus-banner argus-banner--error" role="alert" aria-live="assertive">
            Save failed. {WebUtility.HtmlEncode(reason)}. Check the add-on log for details.</div>""",
            "text/html");
    }
});
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `gen-entities.py` unconditional overwrite on every boot | Guard with `.ui_config_present` lock file | Phase 2 | User UI config survives add-on restart |
| No sensor registry — config loaded once, pipeline uses that | `IHaSensorRegistry` populated on every HA connect | Phase 2 | UI can display live sensors without a second WebSocket |
| `EntitiesConfig` singleton immutable at startup | Same in Phase 2; `ILiveEntitiesConfig` deferred to Phase 3 | Phase 3 (future) | Pipeline will hot-reload without restart |

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | HA `get_states` always includes `attributes.unit_of_measurement` and `attributes.friendly_name` for numeric sensors | §1 (HaStateDto extension) | Some sensors may lack one or both attributes; code uses nullable fields (`string?`) so missing attributes are safe |
| A2 | The Supervisor connects from exactly `172.30.32.2` in all HA OS versions | §8 (Ingress auth) | Different versions may use different IPs; IP check is belt-and-suspenders only, not blocking auth logic |
| A3 | YamlDotNet 16.3.0 `IgnoreUnmatchedProperties()` silently skips keys starting with `_` as well as any other unmatched key | §4 (patterns persistence) | LOW risk: the behavior is by-design ("ignore unmatched" is unconditional); verified the call is present in the codebase |

---

## Open Questions (RESOLVED)

> All three items below have de-facto answers captured in their Recommendation lines and are treated as RESOLVED for planning. None block Phase 2 execution.

1. **X-Ingress-Path strip behavior (live HA)** — RESOLVED: follow the Phase 1 pattern (read `X-Ingress-Path` for base href); Phase 2 adds no new middleware, so Phase 1's deferred live-HA check covers it.
   - What we know: Phase 1 implemented dual PathBase + `<base href>` defense. Live-HA verification was deferred per 01-02-SUMMARY.md.
   - What's unclear: Does the Supervisor strip `X-Ingress-Path` after setting it, meaning the static file paths resolve correctly from the base href but the PathBase is empty? Or does the Supervisor pass both header and path prefix?
   - Recommendation: Phase 2 endpoints follow the same Phase 1 pattern (`req.Headers["X-Ingress-Path"]` for base href). The behavior is already tested by Phase 1; Phase 2 adds no new middleware.

2. **`GET /` redirect vs picker page**
   - What we know: Phase 1 maps `GET /` to the placeholder page. Phase 2 introduces `GET /sensors` for the picker.
   - What's unclear: Should `GET /` redirect to `/sensors`, or remain as the placeholder?
   - Recommendation: Redirect `GET /` → `/sensors` with `Results.Redirect`. This prevents users from landing on the stale placeholder. No backwards compatibility concern (placeholder is temporary).

3. **`HaStateDto` existing usages in tests**
   - What we know: `HaStateDto` is used in tests that construct it with positional args.
   - What's unclear: Exact test files that need updating when the record is extended.
   - Recommendation: Run `grep -rn "new HaStateDto(" orchestrator/` before writing the plan — then list ALL construction sites that need updating. Based on current inspection, sites are only in `HaWebSocketClient.cs` (lines 74 and 125) and possibly test files.

---

## Environment Availability

Step 2.6: SKIPPED — Phase 2 is a code-only change to the existing add-on. No new external tools, services, or CLIs are required beyond what Phase 1 already verified (.NET 8 SDK, YamlDotNet, htmx). The restart guard edit targets the container environment at add-on runtime; no host-side tool is needed to author it.

---

## Project Constraints (from CLAUDE.md)

| Directive | Impact on Phase 2 |
|-----------|-------------------|
| .NET 8 orchestrator; all ML in Python | No new ML, no Python changes except `10-config-gen.sh` guard |
| YamlDotNet 16.3.0 pinned | Use this version; do not upgrade |
| No new packages preferred | Phase 2 adds ZERO new NuGet packages |
| BSD/Apache/MIT licenses only | No new dependencies; all BCL |
| `grpc.experimental.aio` forbidden | Not applicable (Python only; no Python changes in Phase 2) |
| MQTTnet v4.x ManagedClient forbidden | Not applicable |
| Direct Recorder DB access forbidden | Not applicable |
| Code/identifiers in English; HA entity friendly_names in Polish | UI chrome in English; `friendly_name` displayed as-is (may be Polish) |
| No cloud, self-hosted | Not applicable |
| GPU Phase 3+ only | Not applicable |

---

## Sources

### Primary (HIGH confidence)
- [VERIFIED: codebase] `orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs` — full `GetStatesAsync` implementation; confirmed `attributes` not parsed
- [VERIFIED: codebase] `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` — `GetStatesAsync` call location; `SelectDiscoverableSensors` logic; `_configuredEntities` HashSet
- [VERIFIED: codebase] `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` — `IgnoreUnmatchedProperties()` confirmed on line 24
- [VERIFIED: codebase] `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` — atomic write implementation
- [VERIFIED: codebase] `orchestrator/Argus.Orchestrator/Program.cs` — endpoint registration pattern; PathBase middleware
- [VERIFIED: codebase] `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` — `BuildEntityStates()` called once per `RunAsync`; no inner CTS loop
- [VERIFIED: codebase] `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` — `_entities.Entities` read per-cycle (line 127)
- [VERIFIED: codebase] `argus/rootfs/etc/cont-init.d/10-config-gen.sh` — current unconditional `gen-entities.py` invocation
- [VERIFIED: live test] `FileSystemName.MatchesSimpleExpression` — tested in net8.0 console app; `*` and `?` wildcards confirmed; `ignoreCase: true` default confirmed
- [CITED: learn.microsoft.com/en-us/dotnet/api/system.io.enumeration.filesystemname.matchessimpleexpression?view=net-8.0] — official .NET 8 docs; `*`, `?`, `\` escape supported; no `[...]` bracket patterns

### Secondary (MEDIUM confidence)
- [VERIFIED: codebase] `02-UI-SPEC.md` — htmx interaction contract, endpoint contract, CSS class specs
- [VERIFIED: codebase] `02-CONTEXT.md` — locked decisions, patterns combine model

---

## Metadata

**Confidence breakdown:**
- IHaSensorRegistry design: HIGH — grounded in actual code; call sites verified
- HaStateDto extension: HIGH — `attributes` parsing gap confirmed by reading `HaWebSocketClient.cs`
- Glob matching: HIGH — live test verified; official docs cited
- YamlDotNet `_patterns:` safety: HIGH — `IgnoreUnmatchedProperties()` confirmed in source
- gen-entities.py guard: HIGH — exact file and line verified
- Pipeline reload behavior: HIGH — `RunAsync` lifecycle verified in source
- Interim auth: MEDIUM — Supervisor IP `172.30.32.2` is established project knowledge (STATE.md)

**Research date:** 2026-07-01
**Valid until:** 2026-08-01 (stable stack; no fast-moving dependencies in scope)
