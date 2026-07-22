---
status: testing
phase: 14-unified-detectors-screen-add-detector-wizard
source: [14-VERIFICATION.md]
started: 2026-07-21T18:54:54Z
updated: 2026-07-22T13:03:20Z
---

## Current Test

[testing complete]

## Tests

### 1. Add-detector wizard end-to-end — 1-sensor exit
expected: Lands on /detectors/sensor/<entityId> with the SingleDetectorEditorForm showing that sensor's detector-assignment UI; the sensor appears as a tracked row back on /detectors after saving. (Also confirm the Untrack action lives here in the editor, not on the list row — D-08a.)
result: pass
reported: "dziwne błędy. zapisuję detektor, ale nie pojawia się na liście detektorów. raz się pojawił po zapisie ale po odświeżeniu zniknął."
severity: blocker
resolution: "Fixed by 14-05 (commits dda65e7 + 02e1ae0) and RE-VERIFIED LIVE on add-on 2.1.5 via browser: seeded a peer_divergence group, did a single-sensor save (POST /api/sensors/save), then full refresh — the group SURVIVED (GET /api/groups still returns it) and the newly saved sensor (sensor.lazienka_temp_temperature) stayed tracked (GET /api/sensors isTracked=true). Both root causes closed end-to-end."
severity_original: blocker
diagnosis: |
  Reproduced live (2.1.4) via browser. Saving a single-sensor detector (POST /api/sensors/save)
  returns 200 + "2 entities tracked" (runtime/in-memory), but on full refresh GET /api/sensors
  shows the new sensor isTracked=false AND GET /api/groups returns {"groups":[]} — the pre-existing
  group "Ciśnienie w oponach" was WIPED.

  === SYMPTOM #1 — groups wiped (CONFIRMED, code-proven) ===
  Root cause: /api/sensors/save rebuilds the entire entities.yaml root dict as
  { _patterns, entities } with NO "groups" key (Program.cs:410-414), then
  EntitiesConfigLoader.Load + liveCfg.Swap (Program.cs:435-436). Every sensor save therefore
  drops all groups from disk AND from live config. This is the exact inverse of
  /api/groups/save [handler 8] (Program.cs:521-556), which reads the CURRENT entities.yaml and
  replaces only the top-level groups: key, preserving entities:/_patterns:.
  Minimal fix: before serializing in /api/sensors/save, read the current entities.yaml (or
  liveCfg.Get().Groups) and add ["groups"] = existing groups to the root dict (read-modify-write),
  mirroring Program.cs:521-556 exactly. The two saves become symmetric: sensors save preserves
  groups:, groups save preserves entities:/_patterns:.

  === SYMPTOM #2 — new sensor not durable across refresh (NOW ISOLATED, code-proven) ===
  The write is CORRECT — the sensor IS written to disk. count=2 == entities.Count
  (Program.cs:439,444); GlobExpander.Resolve with empty include/exclude + manuallyChecked=
  [biuro,kurnik] resolves both (GlobExpander.cs:84-88), so entities.yaml on disk = [biuro,kurnik].
  The defect is in the READ path: GET /api/sensors sources isTracked from the HA sensor
  registry's IsTracked flag (Program.cs:267), which is computed ONLY inside
  HaSensorRegistry.UpdateSnapshot (HaSensorRegistry.cs:55, trackedEntityIds.Contains(id)).
  UpdateSnapshot is called ONLY from NetDaemonHaEventSource on a live HA WebSocket (re)connect
  (NetDaemonHaEventSource.cs:143). liveCfg.Swap fires ConfigChanged, whose subscribers only
  (a) rebuild _configuredEntities — the STREAMING FILTER, not the registry
  (NetDaemonHaEventSource.cs:64-65); (b) restart the pipeline (HaListenerWorker); (c) republish
  MQTT (MqttPublisherWorker). NONE reconcile the registry's IsTracked (grep: UpdateSnapshot has
  a single call site, NetDaemonHaEventSource:143). So the registry's IsTracked is a lagging,
  reconnect-only snapshot that is NOT updated by the save — the note "registry, updated by Swap"
  in the artifacts is FALSE and is exactly the trap.
  Frontend confirmation: DetectorList filters sensorRows by
  (entityEdits[id]?.isTracked ?? s.isTracked) (state/detectors.ts:29-30). After a full refresh
  entityEdits is re-seeded from GET /api/sensors' isTracked (state/sensors.ts:54-84,
  getOrInitEdit), so the row collapses to the stale server flag (false) and is filtered out.
  Pre-refresh it showed because the wizard's setTracked set the client edit to true
  (AddDetectorWizard.tsx:37) — exactly "raz się pojawił po zapisie ale po odświeżeniu zniknął".
  Two sources of truth: GET /api/groups reads the authoritative liveCfg.Get().Groups
  (Program.cs:462, always fresh); GET /api/sensors reads the registry snapshot (stale until an
  HA reconnect re-runs UpdateSnapshot).
  Minimal fix: derive isTracked in GET /api/sensors from liveCfg.Get().Entities (build an
  OrdinalIgnoreCase HashSet of entity ids at request time; isTracked = set.Contains(e.EntityId))
  instead of e.IsTracked — mirror GET /api/groups reading liveCfg. This makes the tracked read
  consistent with the config the save writes+swaps, with no dependency on HA reconnect timing.

  gen-entities.py was RULED OUT: it regenerates entities.yaml from options.json's `entities`
  list (gen-entities.py:30), NOT from _patterns, and is guarded by /data/.ui_config_present
  (cont-init.d/10-config-gen.sh:112, written by the save at Program.cs:429-430) and only runs at
  add-on start — not on a browser refresh.

  SECONDARY (aggravating, not the root): ConfigFileWatcherService watches entities.yaml for the
  atomic-rename ConfigWriter produces (ConfigWriter.cs:26) and fires a SECOND liveCfg.Swap ~300ms
  after every save (ConfigFileWatcherService.cs:61-64,97-102), on top of the explicit Swap at
  Program.cs:436 — two ConfigChanged -> two pipeline restarts -> two rapid HA reconnects per save.
  Because the registry only refreshes on reconnect, this churn makes the stale read
  non-self-healing/racy. Fix #2 (config-sourced read) makes reconnect timing irrelevant to
  correctness; dropping the redundant self-write Swap is optional hardening.

### 2. Add-detector wizard end-to-end — >=2-sensor exit
expected: From /detectors -> Add detector, select >=2 sensors -> "Create group". Lands on /groups/new with GroupEditorForm's member picker pre-filled with the selected entity ids; the operator can then proceed through the existing AlgorithmChooser -> GuidedFlowStep -> SensitivityPresetPicker flow unmodified.
result: pass

### 3. Visual / Design-System fidelity of the unified list, wizard, and relocated Settings section
expected: Row spacing/rhythm, badge tones, button labels, and section rhythm match the Argus Design System reference (ui_kits/admin/index.html / HANDOFF_TO_CLAUDE_CODE.md); the group-row and sensor-row variants read as ONE consistent list (not two stitched-together lists); the new Settings "Auto-track patterns" section does not disturb the existing three read-only sections; both light and dark themes render with no unstyled regions.
result: skipped
reason: "Requires human design-fidelity judgment against the reference kit. Partially observed via browser: dark-theme /detectors renders cleanly (sidebar, heading, blue Add-detector button, tracked badge, Edit link — no unstyled regions); earlier snapshot showed group-row + sensor-row rendering in one list. Full verdict deferred — the group-row variant needed for list-consistency was wiped by the test-1 data-loss bug (G-14-1), and Settings 'Auto-track patterns' section + light-theme + reference-fidelity were not exhaustively compared."

## Summary

total: 3
passed: 2
issues: 0
pending: 0
skipped: 1
blocked: 0

## Gaps

- gap_id: G-14-1
  truth: "After saving a single-sensor detector, the sensor persists as a tracked row on /detectors across refresh — AND existing groups are NOT destroyed by the save."
  status: resolved
  resolved_by: 14-05-PLAN.md
  resolved_at: 2026-07-22
  verified_live: "add-on 2.1.5 — seeded peer_divergence group, single-sensor save via UI, full refresh: group survived (GET /api/groups) + saved sensor stayed tracked (GET /api/sensors). Both symptoms closed."
  reason: "Reproduced live (2.1.4): single-sensor save wipes the pre-existing group (GET /api/groups -> {groups:[]}) and the new sensor is isTracked=false after refresh, despite a 200 'Saved — 2 entities tracked' response."
  severity: blocker
  test: 1
  artifacts:
    - "orchestrator/Argus.Orchestrator/Program.cs:410-414 — /api/sensors/save builds entities.yaml root as { _patterns, entities } with NO groups key, then liveCfg.Swap() (Program.cs:435-436) => wipes groups (symptom #1)"
    - "orchestrator/Argus.Orchestrator/Program.cs:521-556 — /api/groups/save DOES preserve entities/_patterns by reading current file first (the correct read-modify-write pattern to mirror)"
    - "Program.cs:267 GET /api/sensors isTracked=e.IsTracked — registry snapshot, NOT updated by Swap (the 'updated by Swap' note is FALSE)"
    - "HaSensorRegistry.cs:55 IsTracked=trackedEntityIds.Contains(id); NetDaemonHaEventSource.cs:143 UpdateSnapshot is the ONLY registry writer, called only on live HA (re)connect"
    - "NetDaemonHaEventSource.cs:64-65 ConfigChanged rebuilds _configuredEntities (streaming filter only), does NOT touch the registry — so GET /api/sensors reads a lagging snapshot (symptom #2)"
    - "Program.cs:462 GET /api/groups reads authoritative liveCfg.Get().Groups (always fresh — the source GET /api/sensors should also read)"
    - "state/detectors.ts:29-30 + state/sensors.ts:54-84 — after refresh the list row collapses to the stale server isTracked and is filtered out (matches 'zniknął po odświeżeniu')"
    - "RULED OUT: gen-entities.py:30 reads options.json not _patterns; guarded by .ui_config_present (cont-init.d/10-config-gen.sh:112); runs only at add-on start, not on refresh"
    - "AGGRAVATING: ConfigFileWatcherService.cs:61-64,97-102 fires a 2nd Swap per save (on ConfigWriter's atomic rename, ConfigWriter.cs:26) => reconnect churn that makes the stale read non-self-healing"
  missing:
    - "Symptom #1 fix: /api/sensors/save must read the CURRENT entities.yaml (or liveCfg.Get().Groups) and add the top-level groups: key to the root dict (read-modify-write) — mirror /api/groups/save Program.cs:521-556 — instead of dropping it"
    - "Symptom #2 fix (ROOT CAUSE ISOLATED): GET /api/sensors must derive isTracked from liveCfg.Get().Entities (OrdinalIgnoreCase HashSet of entity ids, computed at request time) instead of e.IsTracked — mirror GET /api/groups reading liveCfg (Program.cs:462). The write already persists the sensor correctly; only the read is sourced from the stale registry snapshot."
    - "Optional hardening: reconcile registry IsTracked on ConfigChanged, and drop the redundant ConfigFileWatcher Swap for orchestrator self-writes to stop the 2-reconnects-per-save churn."
