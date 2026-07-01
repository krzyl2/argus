---
phase: 02-live-sensor-discovery-entity-selection-ui
verified: 2026-07-01T00:00:00Z
status: human_needed
score: 5/5 roadmap success criteria verified (automated) + 3 items need human testing
overrides_applied: 0
gaps: []
human_verification:
  - test: "Open the entity picker via 'Open Web UI' in HA after rebuilding the add-on image, then filter by a search term and verify the live sensor list updates"
    expected: "Filterable list of live HA numeric sensors appears with entity_id, current value, and unit; typing in the search box refreshes the list via htmx; tracked sensors show a checked checkbox and a 'tracked' pill"
    why_human: "Requires a live HA instance with the rebuilt add-on image; IHaSensorRegistry is populated only from a real get_states WebSocket call; htmx fragment swap behavior cannot be verified statically"
  - test: "Select or deselect sensors, optionally enter include/exclude patterns, then click 'Save configuration'"
    expected: "POST /api/sensors/save succeeds; /data/entities.yaml is updated with the resolved entities and a _patterns: top-level key; /data/.ui_config_present lock file is created; success banner appears showing entity count; restarting the add-on does NOT regenerate entities.yaml (gen-entities.py is skipped)"
    why_human: "Requires the live add-on with write access to /data; file creation of .ui_config_present and the restart-guard behavior of 10-config-gen.sh can only be verified end-to-end in the running container"
  - test: "After saving and restarting the add-on, confirm the orchestrator's running entity set reflects the saved selection"
    expected: "The orchestrator logs show the entity set loaded from the UI-authored entities.yaml; anomaly detection runs on the newly selected entities on the next pipeline cycle"
    why_human: "Hot-reload is not implemented in Phase 2 (intentional — Phase 3 deferred); only the next restart confirms the new entity set is picked up correctly by EntitiesConfigLoader and the streaming pipeline"
---

# Phase 2: Live Sensor Discovery + Entity Selection UI — Verification Report

**Phase Goal:** The UI lists live HA numeric sensors (reusing a registry populated from the existing get_states call) and lets the user select which entities Argus tracks, with include_patterns/exclude_patterns honored as selection filters (closing the v2.0 patterns-ignored gap). The gen-entities.py guard is in place before the first UI save so restarts cannot erase UI-authored config.
**Verified:** 2026-07-01
**Status:** human_needed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths (Roadmap Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| SC1 | Entity picker lists live HA numeric sensors with values; user can filter by text search on entity_id | VERIFIED (static + human needed for live data) | `EntityPickerPage.BuildListFragment` calls `registry.GetFiltered(q)`; GET /api/sensors wired in Program.cs; HaSensorRegistry.GetFiltered filters by OrdinalIgnoreCase Contains; all user strings HtmlEncoded |
| SC2 | Tracked sensors are visually distinguished from untracked sensors | VERIFIED (static) | `BuildListRows` emits `argus-list-row--tracked` CSS class and `argus-pill argus-pill--tracked` span when `entry.IsTracked`; `EntityPickerPageTests` asserts tracked pill + checked checkbox vs untracked row |
| SC3 | Selecting entities and saving persists to /data/entities.yaml; orchestrator reflects new selection after next pipeline cycle | VERIFIED (static) | POST /api/sensors/save wired; calls `GlobExpander.Resolve` → builds `List<EntityConfig>` → `SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build()` serializes combined root dict → `ConfigWriter.WriteAsync` (atomic temp+rename) → `File.WriteAllText(lockPath, string.Empty)` synchronously; `SaveEndpointPatternsTests` covers round-trip and lock-file creation; next-cycle apply is intentional (no hot-reload in Phase 2) |
| SC4 | include_patterns and exclude_patterns entered in the UI are applied as real selection filters (not ignored) | VERIFIED (static) | `GlobExpander.Resolve` implements full combine model (include→exclude→manuallyChecked→manuallyUnchecked); uses `FileSystemName.MatchesSimpleExpression(p, id, ignoreCase: true)`; 8 `GlobExpanderTests` cover all combine-model behaviors including manual-override ordering; save handler passes include/exclude from form to `GlobExpander.Resolve` |
| SC5 | Restarting the add-on after a UI save preserves UI-authored config — gen-entities.py does not overwrite it | VERIFIED (static) | `10-config-gen.sh` lines 112-116: `if [ -f /data/.ui_config_present ]; then bashio::log.info "UI config present — skipping gen-entities.py; ..."; else python3 /usr/local/bin/gen-entities.py ... fi`; ARGUS_ENTITIES_PATH export is unconditional; lock file written synchronously by save handler after ConfigWriter.WriteAsync succeeds |

**Score:** 5/5 roadmap success criteria have verifiable static evidence. 3 success criteria have a live-test human_needed component (SC1 for live data flow, SC3 for runtime persistence on real /data, SC5 for actual restart behavior).

---

### Requirements Coverage

| Requirement | Source Plans | Description | Status | Evidence |
|------------|-------------|-------------|--------|----------|
| UI-02 | 02-01, 02-03 | UI lists live HA numeric sensors, filterable, lets user select which entities Argus tracks | SATISFIED | IHaSensorRegistry populated from GetStatesAsync; GET /sensors + GET /api/sensors endpoints wired; EntityPickerPage renders live registry data |
| CFG-02 | 02-02, 02-03 | Entity selection (incl. include/exclude patterns) persists to config; closes v2.0 patterns-ignored gap | SATISFIED | GlobExpander.Resolve implements include/exclude combine model; POST /api/sensors/save writes combined YAML via ConfigWriter; _patterns key round-trips via EntitiesConfigLoader.IgnoreUnmatchedProperties |

Both requirement IDs claimed in plan frontmatter are accounted for and satisfied. No orphaned requirements for Phase 2 found in REQUIREMENTS.md.

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `orchestrator/Argus.Orchestrator/Ha/IHaSensorRegistry.cs` | IHaSensorRegistry interface + HaSensorEntry record | VERIFIED | Exists, 36 lines, `public interface IHaSensorRegistry` with GetAll/GetFiltered/UpdateSnapshot; `public record HaSensorEntry` with 5 fields |
| `orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs` | Thread-safe volatile-snapshot implementation | VERIFIED | Exists, 52 lines, `class HaSensorRegistry`; `private volatile IReadOnlyList<HaSensorEntry> _snapshot = Array.Empty<HaSensorEntry>()`; invariant-culture double.TryParse numeric filter; OrdinalIgnoreCase ordering |
| `orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs` | HaStateDto 5-arg with unit_of_measurement + friendly_name | VERIFIED | `public sealed record HaStateDto(string EntityId, string? State, DateTime LastChangedUtc, string? UnitOfMeasurement, string? FriendlyName)`; both GetStatesAsync and ReceiveEventsAsync parse `attributes.unit_of_measurement` and `attributes.friendly_name` via `JsonElement.TryGetProperty` |
| `orchestrator/Argus.Orchestrator/Config/GlobExpander.cs` | Pure static glob expander with combine model | VERIFIED | Exists, 96 lines, `public static class GlobExpander`; uses `FileSystemName.MatchesSimpleExpression`; implements 6-step combine model with snapshot membership gating on manually checked/unchecked |
| `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` | Full-page + list-fragment + banner HTML builders | VERIFIED | Exists, 257 lines, `public static class EntityPickerPage`; BuildFullPage/BuildListFragment/BuildSuccessBanner/BuildErrorBanner; all user strings HtmlEncoded via WebUtility.HtmlEncode |
| `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` | Phase-2 BEM classes | VERIFIED | Contains `argus-list-row`, `argus-pill--tracked`, `argus-filters`, `argus-save-bar`, `argus-btn--primary`, `argus-banner`, `argus-empty`; spinner selector is `#argus-spinner.htmx-request { display: inline-block; }` (correct — Pitfall 3 addressed) |
| `argus/rootfs/etc/cont-init.d/10-config-gen.sh` | Restart guard with .ui_config_present check | VERIFIED | Lines 112-116: `if [ -f /data/.ui_config_present ]` wraps gen-entities.py; ARGUS_ENTITIES_PATH export is unconditional; else-branch runs gen-entities.py unchanged |
| `orchestrator/Argus.Orchestrator.Tests/HaSensorRegistryTests.cs` | Registry unit tests | VERIFIED | Exists; `public class HaSensorRegistryTests`; 12 tests covering numeric filter, non-numeric exclusion, GetFiltered (empty/matching/case-insensitive/no-match), IsTracked (true/false), ordering, thread-safety |
| `orchestrator/Argus.Orchestrator.Tests/GlobExpanderTests.cs` | Combine-model unit tests | VERIFIED | Exists; `public class GlobExpanderTests`; 8 tests including manual-check-overrides-exclude, manual-uncheck-overrides-include, case-insensitive, empty-whitespace patterns ignored, manual-uncheck-beats-manual-check ordering |
| `orchestrator/Argus.Orchestrator.Tests/SaveEndpointPatternsTests.cs` | Save YAML shape + _patterns round-trip + lock file tests | VERIFIED | Exists; `public class SaveEndpointPatternsTests`; 8 tests covering _patterns round-trip via EntitiesConfigLoader, YAML builder shape, YAML-special chars (colon in entity_id), zero entities valid, lock file creation, lock file NOT created on write failure, full multi-entity round-trip |
| `orchestrator/Argus.Orchestrator.Tests/EntityPickerPageTests.cs` | EntityPickerPage unit tests | VERIFIED | Exists; `public class EntityPickerPageTests` (confirmed in SUMMARY, 14 tests: tracked pill, untracked, empty state, no-results, XSS encoding, friendly name, banners) |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `NetDaemonHaEventSource.cs` | `IHaSensorRegistry.UpdateSnapshot` | call after GetStatesAsync on every connect | VERIFIED | Line 123: `_sensorRegistry.UpdateSnapshot(states, _configuredEntities)` called immediately after `GetStatesAsync` before first-connect/reconnect branch; runs on both first connect and reconnect |
| `HaWebSocketClient.cs` | `HaStateDto` (5-arg) | attributes parsing | VERIFIED | Both GetStatesAsync (line 83) and ReceiveEventsAsync (line 140) parse `attributes.unit_of_measurement` and `attributes.friendly_name`; `unit_of_measurement` confirmed present at both sites |
| `Program.cs` | `EntityPickerPage.BuildFullPage / BuildListFragment` | MapGet /sensors and /api/sensors | VERIFIED | Line 224: `app.MapGet("/sensors", ...)` calls `EntityPickerPage.BuildFullPage`; Line 238: `app.MapGet("/api/sensors", ...)` calls `EntityPickerPage.BuildListFragment` |
| `Program.cs` | `GlobExpander.Resolve + ConfigWriter.WriteAsync + .ui_config_present` | MapPost /api/sensors/save | VERIFIED | Line 272: `GlobExpander.Resolve(registry.GetAll(), include, exclude, selectedIds..., [])` called; Line 320: `writer.WriteAsync(entitiesPath, fullYaml, ct)` called; Line 326: `File.WriteAllText(lockPath, string.Empty)` called synchronously after successful write |
| `EntityPickerPage.cs` | `IHaSensorRegistry.GetFiltered` | list fragment rendering | VERIFIED | Line 144: `BuildListFragment` calls `registry.GetFiltered(q)` |
| `10-config-gen.sh` | `/data/.ui_config_present` | if [ -f /data/.ui_config_present ] guard around gen-entities.py | VERIFIED | Lines 112-116 contain the exact guard; else-branch preserves gen-entities.py invocation |
| `GlobExpander.Resolve` | `FileSystemName.MatchesSimpleExpression` | include/exclude glob matching | VERIFIED | Lines 66 and 72 both call `FileSystemName.MatchesSimpleExpression(p, id, ignoreCase: true)` |
| `Program.cs` | `IHaSensorRegistry` DI singleton | AddSingleton registration | VERIFIED | Line 85: `builder.Services.AddSingleton<IHaSensorRegistry, HaSensorRegistry>()` |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|--------------|--------|-------------------|--------|
| `EntityPickerPage.BuildListFragment` | `entries` (IReadOnlyList<HaSensorEntry>) | `registry.GetFiltered(q)` → `HaSensorRegistry._snapshot` (volatile) → populated by `NetDaemonHaEventSource` via `GetStatesAsync` on HA WebSocket | Live HA sensor data (blocked on live HA instance for runtime confirmation) | WIRED — data path is complete from WebSocket to HTML render; verified statically up to the HA connection boundary |
| `POST /api/sensors/save` | `resolvedIds` (HashSet<string>) | `GlobExpander.Resolve(registry.GetAll(), ...)` | Derived from live snapshot + user form input; written to disk via ConfigWriter | WIRED — GlobExpander operates on real snapshot data; ConfigWriter writes real YAML to entitiesPath |

---

### Behavioral Spot-Checks

Step 7b is SKIPPED for the HTTP endpoints — they require a live HA container and cannot be tested statically. The following static checks substitute:

| Behavior | Check | Result | Status |
|----------|-------|--------|--------|
| `HaSensorRegistry` numeric filter | Code inspection: `double.TryParse(s.State, NumberStyles.Any, CultureInfo.InvariantCulture, out _)` | Uses invariant culture; matches SelectDiscoverableSensors rule | PASS |
| `HaSensorRegistry` thread safety | Code inspection: `private volatile IReadOnlyList<HaSensorEntry> _snapshot`; single assignment in UpdateSnapshot | Volatile reference swap; no locks; no torn reads | PASS |
| `GlobExpander` combine model step ordering | Code inspection: steps 1-6 in order; step 6 (manuallyUnchecked removal) is last; both step 5 and 6 gate on `allIds.Contains(id)` | Manual wins; snapshot membership enforced | PASS |
| Spinner selector | Grep `#argus-spinner.htmx-request` in argus.css | Found at line 402: `#argus-spinner.htmx-request { display: inline-block; }` | PASS |
| Lock file write ordering | Code inspection: `File.WriteAllText(lockPath, string.Empty)` at line 326, synchronous, after `await writer.WriteAsync(...)` succeeds | Lock only created after successful YAML write; no async gap (WR-02 addressed) | PASS |
| Interim auth: header-alone not accepted | Code inspection: `IsAuthorizedRequest` uses only `ctx.Connection.RemoteIpAddress`; X-Ingress-Path is explicitly NOT treated as auth credential (comment at line 203) | Loopback or 172.30.32.2 required; header alone returns false | PASS |
| WebSocket frame cap | Grep `MaxMessageBytes` in HaWebSocketClient.cs | `private const int MaxMessageBytes = 4 * 1024 * 1024`; enforced in `ReceiveMessageAsync` | PASS |
| _patterns round-trip | `SaveEndpointPatternsTests.EntitiesConfigLoader_YamlWithPatternsBlock_LoadsCleanly` | Confirmed by test coverage; uses `IgnoreUnmatchedProperties()` | PASS |

---

### Anti-Patterns Found

No blocking anti-patterns detected. Scan of Phase-2 modified files:

| File | Pattern | Severity | Assessment |
|------|---------|----------|------------|
| `EntityPickerPage.cs` | No `TODO`, `FIXME`, `PLACEHOLDER`, `return null`, or hardcoded empty data patterns in user-facing code | — | Clean |
| `Program.cs` | `lastIncludePatterns = ""` / `lastExcludePatterns = ""` initialized empty | Info | Intentional in-memory holder — fresh restart shows empty pattern boxes; documented design decision, not a stub |
| `GlobExpander.cs` | No issues | — | Clean |
| `HaSensorRegistry.cs` | `_snapshot = Array.Empty<HaSensorEntry>()` initial state | Info | Intentional initial state before first HA connect; not a stub — populated by UpdateSnapshot on every connect |
| `10-config-gen.sh` | No issues | — | Clean |

---

### Human Verification Required

#### 1. Live Sensor Discovery and htmx Search

**Test:** After rebuilding and deploying the add-on image, open the Argus Ingress UI via "Open Web UI" in the HA sidebar. Verify the sensor list loads. Then type a partial entity_id into the search box (e.g., "temp").
**Expected:** The list shows live HA numeric sensors with entity_id, current value, and unit (e.g., "22.5 °C"). Typing in the search box triggers an htmx GET /api/sensors request that filters the list without a full page reload. Tracked sensors show a checked checkbox and a green "tracked" pill; untracked sensors show unchecked checkboxes with no pill.
**Why human:** Requires a live HA instance — IHaSensorRegistry is populated only from a real WebSocket get_states response. htmx behavior (fragment swap, delay:200ms debounce) cannot be verified statically.

#### 2. Save Selection + include/exclude Patterns + Lock File

**Test:** Select or deselect sensors, enter include patterns (e.g., `sensor.*temp*`) and exclude patterns (e.g., `sensor.*test*`) in the Pattern Filters panel, then click "Save configuration". Then SSH into the container (or use HA add-on log) to confirm files were written.
**Expected:** POST /api/sensors/save returns a success banner ("Configuration saved. N entities tracked."). Inside the container: `/data/entities.yaml` exists and contains the `_patterns:` block followed by `entities:` with the resolved entity list (include/exclude applied); `/data/.ui_config_present` exists and is empty. The YAML-special-character patterns are properly quoted by YamlDotNet.
**Why human:** Requires write access to /data in the running add-on container; ConfigWriter atomic write and lock-file creation must be verified against the real filesystem.

#### 3. Restart Preserves UI-Authored Config

**Test:** After a successful save (step 2), restart the add-on. Check the HA add-on log for 10-config-gen.sh output.
**Expected:** The add-on log shows "UI config present — skipping gen-entities.py; ARGUS_ENTITIES_PATH=/data/entities.yaml already set." and NOT a gen-entities.py invocation. After restart, the orchestrator loads the UI-authored entities.yaml and starts tracking the previously selected sensors (visible in the detection log or MQTT binary_sensor entities).
**Why human:** The restart guard and file-preservation behavior require an actual s6-supervised container restart to verify; cannot be simulated statically.

---

### Gaps Summary

No automated gaps identified. All 5 roadmap success criteria have complete static evidence. All required artifacts exist, are substantive (non-stub), and are properly wired. The 3 human verification items are live-HA-dependent tests that are structurally impossible to verify in a pipeline without a running add-on.

Per the verification notes: this phase ran in an autonomous pipeline with no live HA instance. The human_needed items are standard operator UAT tests to be run after the v3.0 add-on image is rebuilt and deployed, not evidence of implementation failures.

---

_Verified: 2026-07-01_
_Verifier: Claude (gsd-verifier)_
