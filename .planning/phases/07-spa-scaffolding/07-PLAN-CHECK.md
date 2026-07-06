# Phase 7 Plan Check — SPA Scaffolding

**Verified:** 2026-07-02
**Plans checked:** 07-01, 07-02, 07-03
**Status:** ISSUES FOUND (2 warnings, 0 blockers)

## Dimension Results

### 1. Requirement Coverage — PASS
| Requirement | Plans | Status |
|---|---|---|
| UI-01 | 07-03 | Covered |
| UI-02 | 07-01 | Covered |
| UI-03 | 07-02 | Covered |
| UI-04 | 07-01, 07-02 | Covered |

All 4 phase requirements appear in plan requirements frontmatter. No PROJECT.md/REQUIREMENTS.md requirement relevant to this phase is dropped.

### 2. Task Completeness — PASS
All 6 tasks (2 per plan) have files/action/verify/done. Actions are specific rather than vague. automated verify present on every task.

### 3. Dependency Correctness — PASS
- 07-01: wave 1, depends_on [] — correct.
- 07-02: wave 1, depends_on [] — correct.
- 07-03: wave 2, depends_on [07-01, 07-02] — correct.
- files_modified for 07-01 and 07-02 are fully disjoint (SPA project vs .NET project) — confirmed, safe to run in parallel Wave 1.
- No cycles, no forward references.

### 4. Key Links Planned — PASS
- state/sensors.ts to api/client.ts via apiGet/apiPost — wired.
- SaveResultBanner.tsx to api/types.ts via ok/kind discriminant — wired.
- Program.cs to InputValidator.Validate, ConfigWriter.WriteAsync/liveCfg.Swap, SaveRequest.cs via ReadFromJsonAsync — wired, validate-before-write ordering preserved.
- 07-03 Dockerfile stage-to-stage COPY --from links present and correctly ordered.

### 5. Scope Sanity — WARNING
- 07-01: 2 tasks (within target) but 30 files_modified — exceeds the 15+ file blocker threshold on paper. Greenfield Vite scaffold, most files are small mechanically-scaffolded stubs, so risk is lower than raw count suggests. Flagged for awareness.
- 07-02, 07-03: 2 tasks each, 11 and 4 files respectively — within/near target.
- Recommendation: proceed as-is; split Task 2 (13 components) if execution quality degrades mid-plan.

### 6. Verification Derivation — PASS
must_haves.truths across all three plans are user-observable, not implementation-focused. Artifacts map to truths. Key_links cover critical wiring.

### 7. Context Compliance — PASS
All CONTEXT.md locked decisions map to implementing tasks. No Deferred Ideas appear in any task (explicit prohibitions in frontmatter). Claude's Discretion items exercised reasonably and documented.

### 7b. Scope Reduction Detection — PASS
No v1/v2, static-for-now, future-enhancement, stub, or not-wired-to language reducing any UI-01..04 requirement. UI-02 live-Ingress check correctly captured as human_verification (acknowledged testability limit), not silently dropped.

### 7c. Architectural Tier Compliance — PASS
Cross-referenced against RESEARCH.md Architectural Responsibility Map. Auth enforcement stays server-side (IsAuthorizedRequest, unchanged, called first in every handler). Client routing/state stays in browser tier. No security-sensitive capability misplaced.

### 8. Nyquist Compliance — PASS
All 6 tasks have automated verify commands (npm build+file checks, npm test, dotnet build, dotnet test, docker build+file check, grep assertions). No watch-mode flags, no full E2E suites. Sampling continuity 2/2 per plan. No Wave-0/MISSING placeholders requiring backfill.

### 9. Cross-Plan Data Contracts — PASS (interface handshake verified)
07-02's authoritative C# SaveRequest (Entities:[{EntityId, Detectors:[{Name, Params}]}], Include, Exclude; camelCase JSON entities/entityId/detectors/name/params/include/exclude) is IDENTICAL in shape and field naming to 07-01's SPA SaveRequest TypeScript type (entities:[{entityId, detectors:[{name, params}]}], include, exclude). No mismatch.

### 10. CLAUDE.md Compliance — PASS
No conflicts with project CLAUDE.md constraints. Multi-stage Dockerfile keeps Node out of the runtime image per UI-01.

### 11. Research Resolution — PASS
RESEARCH.md Open Questions (publish-location, save-body-shape) both carry explicit Recommendation statements, adopted verbatim by the plans (dotnet publish moved into Dockerfile; natural nested JSON body). No dangling ambiguity reaches execution.

### 12. Pattern Compliance — PASS
07-PATTERNS.md exact code blocks (Program.cs JSON conversion, Dockerfile multi-stage, CI/build-push edits) referenced and followed near-verbatim in 07-02 and 07-03. SPA-side files correctly point to RESEARCH.md/UI-SPEC.md as reference (no in-repo analog exists).

## Issues Found

issue 1:
  dimension: verification_derivation
  severity: warning
  description: UI-SPEC AppShell footer spec (footer shows version string, fetch from /api/version or embed at build time) has no executing action in either 07-01 or 07-02. AppShell.tsx is listed as a file to create but Task 2 action prose never specifies footer/version content; 07-02 does not add a /api/version endpoint or build-time version literal.
  plan: 07-01
  task: 2
  fix_hint: Add a sentence to 07-01 Task 2 action specifying how AppShell renders the version (build-time literal via Vite define, or fetch to an existing endpoint) — low-risk cosmetic gap, does not block the 4 core success criteria.

issue 2:
  dimension: scope_sanity
  severity: warning
  description: Plan 07-01 has 30 files_modified, exceeding the 15+ file blocker threshold, though task count (2) is within target and most files are small scaffolded config/component stubs.
  plan: 07-01
  metrics: {tasks: 2, files: 30}
  fix_hint: No pre-execution split required given well-specified must_haves per component; split Task 2 into list/search vs detector/save sub-tasks if execution proves too large.

## Recommendation

2 warnings, 0 blockers. Plans are sound: all 4 requirements covered, dependency graph valid and Wave 1 files fully disjoint, the critical SaveRequest JSON interface handshake between 07-01 (TS) and 07-02 (C#) matches exactly, Nyquist automated-verify coverage is complete, and no scope reduction or scope creep detected against CONTEXT.md decisions. Execution may proceed; address the AppShell footer gap opportunistically during 07-01 execution.

VERDICT: PASS (with non-blocking warnings)
