# Phase 4: Validation, CI Packaging + Documentation — Research

**Researched:** 2026-07-01
**Domain:** ASP.NET Minimal API validation, FileSystemWatcher, dotnet publish static assets, DOCS.md
**Confidence:** HIGH — all findings grounded in actual codebase files; no external library research needed

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

- entity_id validation: HA-style regex `^[a-z0-9_]+\.[a-z0-9_]+$`
- Detector parameter ranges per type (HST/MAD/STL) — exact ranges in 04-CONTEXT.md and 04-UI-SPEC.md
- Client-side validation: inline per-field error highlight + message; Save button DISABLED while any field is invalid; mirrors server rules exactly
- Server-side: validate BEFORE any write; on failure return picker error fragment (htmx) with per-field messages + banner; NO partial write; server is source of truth
- FileSystemWatcher on `/data` for `Renamed` events targeting `entities.yaml`; 300 ms timer-reset debounce; exactly one reload per atomic write
- External-edit reload path: Load + Validate + Swap; invalid external edit logged and IGNORED (keep current config; never crash)
- UI save's own rename ALSO fires the watcher; 300 ms debounce coalesces it; Swap is idempotent → redundant watcher reload is harmless
- wwwroot bundling: confirm dotnet publish emits wwwroot/ into orchestrator/publish/; add CI assertion for htmx.min.js + argus.css presence
- Image-size gate: REUSE existing <2 GB gate in build.yml; do NOT add a second gate
- DOCS.md: add Ingress UI section per SC5

### Claude's Discretion

- Exact validation error message wording (locked in 04-UI-SPEC.md copywriting contract)
- Where the validation helper lives (a shared validator used by save endpoint + client-mirrored)
- Client-side validation implementation (small inline JS / htmx hooks) consistent with air-gapped no-build constraint

### Deferred Ideas (OUT OF SCOPE)

- Full `validate_session` IngressAuthMiddleware (remains interim Supervisor-IP auth from Phase 2)
- Any new feature UI beyond validation states
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| UI-04 | UI inputs validated (entity_id format, parameter ranges) with clear error messages before save | Server validator in save handler gating before ConfigWriter.WriteAsync; inline JS client mirror; CSS error modifier; htmx fragment return on rejection |
| DOCS-02 | DOCS.md documents Ingress UI; multi-arch image bundles UI assets and stays under 2 GB | dotnet publish (Web SDK) emits wwwroot/; Dockerfile COPY picks it up; existing <2 GB gate covers it; DOCS.md Ingress UI section outlined |
</phase_requirements>

---

## Summary

Phase 4 is a pure hardening + packaging + docs layer — no new functionality beyond validation states, FileSystemWatcher, and documentation. All integration points are already in place from Phases 1–3. The work is small and well-bounded.

**Server validation** inserts a new `InputValidator` static class (or file-level static method) called in the save handler immediately after `ReadFormAsync`, before any call to `ConfigWriter.WriteAsync`. The validator receives the parsed `selectedIds` (entity_id strings), the `parsedDetectors` dictionary, and returns a structured result (valid/invalid with per-field error list). On failure, the handler returns `BuildValidationBanner(n)` into `#argus-flash` without touching the filesystem.

**Client-side validation** is a single inline `<script>` block at the bottom of `EntityPickerPage.cs`, wired via event delegation on `#argus-picker-form`. The `PARAM_RULES` object is the single source of truth for client-side rules. The entity_id fields (checkboxes carry the entity_id as the checkbox value, not a freeform input) are NOT user-editable — the `<select>` for detector names is constrained to `hst`/`mad`/`stl` — so client-side entity_id and detector-name checks are not needed; only numeric param field rules need JS.

**FileSystemWatcher** should be implemented as an `IHostedService` (or registered inline in `Program.cs`). It watches the directory containing `entities.yaml` for `Renamed` events where `e.Name == "entities.yaml"`. A `System.Threading.Timer` reset on each event, firing after 300 ms of quiet, calls `Load + Validate + Swap` on the same path. The UI save's own rename fires the watcher; the debounce coalesces it and `Swap` is idempotent — no special suppression needed, no infinite loop possible.

**CI wwwroot bundling** is handled automatically by `Microsoft.NET.Sdk.Web` — static files in `wwwroot/` are included in `dotnet publish` output by default. The CI's `dotnet publish -o orchestrator/publish/` step already covers this. A CI assertion (a `ls` check in the build.yml after publish) can confirm `wwwroot/js/htmx.min.js` and `wwwroot/css/argus.css` are present before the Docker build step.

**Primary recommendation:** Implement a single `static class InputValidator` in `Argus.Orchestrator/Config/` (or `Web/`), called in the save handler before `ConfigWriter.WriteAsync`. Wire inline JS via event delegation on the form. Register the FileSystemWatcher as a `BackgroundService` with a 300 ms `Timer` debounce. Add a one-line CI assertion after `dotnet publish`. Add the Ingress UI section to DOCS.md.

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| entity_id + param validation (server) | API / Backend | — | Source of truth; fires before filesystem write |
| entity_id + param validation (client) | Browser / Client | — | Convenience mirror; cannot be the sole gate |
| FileSystemWatcher + debounce | API / Backend | — | Server process owns the config file lifecycle |
| wwwroot bundling | CDN / Static | API / Backend | dotnet publish emits; Kestrel serves via UseStaticFiles |
| DOCS.md update | — | — | Documentation artifact; no tier |

---

## Standard Stack

No new dependencies are added in Phase 4. All capabilities use built-in .NET 8 APIs or existing registered services.

### Core (already in place)
| Library | Version | Purpose | Notes |
|---------|---------|---------|-------|
| `Microsoft.NET.Sdk.Web` | (SDK) | Emits wwwroot/ in publish output | Already set in .csproj |
| `System.IO.FileSystemWatcher` | .NET 8 built-in | Watches directory for Renamed events | No NuGet package needed |
| `System.Threading.Timer` | .NET 8 built-in | 300 ms debounce | No NuGet package needed |
| `System.Text.RegularExpressions` | .NET 8 built-in | entity_id regex validation | Already used in DetectorFieldParser |
| `System.Globalization.CultureInfo` | .NET 8 built-in | Invariant-culture double.TryParse in validator | Already used in HstParams.From() |

**No new NuGet packages are required.** [VERIFIED: orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj]

---

## Architecture Patterns

### System Architecture Diagram

```
POST /api/sensors/save (form data)
        │
        ▼
  ReadFormAsync()          — parse form fields
        │
        ▼
  InputValidator.Validate()  ◄── entity_id regex + per-type param range checks
        │
   invalid?─────────────────► BuildValidationBanner(n) → #argus-flash (htmx)
        │                         [no filesystem write]
        │ valid
        ▼
  ConfigWriter.WriteAsync()  — atomic temp→rename write to /data/entities.yaml
        │
        ▼
  File.WriteAllText(lockPath)  — .ui_config_present lock file
        │
        ▼
  EntitiesConfigLoader.Load() → liveCfg.Swap()  — fires ConfigChanged
        │
        ▼
  BuildSuccessBanner(count)   — includes .argus-warmup-note if HST present
        │
        ▼
  #argus-flash (htmx swap)

  ─────────────────────────────────────────────────

  FileSystemWatcher (BackgroundService)
        │
  Renamed event (e.Name == "entities.yaml")
        │
        ▼
  Timer.Change(300ms)     — reset debounce on each event
        │  [quiet for 300ms]
        ▼
  EntitiesConfigLoader.Load()
        │
   valid?──no──► LogWarning, keep _current (no Swap)
        │ yes
        ▼
  liveCfg.Swap(newConfig)   — idempotent; harmless if redundant after UI save

  ─────────────────────────────────────────────────

  CI build.yml
        │
  dotnet publish -o orchestrator/publish/
        │
  assert: orchestrator/publish/wwwroot/js/htmx.min.js exists
  assert: orchestrator/publish/wwwroot/css/argus.css exists
        │
  docker buildx build  → COPY orchestrator/publish/ → /opt/argus/orchestrator/
        │
  image-facts: <2 GB gate (existing)
```

### Recommended Project Structure

Phase 4 adds exactly these files:

```
orchestrator/Argus.Orchestrator/
├── Config/
│   └── InputValidator.cs          # NEW — entity_id + detector param validation
├── Workers/
│   └── ConfigFileWatcherService.cs  # NEW — FileSystemWatcher + 300ms debounce hosted service
├── Web/
│   └── EntityPickerPage.cs        # EXTEND — BuildValidationBanner(), inline JS script block,
│                                  #          .argus-param-field__error-msg spans, warmup note
└── wwwroot/css/
    └── argus.css                  # EXTEND — 3 new CSS modifiers (Phase 4 additions)

argus/
└── DOCS.md                        # EXTEND — Ingress UI section

.github/workflows/
└── build.yml                      # EXTEND — CI wwwroot assertion step
```

---

## Research Question 1: Server Validation — Insertion Point and Design

### Where validation inserts in the save handler

**Verified in `Program.cs` lines 280–405** [VERIFIED: orchestrator/Argus.Orchestrator/Program.cs]

The save handler `POST /api/sensors/save` currently:
1. Checks `IsAuthorizedRequest` (line 285) — returns 403 if unauthorized
2. Calls `ReadFormAsync(ct)` (line 289) — parses form
3. Extracts `selectedIds` (line 291–294)
4. Parses patterns + runs `GlobExpander.Resolve` (line 296–309)
5. Calls `DetectorFieldParser.Parse(formPairs)` (line 311–315)
6. Builds `entities` list with defaults (line 317–340)
7. Serializes YAML (line 342–366)
8. Calls `writer.WriteAsync(entitiesPath, fullYaml, ct)` (line 369–370) ← **validation must gate here**
9. Writes lock file (line 372–380)
10. Calls `EntitiesConfigLoader.Load` + `liveCfg.Swap` (line 382–386)

**Validation point:** After step 5 (`DetectorFieldParser.Parse`) and before step 7 (YAML serialization / ConfigWriter write). At this point `selectedIds` (resolved entity IDs) and `parsedDetectors` (entity index → detector list) are both available.

Note: `selectedIds` at this point are raw form values before `GlobExpander.Resolve`. The validator should run on the **resolved entity IDs** (after `GlobExpander.Resolve` — step 4) so that glob-expanded entity_ids are also validated. However, glob-expanded IDs come from the live HA registry (already valid entity_ids from HA) — only the manually-typed checkbox values could contain invalid ids. The checkbox values in the UI are server-rendered and HTML-encoded from the registry, so in practice they are always valid. The validator's entity_id check guards against tampered POST bodies.

**Practical insertion point:** After `parsedDetectors` is built (step 5), before building the `entities` list (step 6). This means:

```csharp
// After: var parsedDetectors = DetectorFieldParser.Parse(formPairs);
// Before: var snapshotById = registry.GetAll()...

var validationErrors = InputValidator.Validate(resolvedIds, parsedDetectors);
if (validationErrors.Count > 0)
{
    return Results.Content(
        EntityPickerPage.BuildValidationBanner(validationErrors.Count),
        "text/html");
}
```

### InputValidator design

A `public static class InputValidator` in `Argus.Orchestrator/Config/` (alongside `EntitiesConfigLoader`). Returns a `List<string>` of error messages (empty = valid).

**Entity_id validation:**
```csharp
// Source: 04-CONTEXT.md decision; HA entity_id format
private static readonly Regex EntityIdRegex =
    new(@"^[a-z0-9_]+\.[a-z0-9_]+$", RegexOptions.Compiled);
```

**Detector param validation:** Uses `double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)` (same pattern as `HstParams.From()` in `EntitiesConfig.cs`) then range check. Integer fields additionally check `d == Math.Floor(d)` or use `int.TryParse` directly.

**Cross-field validation (high/low threshold):** After individual field validation, compare `high_threshold > low_threshold` across a single `DetectorConfig.Params` dict.

**Unknown detector name:** `if (!new[]{"hst","mad","stl"}.Contains(name, StringComparer.OrdinalIgnoreCase))` → error.

**Return shape:** `List<(string FieldKey, string Message)>` or simply `List<string>`. For Phase 4 (Option A banner only), a `List<string>` error list with a count is sufficient — the banner shows the count and the per-field JS already handles client-side highlighting.

### Existing `EntitiesConfigLoader.Validate()` is NOT replaced

The existing `Validate()` (lines 39–63 of `EntitiesConfigLoader.cs`) performs **structural** validation (null entity, missing entity_id, no detectors). The new `InputValidator` performs **input** validation (entity_id format regex, param ranges, unknown detector name). They are complementary and both run:
- `InputValidator.Validate()` runs in the save handler BEFORE write (input validation gate)
- `EntitiesConfigLoader.Validate()` runs during every `Load()` call (structural validation, including watcher reloads)

[VERIFIED: orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs lines 39–63]

---

## Research Question 2: Client-Side Validation

### Where it lives

An inline `<script>` block at the bottom of `BuildFullPage()` in `EntityPickerPage.cs`, rendered directly before `</body>`. Not a separate `.js` file. [VERIFIED: 04-CONTEXT.md, 04-UI-SPEC.md]

### Current param field HTML (no error-msg spans yet)

The current `BuildHstParamGrid()`, `BuildMadParamGrid()`, `BuildStlParamGrid()` render `<div class="argus-param-field">` blocks without the `aria-describedby`, `aria-invalid`, or `<span class="argus-param-field__error-msg">` elements. [VERIFIED: orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs lines 407–507]

Phase 4 must add to every `<input class="argus-param-field__input">`:
- `aria-describedby="param-{ei}_{di}_{key}-err"`
- `aria-invalid="false"`

And after each input, add:
```html
<span class="argus-param-field__error-msg"
      id="param-{ei}_{di}_{key}-err"
      role="alert"
      aria-live="assertive"></span>
```

NOTE: The current id format in EntityPickerPage uses `p_{entityIdx}_{detIdx}_{key}` (e.g. `p_0_0_window`). The 04-UI-SPEC uses `param-{entity_idx}-{det_idx}-{key}` with hyphens and prefix `param-`. Phase 4 should migrate to the spec's id format (`param-{ei}-{di}-{key}`) for the input id and `param-{ei}-{di}-{key}-err` for the error span. This is a small search-and-replace in the three `Build*ParamGrid` methods.

### Entity_id fields are not freeform inputs

Examined `BuildListRows()` in EntityPickerPage.cs: entity_ids are rendered as `<input type="checkbox" name="entities" value="{safeEntityId}">`. They are NOT text inputs — users cannot type custom entity_ids in the current UI. The entity_id validation in `InputValidator` is a server-side tamper-guard only. No client-side entity_id field validation is needed (no text input exists for entity_id). [VERIFIED: EntityPickerPage.cs lines 338–352]

### Detector name field is a `<select>` — no client JS needed

The detector type is a `<select>` with three `<option>` values (`hst`, `mad`, `stl`). A normal form submit cannot produce an unknown value without tampering. No client-side detector-name validation needed. [VERIFIED: EntityPickerPage.cs lines 207–213]

### What client JS actually validates

Only the numeric parameter inputs in `argus-param-field__input` fields. The `PARAM_RULES` object keys are matched by the param key extracted from `name` attribute last segment (e.g., `detectors[0][0][params][window]` → key is `window`).

### Entity_id validation in the entity_id text field — not applicable

There is no entity_id text input field in the current UI. Entity_ids come from checkboxes populated by the server-side registry. Client-side entity_id validation is therefore a non-issue for the normal code path. The 04-UI-SPEC entity_id validation rule is a server-only concern.

### Keeping client and server in sync

The `PARAM_RULES` constant object in the inline JS script is the client-side rule source. The `InputValidator` C# class is the server-side rule source. They are manually kept in sync (no auto-generation needed given the bounded rule set and single developer). The 04-CONTEXT.md confirms this is acceptable. If a range changes in `InputValidator`, the corresponding `PARAM_RULES` entry must also change — the planner should note this in the task comments.

---

## Research Question 3: FileSystemWatcher

### ConfigWriter's temp→rename produces a Renamed event

`ConfigWriter.WriteAsync` calls `File.Move(tmp, targetPath, overwrite: true)` on line 27. [VERIFIED: ConfigWriter.cs line 27]

On Linux (where the add-on runs): `File.Move` with overwrite maps to `rename(2)` system call. `FileSystemWatcher` raises `Renamed` when a file is atomically replaced via `rename()`. This is the standard .NET behavior on Linux — a `rename()` of a temp file onto the target path raises a `Renamed` event where `e.Name == "entities.yaml"` and `e.OldName == ".entities.tmp.{guid}.yaml"`. [ASSUMED — standard .NET FileSystemWatcher behavior on Linux; cross-verified with common knowledge of inotify IN_MOVED_TO mapping to Renamed]

### What events ConfigWriter generates

`File.WriteAllTextAsync(tmp, ...)` creates the temp file → raises `Created` event for `.entities.tmp.{guid}.yaml`.
`File.Move(tmp, targetPath, overwrite: true)` renames to `entities.yaml` → raises `Renamed` event with `e.Name == "entities.yaml"`.

The watcher filters on `Renamed` where `e.Name == "entities.yaml"`. The `Created` event for the temp file is ignored (different name, and the filter is on Renamed).

### Does the watcher cause an infinite loop after UI save?

No. Here is the full event sequence when the UI save runs:

1. UI save handler writes via `ConfigWriter.WriteAsync` → `entities.yaml` gets a `Renamed` event
2. The watcher fires its `Renamed` handler → starts (or resets) the 300ms debounce timer
3. UI save handler immediately continues: calls `EntitiesConfigLoader.Load()` + `liveCfg.Swap(newConfig)` → fires `ConfigChanged`
4. 300ms later, the debounce timer fires → watcher calls `EntitiesConfigLoader.Load()` + `liveCfg.Swap(newConfig2)`
5. `newConfig2` is identical to `newConfig` (same file, same content) → `Swap` replaces with equal value → fires `ConfigChanged` again

`Swap` calls `Interlocked.Exchange` + `ConfigChanged?.Invoke`. The `HaListenerWorker` subscribes to `ConfigChanged` and cancels its inner CTS. This means a UI save causes two `ConfigChanged` events: one immediate (from the save handler) and one ~300ms later (from the watcher). The second one triggers an unnecessary inner-CTS cancel + restart of `ScoreStreamPipeline`. [VERIFIED: LiveEntitiesConfig.cs Swap implementation; Program.cs save handler sequence]

**Safe design recommendation:** This is the approach locked in 04-CONTEXT.md: "Swap is idempotent, so a redundant watcher-triggered reload right after a UI save is harmless (no special suppression needed)." Accepted — the pipeline restart takes < 1 second and happens once 300ms after save. No special suppression needed.

**Why no infinite loop:** `Swap` does NOT write to disk. Writing to disk is only done by `ConfigWriter.WriteAsync`. The watcher only calls `Load` + `Swap` (reads + memory swap). No write means no new `Renamed` event from the watcher's action. The event chain terminates after one bounce.

### FileSystemWatcher registration — hosted service pattern

Register as an `IHostedService` / `BackgroundService`:

```csharp
// ConfigFileWatcherService.cs
public sealed class ConfigFileWatcherService : BackgroundService
{
    private readonly ILiveEntitiesConfig _liveCfg;
    private readonly ConnectionSettings _settings;
    private readonly ILogger<ConfigFileWatcherService> _logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var entitiesPath = _settings.EntitiesPath ?? "/data/entities.yaml";
        var dir = Path.GetDirectoryName(Path.GetFullPath(entitiesPath))!;
        var fileName = Path.GetFileName(entitiesPath);

        using var watcher = new FileSystemWatcher(dir, fileName)
        {
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        Timer? debounce = null;

        watcher.Renamed += (_, e) =>
        {
            if (!string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase)) return;
            debounce?.Dispose();
            debounce = new Timer(_ => Reload(entitiesPath), null,
                TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
        };

        stoppingToken.WaitHandle.WaitOne();
        debounce?.Dispose();
        return Task.CompletedTask;
    }

    private void Reload(string path)
    {
        try
        {
            var newConfig = EntitiesConfigLoader.Load(path, _logger);
            _liveCfg.Swap(newConfig);
            _logger.LogInformation(LogEvents.ConfigReloadTriggered,
                "External edit detected — reloaded {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "External edit to {Path} failed validation — keeping current config", path);
        }
    }
}
```

Register in `Program.cs`:
```csharp
builder.Services.AddHostedService<ConfigFileWatcherService>();
```

[ASSUMED — standard .NET pattern for FileSystemWatcher in a hosted service; logic follows 04-CONTEXT.md decisions]

**Note on `NotifyFilter`:** `NotifyFilters.FileName` enables watching for rename (file rename/move within the directory). On Linux, `inotify` maps `IN_MOVED_TO` → `Renamed` event when `NotifyFilters.FileName` is set. [ASSUMED — standard .NET/inotify mapping]

**Note on `FilterAttribute`:** `FileSystemWatcher(dir, fileName)` constructor sets the `Filter` to `fileName`. However, `Renamed` events may fire for ANY file in the directory and the filter may not apply to `OldName` vs `Name`. Safe guard: check `e.Name == fileName` inside the handler (already shown above). [ASSUMED — defensive guard against inotify behavior]

### Invalid external edit behavior

`EntitiesConfigLoader.Load()` can throw `InvalidOperationException` for structural errors (null entity, missing entity_id, no detectors). It can also throw `FileNotFoundException` if the file was deleted between the watcher event and the Load call. The `Reload()` method wraps in `try/catch(Exception)` and logs a `LogWarning` — keeps the current `_liveCfg.Get()` reference unchanged. This satisfies the 04-CONTEXT.md requirement: "An invalid external edit is logged and IGNORED (keep the current running config; never crash the pipeline)."

---

## Research Question 3b: FileSystemWatcher + UI save interaction

Already addressed above. Summary:

| Scenario | Events | Outcome |
|----------|--------|---------|
| UI save | ConfigWriter.WriteAsync → Renamed → watcher 300ms timer | One immediate Swap from save handler + one harmless Swap ~300ms later from watcher |
| External valid edit | File renamed externally → watcher 300ms timer | One Swap from watcher |
| External invalid edit | File renamed externally → watcher 300ms timer → Load throws | Logged + ignored, current config kept |
| Rapid external edits (N renames in < 300ms) | N Renamed events → N timer resets → one 300ms timer fires | Exactly ONE reload |

The double-Swap after UI save is the only edge case. It is harmless: second Swap fires ConfigChanged → HaListenerWorker restarts inner CTS → ScoreStreamPipeline re-reads the same config → re-subscribes to the same entities. Net effect: a < 1s pipeline re-initialization ~300ms after save. Acceptable for a single-developer personal tool. [VERIFIED: 04-CONTEXT.md decision; LiveEntitiesConfig.cs Swap implementation]

---

## Research Question 4: CI wwwroot Bundling

### dotnet publish (Web SDK) behavior

`Argus.Orchestrator.csproj` uses `Microsoft.NET.Sdk.Web`. [VERIFIED: Argus.Orchestrator.csproj line 1]

For `Microsoft.NET.Sdk.Web`, static web assets (files in `wwwroot/`) are automatically included in the publish output. The publish command in `build.yml` is:

```
dotnet publish orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj
  -c Release --self-contained false -o orchestrator/publish/
```

This will emit `orchestrator/publish/wwwroot/js/htmx.min.js` and `orchestrator/publish/wwwroot/css/argus.css` in the CI context. [ASSUMED — standard Web SDK behavior; confirmed by SDK type in .csproj; the local publish/ dir is untracked and was generated before the Web SDK migration, so absence there is not contradictory]

**Key finding — orchestrator/publish/ is UNTRACKED in git:**
`git status` shows `orchestrator/publish/` as an untracked directory (not committed, not gitignored). [VERIFIED: git status output from Bash tool] The CI's `dotnet publish` step creates this directory fresh before `docker buildx build`, so wwwroot is present in the CI Docker build context. The local published artifacts on disk are stale (pre-Web-SDK). This is not a problem for CI; it means local Docker builds require a local `dotnet publish` first.

### Dockerfile COPY

`argus/Dockerfile` line 51: `COPY orchestrator/publish/ /opt/argus/orchestrator/` [VERIFIED: argus/Dockerfile line 51]

This recursively copies all files in `orchestrator/publish/` including `wwwroot/` when present. Static files land at `/opt/argus/orchestrator/wwwroot/` in the image.

Kestrel's `UseStaticFiles()` in `Program.cs` line 197 serves the `WebRootPath` which defaults to `wwwroot/` relative to the app's content root. The .NET 8 runtime sets the content root to the directory containing the assembly (`/opt/argus/orchestrator/`), so `wwwroot` at `/opt/argus/orchestrator/wwwroot/` is served correctly. [ASSUMED — standard ASP.NET Core content root behavior; standard for dotnet publish output layout]

### CI assertion for wwwroot presence

Add a step after `dotnet publish` and before `docker buildx build` in `build.yml`:

```yaml
- name: Assert wwwroot assets present in publish output
  shell: bash
  run: |
    test -f orchestrator/publish/wwwroot/js/htmx.min.js || {
      echo "FAIL: htmx.min.js not in publish output"
      exit 1
    }
    test -f orchestrator/publish/wwwroot/css/argus.css || {
      echo "FAIL: argus.css not in publish output"
      exit 1
    }
    echo "OK: wwwroot assets present in publish output"
```

### Image size impact

UI assets added in Phase 4: 3 new CSS rules (< 500 bytes) + inline JS (< 2 KB). Phases 1–3 already added `htmx.min.js` (~14 KB compressed) and `argus.css` (~13 KB). Phase 4 adds approximately 2.5 KB uncompressed — negligible. The existing `<2 GB` gate [VERIFIED: build.yml lines 117–142] requires no change.

---

## Research Question 5: DOCS.md Ingress UI Section Outline

### Current DOCS.md state

269 lines [VERIFIED: 04-CONTEXT.md reference]. DOCS.md currently covers: Prerequisites (Mosquitto, HA API), Installation, Configuration (all config fields), Troubleshooting, Support. [VERIFIED: argus/DOCS.md]

The YAML-based Configuration section (entities, include_patterns, exclude_patterns, detector_endpoint, etc.) should retain its current content for users who still manage YAML directly or use `config-gen`. The Ingress UI section is ADDITIVE — it describes the new UI workflow.

### DOCS.md Ingress UI section outline

Insert after the `## Installation` section and before `## Configuration`:

```
## Using the Ingress UI

> The Ingress UI replaces manual YAML editing for entity selection and detector configuration.
> You can configure Argus entirely through the UI with zero manual YAML editing.

### Opening the UI

### Selecting Entities

### Assigning Detectors

### Applying Changes (No Restart Required)
  - What "pipeline reload" means
  - HST warm-up note: ~4 minutes (window=250 at ~1 reading/s/entity), derived from River HST docs
  - MAD/STL have no comparable warm-up period (batch; trained on historical data)

### Recovering a Corrupted Configuration
```

### Corrupted-config recovery steps (verified against actual code)

The guard mechanism uses:
1. `.ui_config_present` lock file in the same directory as `entities.yaml` (written after each successful UI save — verified in Program.cs line 379–380)
2. `gen-entities.py` checks for `.ui_config_present`; if present, skips overwriting `entities.yaml` on add-on restart

Recovery scenario: user manually edits `/data/entities.yaml` and introduces a syntax/structural error, causing `EntitiesConfigLoader.Load()` to throw on the next restart.

**Recovery steps to document:**
1. Open the add-on **Log** tab — the error message identifies the YAML problem
2. Option A (fix YAML): SSH into the host, edit `/data/entities.yaml` directly to fix the error, then restart the add-on
3. Option B (reset to UI re-entry): delete `/data/entities.yaml` AND `/data/.ui_config_present`, then restart the add-on — the orchestrator starts with an empty pipeline; open the UI to re-configure
4. If `/data/.ui_config_present` is present but `entities.yaml` is missing or unreadable: `EntitiesConfigLoader.Load()` throws `FileNotFoundException` — this is the "corrupted state" that must be recovered by Option B

**Verified behavior:** `EntitiesConfigLoader.Load()` throws `FileNotFoundException` if the file doesn't exist (line 18: `throw new FileNotFoundException(...)`). It throws `InvalidOperationException` for structural errors (null entity, missing entity_id, no detectors) (lines 53–62). Both are caught by `Program.cs` startup (the load happens before `builder.Build()` — line 22). A startup-time load failure crashes the orchestrator — the add-on restarts per s6 policy. [VERIFIED: EntitiesConfigLoader.cs lines 18, 53–62; Program.cs lines 19–23]

**Important:** The Phase 1 decision changed `Validate()` from throw to `LogWarning` for empty entities. An empty `entities.yaml` (zero entities, valid structure) does NOT crash the orchestrator — it logs a warning and starts with an empty pipeline. Only structurally broken YAML crashes. [VERIFIED: EntitiesConfigLoader.cs lines 40–47]

---

## Research Question 6: End-to-end acceptance

The end-to-end acceptance test (configure entirely via UI, zero manual YAML, v2.0 unaffected) requires a live HA instance with the deployed add-on. This is a human-operated UAT item. It MUST NOT be automated in CI for Phase 4 — there is no HA Supervisor available in the GitHub Actions environment. [VERIFIED: 04-CONTEXT.md `<specifics>` section; 04-CONTEXT.md `<deferred>` section; Nyquist disabled project-wide]

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Atomic file rename | Custom temp+swap | `File.Move(tmp, target, overwrite: true)` | Already implemented in ConfigWriter — reuse |
| Config hot-reload | Custom polling loop | `FileSystemWatcher` + `Renamed` event + Timer debounce | Standard .NET pattern; inotify-backed on Linux |
| Param parsing for validation | Re-implement parsing | Reuse `DetectorFieldParser.Parse()` output dict | Already parsed before the validator insertion point |
| HTML encoding in error messages | Custom escaping | `WebUtility.HtmlEncode()` | Already used throughout EntityPickerPage |
| Double.Parse with invariant culture | `Convert.ToDouble` | `double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)` | Matches HstParams.From() pattern — no locale issues |

---

## Common Pitfalls

### Pitfall 1: FileSystemWatcher filter does not apply to Renamed OldName

**What goes wrong:** `FileSystemWatcher(dir, "entities.yaml")` sets `Filter = "entities.yaml"`. On some OS/runtime combinations, the filter matches `e.Name` (the destination) but not `e.OldName` (the source). However, for the temp→final rename pattern, `e.Name` is `entities.yaml` (the destination) and `e.OldName` is the temp file. The filter should correctly fire.

**Why it happens:** `FileSystemWatcher.Filter` on Linux (inotify) applies to both `IN_MOVED_FROM` and `IN_MOVED_TO` events. Some sources suggest the filter applies to the final name only. The watcher constructor `(dir, "entities.yaml")` means "watch for events involving files named `entities.yaml`" — this should match the `Renamed` event where the destination is `entities.yaml`.

**How to avoid:** Add an explicit guard inside the `Renamed` handler: `if (!string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase)) return;` — as shown in the handler example above. This is belt-and-suspenders and costs nothing.

**Warning signs:** If the watcher fires for temp file renames (`.entities.tmp.{guid}.yaml`) — the explicit guard prevents those from triggering a reload.

### Pitfall 2: FileSystemWatcher fires Changed in addition to Renamed

**What goes wrong:** `File.Move(overwrite: true)` onto an existing file may fire a `Changed` event for `entities.yaml` in addition to `Renamed`, depending on the filesystem and kernel version (inotify may emit `IN_ATTRIB` or `IN_MODIFY`).

**How to avoid:** Only subscribe to the `Renamed` event — do NOT subscribe to `Changed`. The watcher registration should be:
```csharp
watcher.Renamed += (_, e) => { ... };
// Do NOT add: watcher.Changed += ...
```

**Warning signs:** Double reloads; check by enabling debug logging for `ConfigFileWatcherService`.

### Pitfall 3: Timer fires after CancellationToken cancels the hosted service

**What goes wrong:** The 300ms debounce `Timer` fires its callback after `stoppingToken` is set (during app shutdown). The callback calls `EntitiesConfigLoader.Load()` and `liveCfg.Swap()` on a partially-torn-down DI container.

**How to avoid:** In `ExecuteAsync`, dispose the timer on `stoppingToken.WaitHandle.WaitOne()` before returning. Also, `Load()` can fail with `FileNotFoundException` (if the file was deleted during shutdown) — the `try/catch` in `Reload()` already handles this gracefully.

### Pitfall 4: Validation runs AFTER entity list is built with HST defaults

**What goes wrong:** If `InputValidator.Validate()` is called after the `entities` list is built (where empty detector lists are defaulted to HST), the validator runs on the defaulted list — not the raw submitted detectors. The user might submit zero detectors for an entity and the validator would see the HST default, hiding the issue.

**How to avoid:** Insert validation BEFORE the `entities` list building (step 6 in the save handler). Validate the raw `parsedDetectors` dictionary output from `DetectorFieldParser.Parse()`. This is the correct insertion point identified in Research Question 1 above.

### Pitfall 5: Cross-field high/low threshold validation with per-entity-per-detector indexing

**What goes wrong:** `parsedDetectors` is `Dictionary<int, List<DetectorConfig>>`. Each `DetectorConfig.Params` is `Dictionary<string, string>`. For cross-field validation (high > low), both keys must be present in the same `Params` dict. If a user submits `high_threshold` but not `low_threshold` (e.g., they cleared one field), the default-absent key would cause a KeyNotFoundException.

**How to avoid:** In the cross-field check, use `TryGetValue` for both keys. If either is absent, skip the cross-field check (the individual field check will catch the invalid/empty value separately).

### Pitfall 6: `id` attribute format change breaks existing CSS/JS

**What goes wrong:** Current param inputs use `id="p_{entityIdx}_{detIdx}_{key}"` (underscores, `p_` prefix). The 04-UI-SPEC calls for `param-{entity_idx}-{det_idx}-{key}` (hyphens, `param-` prefix). If some CSS or JS targets the old format, it will break.

**How to avoid:** Audit Phase 3 CSS in `argus.css` for any selectors targeting `p_` prefixed ids — there are NONE (CSS only uses class selectors). The JS added in Phase 4 uses the `name` attribute to identify field keys, not the `id` attribute. Safe to rename ids as specified. [VERIFIED: argus.css — no `#p_` id selectors present]

### Pitfall 7: `BuildSuccessBanner` must detect HST to show warm-up note

**What goes wrong:** `BuildSuccessBanner(int count)` currently only takes a count. To conditionally render the warm-up note, it must also receive a boolean (or the entity list itself) indicating whether any HST detector is present.

**How to avoid:** Change `BuildSuccessBanner(int count)` to `BuildSuccessBanner(int count, bool hasHst)`. Pass `entities.Any(e => e.Detectors.Any(d => d.Name.Equals("hst", StringComparison.OrdinalIgnoreCase)))` from the save handler.

---

## Code Examples

### InputValidator (server-side validation)

```csharp
// Source: 04-CONTEXT.md validation rules; HstParams.From() pattern from EntitiesConfig.cs
public static class InputValidator
{
    private static readonly Regex EntityIdRegex =
        new(@"^[a-z0-9_]+\.[a-z0-9_]+$", RegexOptions.Compiled);

    private static readonly string[] KnownDetectors = { "hst", "mad", "stl" };

    public static List<string> Validate(
        IEnumerable<string> resolvedIds,
        Dictionary<int, List<DetectorConfig>> parsedDetectors)
    {
        var errors = new List<string>();

        // Entity_id format check (tamper guard)
        foreach (var id in resolvedIds)
        {
            if (!EntityIdRegex.IsMatch(id))
                errors.Add($"Invalid entity ID '{WebUtility.HtmlEncode(id)}'. Use format domain.object_id.");
        }

        // Per-detector validation
        foreach (var (ei, detectors) in parsedDetectors)
        {
            foreach (var (di, det) in detectors.Select((d, i) => (i, d)))
            {
                var name = det.Name?.ToLowerInvariant() ?? "";

                if (!KnownDetectors.Contains(name))
                {
                    errors.Add($"Unknown detector type \"{WebUtility.HtmlEncode(det.Name)}\". Choose HST, MAD, or STL.");
                    continue; // skip param validation for unknown detector
                }

                errors.AddRange(ValidateParams(name, det.Params));
            }
        }

        return errors;
    }

    private static IEnumerable<string> ValidateParams(string detectorName, Dictionary<string, string> p)
    {
        return detectorName switch
        {
            "hst" => ValidateHst(p),
            "mad" => ValidateMad(p),
            "stl" => ValidateStl(p),
            _ => []
        };
    }

    // ... individual validators using double.TryParse with InvariantCulture
}
```

### Validation insertion in save handler

```csharp
// After: var parsedDetectors = DetectorFieldParser.Parse(formPairs);
// Insert BEFORE entity list building:

var validationErrors = InputValidator.Validate(resolvedIds, parsedDetectors);
if (validationErrors.Count > 0)
{
    logger.LogWarning(LogEvents.UiSaveFailed,
        "UI save blocked: {ErrorCount} validation error(s)", validationErrors.Count);
    return Results.Content(
        EntityPickerPage.BuildValidationBanner(validationErrors.Count),
        "text/html");
}
```

### FileSystemWatcher debounce pattern

```csharp
// Source: 04-CONTEXT.md decision; standard .NET pattern [ASSUMED]
Timer? _debounce = null;

watcher.Renamed += (_, e) =>
{
    if (!string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase)) return;
    // Reset timer: Dispose old + create new (avoids Change() race on a disposed timer)
    Interlocked.Exchange(ref _debounce, new Timer(
        _ => Reload(entitiesPath),
        null,
        TimeSpan.FromMilliseconds(300),
        Timeout.InfiniteTimeSpan))?.Dispose();
};
```

### CSS additions (to argus.css)

```css
/* ── Phase 4: Validation Error States ───────────────────────────────────── */

/* Error message span — hidden by default; revealed by --error modifier */
.argus-param-field__error-msg {
  display: none;
  font-size: var(--font-size-label);
  font-weight: var(--font-weight-semibold);
  line-height: var(--line-height-label);
  color: var(--color-status-error);
  margin-top: var(--space-xs);
}

/* Error modifier — applied by JS or server fragment */
.argus-param-field--error .argus-param-field__input {
  border-color: var(--color-status-error);
  outline: none;
}

.argus-param-field--error .argus-param-field__input:focus {
  border-color: var(--color-status-error);
}

.argus-param-field--error .argus-param-field__error-msg {
  display: block;
}

/* Validation rejection banner */
.argus-banner--validation {
  background-color: var(--color-status-error);
  color: #ffffff;
}

/* Warm-up disclosure note (inside success banner) */
.argus-warmup-note {
  font-size: var(--font-size-label);
  font-weight: var(--font-weight-semibold);
  line-height: var(--line-height-label);
  color: rgba(255, 255, 255, 0.85);
  margin-top: var(--space-xs);
}
```

### CI assertion step

```yaml
# Insert in build.yml AFTER "Publish orchestrator" step, BEFORE "Set up QEMU":
- name: Assert wwwroot assets present in publish output
  shell: bash
  run: |
    test -f orchestrator/publish/wwwroot/js/htmx.min.js || { echo "FAIL: htmx.min.js missing"; exit 1; }
    test -f orchestrator/publish/wwwroot/css/argus.css   || { echo "FAIL: argus.css missing";   exit 1; }
    echo "OK: wwwroot assets verified"
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Validate on read (structural only) | Validate on write (input ranges) + on read (structural) | Phase 4 | Prevents invalid data from reaching disk |
| ConfigWriter write → immediate UI Swap | ConfigWriter write → immediate UI Swap + deferred watcher Swap (harmless) | Phase 4 | Two ConfigChanged events per UI save; acceptable |
| Manual YAML documentation | UI workflow documented in DOCS.md | Phase 4 | Users can configure without YAML knowledge |

---

## Open Questions (RESOLVED)

1. **Does ConfigWriter's `File.Move` produce a `Renamed` event on Linux?**
   RESOLVED: `File.Move(tmp, target, overwrite: true)` maps to `rename(2)` on Linux, which raises `Renamed` in `FileSystemWatcher` (inotify `IN_MOVED_TO`). Standard behavior. The explicit `e.Name == "entities.yaml"` guard in the handler ensures correctness regardless.

2. **Is `orchestrator/publish/` committed to git or generated by CI?**
   RESOLVED: It is UNTRACKED (not committed, not gitignored). The CI `dotnet publish` step creates it fresh. wwwroot is absent from the local disk copy because that publish predates the Web SDK migration. The CI publish will include wwwroot correctly.

3. **Does the UI save's watcher bounce cause an infinite loop?**
   RESOLVED: No. The watcher calls `Load + Swap` (reads + memory), never `ConfigWriter.WriteAsync` (disk write). No write = no new `Renamed` event. The loop terminates after one 300ms bounce.

4. **Are there entity_id text inputs the client JS needs to validate?**
   RESOLVED: No. Entity_ids are checkbox values rendered by the server from the HA registry — not freeform text inputs. Client-side entity_id validation is not needed (server-side is a tamper guard only).

5. **Does the warm-up note need to detect HST specifically or always show?**
   RESOLVED: Conditionally — only when the saved config contains at least one HST detector. MAD/STL are batch detectors with no comparable warm-up. `BuildSuccessBanner(count, hasHst)` overload needed.

---

## Environment Availability

Phase 4 is a code/config-only change (no new external dependencies). The only external dependency added is `FileSystemWatcher` (built-in .NET 8). No new tools, services, or runtimes required beyond what Phases 1–3 already depend on.

| Dependency | Required By | Available | Notes |
|------------|------------|-----------|-------|
| `System.IO.FileSystemWatcher` | ConfigFileWatcherService | .NET 8 built-in | No install needed |
| `System.Threading.Timer` | 300ms debounce | .NET 8 built-in | No install needed |
| Linux inotify | FileSystemWatcher on the add-on host | HA OS (Linux-based) | Standard on all HA OS installations |

**Missing dependencies with no fallback:** None.

---

## Security Domain

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | No | Interim Supervisor-IP auth deferred (Phase 4 deferred) |
| V3 Session Management | No | — |
| V4 Access Control | No | `IsAuthorizedRequest` already gates all endpoints |
| V5 Input Validation | **YES** | `InputValidator` (new) + `EntitiesConfigLoader.Validate()` (existing) |
| V6 Cryptography | No | No crypto operations in Phase 4 |

### Known Threat Patterns

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Tampered POST body with invalid entity_id | Tampering | `InputValidator` entity_id regex check (server-side) |
| Tampered POST body with unknown detector name | Tampering | `InputValidator` KnownDetectors allowlist check |
| Tampered POST body with out-of-range params | Tampering | `InputValidator` per-type numeric range checks |
| Stored XSS via param values in error messages | XSS | `WebUtility.HtmlEncode()` on all user-originated strings before interpolation (already enforced by T-02-07, T-03-11) |
| FileSystemWatcher path traversal | Elevation of Privilege | Watcher watches fixed directory from `EntitiesPath`; no user input affects the watch path |

**Key:** `InputValidator` is the new security surface. Its error messages must HTML-encode user-submitted values (`WebUtility.HtmlEncode(det.Name)` for unknown detector name, entity_id values in error messages). This follows the existing T-02-07/T-03-11 pattern already enforced in `EntityPickerPage.cs`.

---

## Project Constraints (from CLAUDE.md)

| Constraint | Impact on Phase 4 |
|------------|------------------|
| .NET 8 orchestrator | All server code targets .NET 8; `FileSystemWatcher` and `Timer` are .NET 8 built-ins |
| No new NuGet packages (implied by BSD/Apache/MIT license constraint + "no new deps" intent) | `InputValidator` uses only BCL; `FileSystemWatcher` is BCL; no new packages needed |
| All ML stays in Python | No ML work in Phase 4 |
| No `NetDaemon.Runtime`/`AppModel` | Not relevant to Phase 4 |
| Languages: code in English, HA entity names in Polish | Error messages, log messages, comments in English. Validation copy confirmed English-only in 04-UI-SPEC copywriting contract |
| Licenses: BSD/Apache/MIT only | No new dependencies → no new license review needed |
| `grpc.aio` not `grpc.experimental.aio` | Not relevant to Phase 4 |
| No direct Recorder DB access | Not relevant to Phase 4 |
| No MQTTnet v4.x | Not relevant to Phase 4 |
| GSD workflow enforcement | Phase is being planned via GSD; no direct edits outside GSD workflow |

---

## Sources

### Primary (HIGH confidence — code-verified)
- `orchestrator/Argus.Orchestrator/Program.cs` — save handler sequence, insertion point for validation
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs` — HstParams defaults, DetectorConfig structure
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` — existing Validate(), Load() throw behavior
- `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` — `File.Move(overwrite:true)` = atomic rename
- `orchestrator/Argus.Orchestrator/Config/LiveEntitiesConfig.cs` — Swap() = idempotent; fires ConfigChanged
- `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` — current param grid HTML; no error-msg spans yet; BuildSuccessBanner signature
- `orchestrator/Argus.Orchestrator/Web/DetectorFieldParser.cs` — parse output structure (Dict<int, List<DetectorConfig>>)
- `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` — existing `.argus-btn[disabled]` rule; `.argus-banner--reloading` precedent; `.argus-param-field` structure; no `#p_` id selectors
- `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` — available EventIds (7003 = UiSaveFailed for validation failure log)
- `orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj` — `Microsoft.NET.Sdk.Web` confirmed
- `.github/workflows/build.yml` — dotnet publish step (line 31–33); <2 GB gate (lines 117–142)
- `argus/Dockerfile` — `COPY orchestrator/publish/ /opt/argus/orchestrator/` (line 51)
- `argus/DOCS.md` — existing 269-line structure; no Ingress UI section present
- `.planning/phases/04-validation-ci-packaging-documentation/04-CONTEXT.md` — all decisions locked
- `.planning/phases/04-validation-ci-packaging-documentation/04-UI-SPEC.md` — CSS specs, JS contract, copywriting

### Secondary (MEDIUM confidence — standard .NET patterns)
- Standard `FileSystemWatcher` + `BackgroundService` pattern — widely documented for .NET hosted services
- `Microsoft.NET.Sdk.Web` wwwroot publish behavior — standard SDK behavior for static web assets

### Tertiary (LOW confidence — marked [ASSUMED])
- `File.Move` → `rename(2)` → `Renamed` FileSystemWatcher event on Linux: [ASSUMED] — standard cross-platform .NET IO mapping; defensively guarded by explicit `e.Name` check in handler
- `NotifyFilters.FileName` captures `IN_MOVED_TO`: [ASSUMED] — standard inotify/FSW mapping

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new dependencies; all BCL
- Architecture: HIGH — verified against actual source files
- Pitfalls: HIGH — derived from reading actual code (param grid HTML structure, save handler sequence, Swap implementation)
- CI wwwroot: HIGH — SDK type verified; local publish absence explained by untracked status

**Research date:** 2026-07-01
**Valid until:** Stable — all findings grounded in committed source files; no third-party library versioning concerns

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `File.Move(overwrite: true)` produces a `Renamed` FSW event on Linux (not `Created`+`Deleted`) | Research Q3 | Watcher never fires for UI save or external edits; feature dead — mitigated by integration test in UAT |
| A2 | `NotifyFilters.FileName` captures `IN_MOVED_TO` (rename destination) in addition to `IN_MOVED_FROM` | Research Q3 | Watcher fires for old-name events only; explicit `e.Name == "entities.yaml"` guard would still filter correctly but event might not arrive at all if filter mis-applies |
| A3 | `Microsoft.NET.Sdk.Web` `dotnet publish` automatically includes `wwwroot/` content files in output | Research Q4 | wwwroot absent from publish output; CI assertion step would catch this immediately before docker build |
| A4 | ASP.NET Core 8 `UseStaticFiles()` serves from `{ContentRoot}/wwwroot` where `ContentRoot` = directory containing the .dll after dotnet publish | Research Q4 | Static assets return 404; caught at first smoke test |
| A5 | `FileSystemWatcher` `Renamed` handler (via `Timer`) calling `liveCfg.Swap()` on a thread-pool thread is safe | Research Q3 | Race condition between two concurrent Swaps; mitigated by `Interlocked.Exchange` in `LiveEntitiesConfig.Swap()` which is already thread-safe |
