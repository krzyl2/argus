---
phase: 04-validation-ci-packaging-documentation
plan: 01
subsystem: web-ui
tags: [validation, security, server-side, unit-tests, input-validation]
dependency_graph:
  requires:
    - "04-02: BuildValidationBanner(int) + BuildSuccessBanner(int, bool) on EntityPickerPage"
  provides:
    - "InputValidator.Validate(IEnumerable<string>, Dictionary<int,List<DetectorConfig>>) — server-side validation gate"
    - "LogEvents.UiValidationBlocked = new(7008, ...) in 7xxx block"
    - "POST /api/sensors/save blocked on invalid input — no write, no swap, no lock-file"
    - "BuildSuccessBanner call site wired with real hasHst — warm-up note now renders when HST present"
  affects:
    - "orchestrator/Argus.Orchestrator/Program.cs — validation gate + hasHst success-banner upgrade"
tech_stack:
  added: []
  patterns:
    - "Static validator class with TryGetDouble(InvariantCulture) + TryGetInt + ValidateIntAtLeast helpers"
    - "KnownDetectors allowlist (string[]) + EntityIdRegex (RegexOptions.Compiled)"
    - "Cross-field high > low threshold check skipped when either key absent (Pitfall 5 guard)"
    - "WebUtility.HtmlEncode on all user-submitted strings in error messages (T-04-04)"
    - "InputValidator.Validate called on raw parsedDetectors before entity-list defaulting"
key_files:
  created:
    - orchestrator/Argus.Orchestrator/Config/InputValidator.cs
    - orchestrator/Argus.Orchestrator.Tests/InputValidatorTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
    - orchestrator/Argus.Orchestrator/Program.cs
decisions:
  - "Validate raw parsedDetectors (before empty→HST defaulting) per plan prohibition: InputValidator.Validate MUST NOT be invoked on the defaulted list"
  - "Cross-field high/low check only fires when both keys present AND both in their individual valid ranges — avoids double-reporting"
  - "Unknown detector skips param validation via continue — one error per unknown detector, not N param errors"
  - "UiValidationBlocked (7008) is a distinct LogEvent from UiSaveFailed (7003) to allow targeted log filtering"
  - "hasHst computed from entities list using OrdinalIgnoreCase — wired inline at the success-banner return"
metrics:
  duration: "~12 minutes"
  completed: "2026-07-01"
  tasks: 2
  files: 4
---

# Phase 04 Plan 01: Server-Side Input Validation Summary

Server-side input validation gate enforcing every 04-UI-SPEC rule via `InputValidator` static class, wired into POST /api/sensors/save before any write; HST warm-up disclosure now renders correctly via real hasHst flag at the success-banner call site.

---

## Tasks Completed

| Task | Name | Commit | Key Outputs |
|------|------|--------|-------------|
| 1 (RED) | InputValidatorTests failing | a066d67 | 38 test cases covering all 04-UI-SPEC validation rules |
| 1 (GREEN) | InputValidator + LogEvent | 7299410 | InputValidator.cs (static class, full rules), LogEvents.UiValidationBlocked=7008 |
| 2 | Wire InputValidator into save handler | ef1f4b4 | Validation gate in Program.cs, hasHst wiring at BuildSuccessBanner |

---

## Deviations from Plan

### Cross-field high/low threshold implementation detail

**Rule: auto-adapt (spec interpretation)**
- **Found during:** Task 1 implementation
- **Issue:** The spec says high_threshold must be > low_threshold, but adding a cross-field error when one of the values is already out of range would produce duplicate errors for the same logical violation.
- **Fix:** Cross-field check only fires when both values are within their individual valid ranges. If high_threshold=1.5 (out of range), only the range error fires; the cross-field check is skipped for that pair.
- **Files modified:** `orchestrator/Argus.Orchestrator/Config/InputValidator.cs`
- **Outcome:** All 38 tests pass including the cross-field and boundary cases.

---

## Known Stubs

None. All validation rules are fully implemented. The `BuildSuccessBanner` hasHst value is computed from the actual entity list, not hardcoded.

---

## Threat Flags

No new threat surface introduced. The plan's STRIDE threats T-04-01 through T-04-05 are all mitigated:

| Threat | Mitigation | Status |
|--------|-----------|--------|
| T-04-01 — entity_id Tampering | EntityIdRegex `^[a-z0-9_]+\.[a-z0-9_]+$` | DONE |
| T-04-02 — detector name Tampering | KnownDetectors allowlist {hst,mad,stl} | DONE |
| T-04-03 — param value Tampering | Per-type numeric range checks | DONE |
| T-04-04 — XSS in error messages | WebUtility.HtmlEncode on entity_id + detector name | DONE |
| T-04-05 — partial/corrupt write | Validation gate before ConfigWriter.WriteAsync | DONE |

---

## Verification

- `dotnet build Argus.Orchestrator/Argus.Orchestrator.csproj -c Debug` — PASS (0 warnings, 0 errors)
- `dotnet test --filter "FullyQualifiedName~InputValidatorTests"` — PASS (38/38)
- `dotnet test --filter "FullyQualifiedName~SaveEndpoint"` — PASS (22/22)
- `grep -n "InputValidator.Validate" Program.cs` — line 320, before snapshotById (line 331) and before writer.WriteAsync
- `grep -n "LogEvents.UiValidationBlocked" Program.cs` — line 323, inside validation-failure branch
- `grep -n "BuildValidationBanner" Program.cs` — line 326, inside validation-failure branch (no WriteAsync in same branch)
- `grep -n "BuildSuccessBanner" Program.cs` — line 411, passes real hasHst second argument
- `grep "WebUtility.HtmlEncode" InputValidator.cs` — 2 matches (entity_id path + detector name path)
- `grep "UiValidationBlocked = new(7008" LogEvents.cs` — present in 7xxx block

## Self-Check: PASSED

- `orchestrator/Argus.Orchestrator/Config/InputValidator.cs` — FOUND
- `orchestrator/Argus.Orchestrator.Tests/InputValidatorTests.cs` — FOUND
- `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` (UiValidationBlocked) — FOUND
- `orchestrator/Argus.Orchestrator/Program.cs` (validation gate + hasHst) — FOUND
- Commit a066d67 (RED tests) — FOUND
- Commit 7299410 (GREEN implementation) — FOUND
- Commit ef1f4b4 (Task 2 wiring) — FOUND
