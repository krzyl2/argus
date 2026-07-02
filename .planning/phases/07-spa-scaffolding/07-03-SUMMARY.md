---
phase: 07-spa-scaffolding
plan: 03
subsystem: build-infra
tags: [docker, multi-stage-build, ci, github-actions, powershell]

# Dependency graph
requires:
  - phase: 07-spa-scaffolding
    plan: 01
    provides: "orchestrator/ui/ Vite project with package-lock.json for the Node build stage to consume"
  - phase: 07-spa-scaffolding
    plan: 02
    provides: "JSON /api/* endpoints + MapFallbackToFile SPA hosting for the built image to serve"
provides:
  - "Multi-stage argus/Dockerfile: node:20-alpine ui-build -> dotnet/sdk:8.0 dotnet-build -> base-debian:bookworm runtime, no Node/SDK in runtime"
  - "Single publish path (Docker-internal); host-side dotnet publish removed from both CI and local script"
  - "SPA asset assertion (index.html + assets/*.js) replacing the retired htmx.min.js assertion in both build.yml and build-push.ps1"
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Global ARG BUILD_FROM declared before the first FROM (not stage-locally) — required so the runtime stage's FROM ${BUILD_FROM} resolves correctly in a multi-stage file"
    - "Vite outDir already targets orchestrator/Argus.Orchestrator/wwwroot directly (per 07-01's vite.config.ts) — Docker COPY --from=ui-build sources from that path, not an intermediate dist/ folder"

key-files:
  created: []
  modified:
    - argus/Dockerfile
    - .github/workflows/build.yml
    - deploy/build-push.ps1

key-decisions:
  - "COPY --from=ui-build source path corrected to /src/Argus.Orchestrator/wwwroot/ (Vite's actual configured outDir) instead of the plan's assumed /src/ui/dist/ — verified by running the Docker build and observing the real output location"
  - "ARG BUILD_FROM moved to a single global declaration before Stage 1's FROM — a stage-scoped ARG placed between two FROM lines is not visible to the next FROM in Docker's build model, which broke the runtime stage entirely until fixed"
  - "-SkipPublish parameter kept in build-push.ps1 as a documented no-op for CLI back-compat rather than removed, since the host publish block it used to gate no longer exists"
  - "dotnet test step in build.yml reordered ahead of the new Node/SPA verification steps (still logically independent of publish/Docker, per RESEARCH) rather than left in its old position sandwiched between two now-removed steps"

requirements-completed: [UI-01]

# Metrics
duration: ~20min
completed: 2026-07-02
status: complete
---

# Phase 07 Plan 03: Dockerfile Multi-Stage Build + CI/Local Script Wiring Summary

**Rebuilt `argus/Dockerfile` into three stages (Node SPA build -> dotnet publish -> Node/SDK-free runtime), deleted the host-side `dotnet publish` step from both CI and the local PowerShell script, and rewrote their htmx-era asset assertions to check for the SPA's actual output — verified end-to-end with a real `docker build` (dotnet-build stage confirms `wwwroot/index.html` + `assets/*.js`; full runtime image confirms zero Node/npm/SDK).**

## Performance

- **Duration:** ~20 min
- **Tasks:** 2
- **Files modified:** 3 (no files created; htmx.min.js was already removed in Plan 07-01, nothing left to delete here)

## Accomplishments

- `argus/Dockerfile` is now three stages: `node:20-alpine AS ui-build` (npm ci + npm run build) -> `mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet-build` (copies built wwwroot in, then `dotnet publish`) -> the existing `base-debian:bookworm` runtime stage, now sourcing its orchestrator bits via `COPY --from=dotnet-build` instead of `COPY orchestrator/publish/`
- Verified via real Docker builds (not just static review): `docker build --target dotnet-build` produces `/app/publish/wwwroot/index.html` + `/app/publish/wwwroot/assets/*.js`; a full build of the runtime stage confirms `dotnet --list-sdks` is empty, `command -v node`/`command -v npm` are empty, and `/opt/argus/orchestrator/wwwroot/` contains the built SPA
- `.github/workflows/build.yml`: removed the "Set up .NET 8" + "Publish orchestrator" host-publish pairing's publish step (kept .NET setup since `dotnet test` still needs it), added a Node 20 setup + `npm ci && npm run build` step purely for CI-side verification that the SPA builds cleanly, and rewrote the asset assertion to check `wwwroot/index.html` + `wwwroot/assets/*.js` instead of `htmx.min.js`/`argus.css`
- `deploy/build-push.ps1`: removed the entire host `dotnet publish` block (including its own htmx/argus.css assertion) and the now-dead `$publishDir`/`$csproj` variables; `-SkipPublish` kept as a documented no-op switch for CLI back-compat
- `dotnet test orchestrator/Argus.Orchestrator.sln -c Release` — 292/292 passing, confirming zero regression from the infra-only changes in this plan

## Task Commits

1. **Task 1: Multi-stage Dockerfile (node build SPA -> dotnet publish -> runtime bez Node/SDK)** - `5a68cab` (feat)
2. **Task 2: Usun host dotnet publish z CI + skryptu lokalnego, przepisz asercje assetow z htmx na SPA** - `ec7b752` (feat)

## Files Created/Modified

- `argus/Dockerfile` - three-stage rebuild (`ui-build` -> `dotnet-build` -> runtime); `ARG BUILD_FROM` promoted to a global declaration; final `COPY --from=dotnet-build /app/publish/ /opt/argus/orchestrator/` replaces the old host-publish `COPY`
- `.github/workflows/build.yml` - "Publish orchestrator" step removed; `dotnet test` step retained (moved earlier in sequence); new Node 20 setup + `npm ci && npm run build` (CI-side-only verification) + SPA asset assertion (`index.html` + `assets/*.js`)
- `deploy/build-push.ps1` - host `dotnet publish` block and its htmx/argus.css assertion removed entirely; `$publishDir`/`$csproj` variables removed; `-SkipPublish` kept as documented no-op; usage comment updated

## Decisions Made

- Fixed the plan's assumed `COPY --from=ui-build /src/ui/dist/ ...` source path to the real one, `/src/Argus.Orchestrator/wwwroot/` — Plan 07-01 already configured `vite.config.ts`'s `outDir` to resolve directly to `../Argus.Orchestrator/wwwroot` (relative to `orchestrator/ui/`), so there is no intermediate `dist/` directory to copy from. Discovered by actually running `docker build` and reading the real Vite build log/output paths, not by re-deriving from the plan text.
- Moved `ARG BUILD_FROM=...` to a single global declaration before the first `FROM` in the file. Docker's build model does not carry a stage-scoped `ARG` declared between two `FROM` lines forward to the next `FROM` — the plan's snippet (declaring `ARG BUILD_FROM` immediately before Stage 3's `FROM ${BUILD_FROM}`, same position as the original single-stage file) produced `ERROR: failed to build: failed to solve: base name (${BUILD_FROM}) should not be blank` when actually built. Confirmed via `docker build` reproduction before and after the fix.
- Reordered `build.yml`'s `dotnet test` step to run immediately after `.NET 8` setup rather than after the new Node/SPA build steps — it is source-level and independent of the SPA build per RESEARCH, and keeping it early avoids paying for a Node.js SPA build before finding out `dotnet test` failed.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] `ARG BUILD_FROM` not visible to the runtime stage's `FROM` (Docker build model constraint)**
- **Found during:** Task 1, first `docker build --target dotnet-build` attempt
- **Issue:** Following the plan's Dockerfile snippet literally (declaring `ARG BUILD_FROM=ghcr.io/home-assistant/base-debian:bookworm` immediately before the runtime stage's `FROM ${BUILD_FROM}`, mirroring the original single-stage file's layout) fails once the file has multiple stages — Docker only carries `ARG` declarations before the *first* `FROM` forward into every subsequent `FROM`'s scope; an `ARG` declared between two `FROM` lines is stage-local to whatever comes after it, but is NOT resolved for use as the base image of the very next `FROM` itself. Docker's own build output confirms this: `WARN: UndefinedArgInFrom` + `ERROR: base name (${BUILD_FROM}) should not be blank`.
- **Fix:** Moved `ARG BUILD_FROM=ghcr.io/home-assistant/base-debian:bookworm` to a single global declaration at the very top of the file (before Stage 1's `FROM node:20-alpine`), removed the duplicate declaration that used to sit before Stage 3.
- **Files modified:** `argus/Dockerfile`
- **Commit:** `5a68cab`

**2. [Rule 1 - Bug] `COPY --from=ui-build` source path assumed a `dist/` subfolder that Vite does not produce here**
- **Found during:** Task 1, second `docker build --target dotnet-build` attempt (after fixing the ARG issue)
- **Issue:** The plan and RESEARCH.md both write `COPY --from=ui-build /src/ui/dist/ ./orchestrator/Argus.Orchestrator/wwwroot/`, assuming Vite's default `dist/` output directory. However, Plan 07-01 already set `vite.config.ts`'s `build.outDir` to `resolve(__dirname, '../Argus.Orchestrator/wwwroot')` — Vite writes its build output directly to that absolute path (`/src/Argus.Orchestrator/wwwroot` inside the `ui-build` stage, since `WORKDIR` is `/src/ui`), never populating `/src/ui/dist/` at all. The build failed with `"/src/ui/dist": not found`.
- **Fix:** Changed the `COPY --from=ui-build` source to `/src/Argus.Orchestrator/wwwroot/`, matching the actual configured Vite output location.
- **Files modified:** `argus/Dockerfile`
- **Commit:** `5a68cab`

**3. [Rule 1 - Bug] Stale Dockerfile comment claiming host-side publish**
- **Found during:** Task 1, reviewing the runtime stage's `.NET Runtime` comment block after the multi-stage rewrite
- **Issue:** A pre-existing comment above the `dotnet-install.sh` block read "Orchestrator is published by CI before docker build" — now false, since this same plan moves publish into the Dockerfile itself. Leaving it would mislead future readers about where publish happens.
- **Fix:** Updated the comment to state the orchestrator is published inside the image by the `dotnet-build` stage.
- **Files modified:** `argus/Dockerfile`
- **Commit:** `5a68cab`

## Issues Encountered

None beyond the three auto-fixed issues documented above — all were caught and resolved by actually running `docker build` rather than trusting the plan's Dockerfile snippet verbatim, per this plan's own verification requirement.

## User Setup Required

None. `docker`, `docker buildx`, and the `node:20-alpine`/`mcr.microsoft.com/dotnet/sdk:8.0` images were all available locally and used directly to verify the build end-to-end (dotnet-build stage target build + full runtime build), so no deploy-time-only verification item is being deferred here.

## Verification Evidence

- `docker build -f argus/Dockerfile --target dotnet-build` succeeded; `docker run --rm --entrypoint sh ... -c "test -f /app/publish/wwwroot/index.html && ls /app/publish/wwwroot/assets/*.js"` printed `SPA_IN_PUBLISH_OK`
- Full `docker build -f argus/Dockerfile --build-arg BUILD_VERSION=0.0.0-test .` (no `--target`, i.e. runtime stage) succeeded
- In the built runtime image: `command -v node` and `command -v npm` both returned empty; `dotnet --list-sdks` returned empty (only `dotnet --list-runtimes` shows `Microsoft.AspNetCore.App 8.0.28` + `Microsoft.NETCore.App 8.0.28`); `/opt/argus/orchestrator/wwwroot/` contains `index.html`, `assets/index-D8yzcv9n.js`, `css/argus.css`
- `grep` gates from the plan's Task 2 verification all pass: no `htmx.min.js` in `build.yml` or `build-push.ps1`, no `dotnet publish` in `build.yml`
- `dotnet test orchestrator/Argus.Orchestrator.sln -c Release` — 292/292 passing (no regression from infra-only changes)
- Test images (`argus-dotnetbuild-check`, `argus-full-check`) were removed after verification (`docker rmi`) — not left behind as build cache pollution

## Next Phase Readiness

- UI-01 is now fully satisfied: SPA is built at Docker image build-time via the `ui-build` stage, ships as static assets, and the runtime image has zero Node/npm/SDK footprint
- Phase 07 (SPA Scaffolding) is functionally complete across all three plans: 07-01 (SPA scaffold), 07-02 (JSON API + SPA fallback), 07-03 (Docker/CI wiring, this plan)
- Phase 8 (Group Config UI + Algorithm Chooser) can build directly on this SPA foundation — no further build-pipeline work expected to be needed before then

---
*Phase: 07-spa-scaffolding*
*Completed: 2026-07-02*

## Human Verification Required

**UI-02 (Ingress base-path live verification)** — not satisfiable in this plan or by unit/Docker-level testing. Per `07-RESEARCH.md` and this plan's `<human_verification>` block: after the next add-on release ships this image, the operator must open the add-on through HA's "Open Web UI" (never a direct port) and confirm: the SPA loads under the real dynamic Ingress prefix, sensor search works, detector assignment + save works, and the hot-reload cycle completes without an add-on restart. The SPA is built to be resilient to this by construction (`base: './'` + hash routing + relative fetch, from Plan 07-01), but the live Ingress proxy behavior itself has not been exercised against a running Home Assistant Supervisor in this session.

## Self-Check: PASSED

Verified `argus/Dockerfile`, `.github/workflows/build.yml`, `deploy/build-push.ps1` all present on disk with expected content. Verified commit hashes `5a68cab` and `ec7b752` both present in `git log --oneline`. Verified via live `docker build` (not just static grep) that the multi-stage image builds successfully end-to-end and satisfies every must-have in the plan's frontmatter.
