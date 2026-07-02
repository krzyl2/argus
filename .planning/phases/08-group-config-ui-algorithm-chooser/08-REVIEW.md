---
phase: 08-group-config-ui-algorithm-chooser
reviewed: 2026-07-02T19:53:32Z
depth: standard
files_reviewed: 21
files_reviewed_list:
  - detector/argus_detector/group/peer_divergence.py
  - detector/argus_detector/group/multivariate_detector.py
  - detector/argus_detector/registry.py
  - detector/argus_detector/servicer.py
  - orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs
  - orchestrator/Argus.Orchestrator/Web/GroupSaveRequest.cs
  - orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs
  - orchestrator/Argus.Orchestrator/Batch/GroupStatusCache.cs
  - orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs
  - orchestrator/ui/src/state/groups.ts
  - orchestrator/ui/src/state/groupEditor.ts
  - orchestrator/ui/src/validation/groupParams.ts
  - orchestrator/ui/src/api/client.ts
  - orchestrator/ui/src/api/types.ts
  - orchestrator/ui/src/components/AlgorithmChooser.tsx
  - orchestrator/ui/src/components/SensitivityPresetPicker.tsx
  - orchestrator/ui/src/components/AdvancedParamsDisclosure.tsx
  - orchestrator/ui/src/components/AttributionPanel.tsx
  - orchestrator/ui/src/components/GroupEditorForm.tsx
  - orchestrator/ui/src/components/GroupListRow.tsx
  - orchestrator/ui/src/components/AreaSuggestionBanner.tsx
findings:
  critical: 3
  warning: 4
  info: 2
  total: 9
status: issues_found
---

# Phase 08: Code Review Report

**Reviewed:** 2026-07-02T19:53:32Z
**Depth:** standard
**Files Reviewed:** 21 (source files reviewed; note config lists 22 paths but `groupParams.ts` and the components together total 21 unique existing files after dedup — see file list above for the exact set read)
**Status:** issues_found

## Summary

Przejrzano ścieżkę end-to-end konfiguracji grup: Python-owe detektory grupowe (`peer_divergence`, `multivariate_detector`), rejestr/serwisant gRPC, walidację i zapis po stronie C# (`GroupInputValidator`, `Program.cs`), oraz UI Preact (chooser algorytmu, presety, formularz edycji grupy, panel atrybucji).

Największy problem: **presety sensitivity dla trybu joint (ecod/copod/pca/iforest) są autentyczne tylko przy pierwszym Fit** — każdy kolejny nocny re-fit z nowymi parametrami po stronie operatora jest po cichu ignorowany, bo `DetectorRegistry.fit_one` klonuje (`deepcopy`) już istniejący, wcześniej skonstruowany model zamiast odtworzyć go z nowymi parametrami. To bezpośrednio łamie kontrakt ALGO-01/02 "preset genuineness" dla wszystkich detektorów joint (peer_divergence jest bezpieczny — konstruowany od nowa przy każdym `ScoreGroupBatch`).

Drugi poważny problem: formularz UI pokazuje operatorowi błąd walidacji "Choose an algorithm to continue", ale **nie blokuje faktycznie przycisku Save** — `hasErrors` pomija `noAlgorithmError`. W efekcie `saveGroup()` po cichu podstawia `detector: 'peer_divergence'` mimo że operator nigdy go nie wybrał, co w połączeniu z brakiem walidacji spójności `mode`/`detector` po stronie serwera pozwala zapisać grupę `mode="joint"` z `detector="peer_divergence"`. To skutkuje publikowaniem sfabrykowanego werdyktu grupy (score=0.0, isAnomaly=false) zamiast błędu — provable data-integrity bug w `BatchSchedulerWorker.RunGroupBatchAsync`.

Dodatkowo: brak wykrywania kolizji `groupId` przy zapisie (klient robi upsert po `groupId`, serwer nigdy nie odrzuca duplikatu) pozwala jednej grupie po cichu nadpisać inną, jeśli dwie różne nazwy zesluggifikują się identycznie.

Punkty potwierdzone jako POPRAWNE (żeby nie zgadywać w drugą stronę): sortowanie kontrybucji przed cache'owaniem w `GroupStatusCache`/`BatchSchedulerWorker` (GRP-09) jest poprawne; `IsAuthorizedRequest` jest wywoływane jako pierwsze na wszystkich 4 nowych endpointach; `client.ts` wymusza relative fetch bez wiodącego `/`; floor=3 i unit-consistency są spójne między Python/C#/TS; `peer_divergence.from_params(threshold)` faktycznie zmienia, który członek zostaje oflagowany (parametr wątku poprawnie przechodzi przez `ScoreGroupBatch` do świeżo konstruowanego detektora za każdym wywołaniem); `contamination` jest uczciwie opisany jako nie-zmieniający score (tylko threshold) w `DetectorCatalog.cs`.

## Critical Issues

### CR-01: Joint-mode sensitivity presets are cosmetic after the first fit — params never re-applied on re-fit

**File:** `detector/argus_detector/registry.py:160-169`
**Issue:** `fit_one()` for non-stateless detectors (ecod/copod/pca/iforest) does:
```python
with lock:
    current = self._detectors.get(key)
candidate = copy.deepcopy(current) if current else self._create_detector(detector, params)
candidate.fit(values)
```
When `current` already exists (i.e. every re-fit after the group's very first `FitGroup` call — including every subsequent nightly re-fit), the incoming `params` argument is **silently discarded**. `copy.deepcopy(current)` clones the *already-constructed* PyOD model object, whose `contamination`/`n_estimators` were baked into its constructor at the first-ever fit. `GroupMultivariateDetector.fit()` (`multivariate_detector.py:111-116`) then calls `self._model.fit(Xs)` on that same object — it never reconstructs `self._model` from `_DETECTOR_FACTORY[detector_name](params)`.

Concrete consequence: an operator changes a joint-mode group's sensitivity from Low→High (`contamination` 0.05→0.2) in the UI and saves. The catalog/preset UI honestly claims this changes the anomaly-flag threshold (ALGO-01/02 honesty note in `DetectorCatalog.cs:36-39`). In reality, the NEXT nightly `FitGroup` call re-fits the **old** PyOD object (still `contamination=0.05`) on new data, and the new `contamination=0.2` param is never applied — for the lifetime of the orchestrator process (until restart clears the in-memory registry). The preset picker is genuine for `peer_divergence` only; for the four joint detectors it is cosmetic after the very first fit.

**Fix:** Either (a) always reconstruct via `_create_detector(detector, params)` for joint detectors instead of deep-copying `current` (joint detectors have no meaningful "warm start" state worth preserving across a param change — `RobustScaler`+PyOD refit from scratch every nightly cycle anyway), or (b) compare incoming `params` against the params used to construct `current` and force reconstruction on a mismatch:
```python
candidate = (
    self._create_detector(detector, params)
    if current is None or detector in ("ecod", "copod", "pca", "iforest")
    else copy.deepcopy(current)
)
candidate.fit(values)
```
Simplest correct fix: for the four joint-multivariate detector names, always call `_create_detector` (they are refit from scratch nightly regardless — there is no incremental/warm-start capability being preserved by the deepcopy path for these algorithms).

### CR-02: "Choose an algorithm to continue" validation does not actually block Save — silently defaults to peer_divergence

**File:** `orchestrator/ui/src/components/GroupEditorForm.tsx:58,62` and `orchestrator/ui/src/state/groups.ts:114`
**Issue:**
```tsx
const noAlgorithmError = draftDetector.value === null ? 'Choose an algorithm to continue.' : null;
...
const hasErrors = !!memberFloorError || !!unitMismatchError || !!nameError; // noAlgorithmError NOT included
```
`<SaveBar disabled={saving || hasErrors} .../>` therefore stays enabled even when `noAlgorithmError` is shown. If the operator saves without picking an algorithm, `saveGroup()` runs:
```ts
detector: draftDetector.value ?? 'peer_divergence',
```
This silently persists `detector: "peer_divergence"` with empty `params: {}` — a choice the operator never made and was actively told ("Choose an algorithm to continue") they had not yet made. This contradicts the domain rule that guided-flow suggestions are approve-only and nothing auto-applies without an explicit pick.

**Fix:**
```tsx
const hasErrors = !!memberFloorError || !!unitMismatchError || !!nameError || !!noAlgorithmError;
```
And drop the `?? 'peer_divergence'` fallback in `groups.ts` — `saveGroup()` should refuse to build a request when `draftDetector.value` is `null` (mirrors the now-enforced UI gate), e.g. return early or throw before hitting the network.

### CR-03: No mode/detector consistency check — a joint-mode group can be saved with detector="peer_divergence" (or vice versa), causing a fabricated verdict to be published

**File:** `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs:66-89` (missing check) and `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:197,237-241`
**Issue:** `GroupInputValidator.Validate` checks `Mode` is one of the two known values and, for peer mode, checks unit consistency — but never checks that `Detector` is compatible with `Mode` (`peer_divergence` mode requires `detector == "peer_divergence"`; `joint` mode requires `detector` ∈ {ecod, copod, pca, iforest}). Combined with CR-02 (client can submit `detector=null` → `"peer_divergence"` regardless of the chosen `mode`), a group with `mode="joint"`, `detector="peer_divergence"` can reach disk.

At batch time, `RunGroupBatchAsync` branches purely on `Mode`:
```csharp
var isPeer = string.Equals(group.Mode, "peer_divergence", ...); // true only for Mode=="peer_divergence"
...
var response = await _detectorClient.ScoreGroupBatchAsync(request, ct); // request.Detector = "peer_divergence"
...
else // isPeer == false, i.e. Mode == "joint"
{
    var v = response.GroupVerdict;                    // proto default Verdict (never set by servicer for peer_divergence)
    await _statePublisher.PublishGroupScoreAsync(group.GroupId, null, v.Score ?? 0.0, ct);   // publishes 0.0
    await _statePublisher.PublishGroupFlagAsync(group.GroupId, null, v.IsAnomaly, ct);        // publishes false
```
`servicer.py`'s `ScoreGroupBatch` dispatches on `request.detector` alone (not on any mode field — there is no mode field in the RPC), so for `detector="peer_divergence"` it always populates `per_member` and leaves `group_verdict` unset. In generated C# protobuf code, an unset singular message field resolves to a non-null default-valued instance (`Score=null`→`?? 0.0`, `IsAnomaly=false`), not a null/exception. The orchestrator therefore publishes a **fabricated "score=0.0, not anomalous" verdict** for the group to HA via MQTT every cycle, instead of erroring or skipping — a silent false-negative data-integrity issue for a misconfigured group.

**Fix:** Add to `GroupInputValidator.Validate`:
```csharp
var jointDetectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ecod", "copod", "pca", "iforest" };
if (isPeerDivergence && !string.Equals(group.Detector, "peer_divergence", StringComparison.OrdinalIgnoreCase))
{
    errors.Add($"Group '{group.GroupId}' is in peer-divergence mode but has detector '{group.Detector}'.");
}
else if (isJoint && !jointDetectors.Contains(group.Detector))
{
    errors.Add($"Group '{group.GroupId}' is in joint mode but has an incompatible detector '{group.Detector}'.");
}
```
This closes the gap even if CR-02 is also fixed independently (defense in depth — server is the authoritative boundary per this file's own docstring).

## Warnings

### WR-01: No duplicate-groupId detection on save — one group can silently overwrite another

**File:** `orchestrator/ui/src/state/groups.ts:107-121` and `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs` (missing check)
**Issue:** `saveGroup()` upserts by `groupId` client-side (`findIndex` + replace-or-append), and `GroupEditorForm.tsx:87` auto-derives `groupId` from the friendly name via `slugify()` for new groups. Two different friendly names that slugify to the same id (e.g. "Kitchen!" and "kitchen  " both → `"kitchen"`) silently collide: creating the second group overwrites the first group's full config (members, mode, detector, params) with no warning, because `GroupInputValidator.Validate` never checks for duplicate `GroupId` values within the submitted list, and the client-side `findIndex` treats the collision as "editing the same group."
**Fix:** Add a duplicate-groupId check in `GroupInputValidator.Validate` (reject the whole save with an error listing the colliding id), and/or have the client warn before creating a new group whose derived slug already exists in `groups.value`.

### WR-02: Joint-detector params are never bounds-validated server-side — catalog Min/Max are UI-only decoration

**File:** `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs` (missing check), `detector/argus_detector/group/multivariate_detector.py:45-64`
**Issue:** `DetectorCatalog.cs`'s `ParamFieldSchema(Key, Type, Min, Max, Step)` is rendered as HTML `min`/`max`/`step` attributes in `AdvancedParamsDisclosure.tsx` — a browser-UI hint only, trivially bypassed via a direct `POST /api/groups/save`. `GroupInputValidator.Validate` never checks `Params` values against the catalog's declared bounds. On the Python side, `_cast_float`/`_cast_int` only guard against non-numeric strings, not out-of-range values (e.g. `contamination` outside PyOD's valid `(0, 0.5]`). An out-of-range value causes a `ValueError` deep inside PyOD's constructor at fit time, caught by `servicer.py`'s outer `except Exception` in `FitGroup` (returns `ok=False, error=...`), so the group's nightly fit silently and indefinitely fails with no operator-visible feedback beyond a buried orchestrator log line.
**Fix:** Validate `Params` against `DetectorCatalog.All()`'s `ParamSchema` bounds inside `GroupInputValidator.Validate` before writing to disk (reject the save with a clear message), mirroring the existing "server is the authoritative boundary" pattern already used for member-floor/unit checks in this same file.

### WR-03: AttributionPanel does not URL-encode groupId in the status poll path

**File:** `orchestrator/ui/src/components/AttributionPanel.tsx:29`
**Issue:** `` `api/groups/${groupId}/status` `` builds the fetch path without `encodeURIComponent`, unlike `GroupListRow.tsx:59`'s `` href={`#/groups/${encodeURIComponent(group.groupId)}`} ``. `entities.yaml` can contain a hand-edited `group_id` with characters not restricted to the client's `slugify()` charset (only `Validate`'s non-whitespace check applies server-side), so a `groupId` containing `/`, `?`, or `#` would corrupt the request path or silently poll the wrong resource.
**Fix:**
```ts
const res = await apiGet<GroupStatusResponse>(`api/groups/${encodeURIComponent(groupId)}/status`);
```

### WR-04: peer_divergence group saved with a joint-only detector is a silent no-op, not a validation error

**File:** `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:225-236`, `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:486-489`
**Issue:** The inverse of CR-03 (`mode="peer_divergence"` + `detector="ecod"`, also currently allowed) does not corrupt data, but silently degrades to a permanent no-op: `RunNightlyFitAsync` skips fitting any group whose `Mode == "peer_divergence"` (line 488-489), so the `ecod` model backing this misconfigured group is never fitted. Every subsequent `ScoreGroupBatch` call then hits the "no fitted model" `context.abort(INVALID_ARGUMENT)` path in `servicer.py`, resulting in an `RpcException` that surfaces as a caught, logged batch failure every single cycle forever, with `response.PerMember.Count == 0` — the group is effectively dead with no clear top-level explanation pointing at the actual root cause (mode/detector mismatch). Same root cause as CR-03; fixing the validator check there (mode/detector consistency) eliminates this case too.
**Fix:** Covered by the CR-03 fix (mode/detector consistency validation) — no separate fix needed once that lands.

## Info

### IN-01: `ScoreGroupBatch`'s contribution/member_id zip relies on outer try/except rather than an explicit length check

**File:** `detector/argus_detector/servicer.py:305-315`
**Issue:** `feature_contributions` is built via `range(len(member_ids))` indexing into `last_contribution[i]`. If `contributions`' row width ever diverged from `len(member_ids)` (not currently reachable given the upstream ragged-input guard and `zip(*...)` construction, but a latent trap for future refactors), this would raise an uncaught `IndexError` inside the `try` block at line 239, caught generically at line 323 and turned into `ok=False`. Acceptable today only because the invariant is enforced far upstream and implicitly, not locally.
**Fix:** Optional — add an explicit `assert len(last_contribution) == len(member_ids)` or defensive length check with a clear error message, for the benefit of future maintainers who may not trace the upstream invariant.

### IN-02: `files_reviewed_list` count note

**File:** N/A (review scope bookkeeping)
**Issue:** The config's `files` list contains 22 entries but `groupParams.ts` was reviewed as validation logic embedded in the "Client validation" attention point alongside 21 component/state/API files — all 22 requested paths were confirmed to exist and were read; no file was skipped.
**Fix:** N/A — informational only.

---

_Reviewed: 2026-07-02T19:53:32Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
