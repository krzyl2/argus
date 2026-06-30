---
phase: 04-multi-arch-ci-integration-documentation
reviewed: 2026-06-30T00:00:00Z
depth: standard
files_reviewed: 2
files_reviewed_list:
  - .github/workflows/build.yml
  - argus/DOCS.md
findings:
  critical: 0
  warning: 3
  info: 3
  total: 6
status: issues_found
---

# Phase 4: Code Review Report

**Reviewed:** 2026-06-30
**Depth:** standard
**Files Reviewed:** 2
**Status:** issues_found

## Summary

Reviewed the multi-arch GitHub Actions workflow (`build.yml`) and the add-on install
documentation (`DOCS.md`) for Phase 4. Cross-referenced against the actual add-on
(`argus/config.yaml`, `argus/Dockerfile`, `detector/requirements.txt`).

Overall the workflow is well-structured: `permissions:` are correctly scoped to least
privilege, GHCR auth uses `GITHUB_TOKEN` without leaking it, publish→buildx ordering is
correct, and the three image-facts gates are logically sound — each fails closed on
violation. `DOCS.md` is factually accurate against the config schema with no real
inaccuracies found.

The notable issues are: (1) a GitHub Actions script-injection sink where `github.ref_name`
is interpolated directly into a `run:` step, (2) the Dockerfile `BUILD_ARCH` arg is never
supplied by CI so the `io.hass.arch` label is always empty, and (3) `BUILD_DATE` is fed the
run ID instead of a date. The remaining items are supply-chain hardening and minor notes.

## Warnings

### WR-01: Script-injection sink — `github.ref_name` interpolated into `run:` step

**File:** `.github/workflows/build.yml:65`
**Issue:** The "Export image ref" step expands `${{ github.ref_name }}` directly into the
shell command body:
```yaml
run: echo "ref=ghcr.io/${{ github.repository_owner }}/argus:${{ github.ref_name }}" >> "$GITHUB_OUTPUT"
```
`github.ref_name` is the git tag name (the workflow triggers on `push: tags: v*`). Git tag
names are attacker-influenceable and may contain shell metacharacters (backticks, `$(...)`,
`;`). A tag such as ``v1`curl evil`` would execute injected code in the runner with access
to `GITHUB_TOKEN` (`packages: write`). This is the canonical Actions script-injection
pattern: untrusted context expanded inside `run:`.
**Fix:** Pass the value through `env:` and reference it as a quoted shell variable so the
expansion happens outside the shell parser:
```yaml
- name: Export image ref
  id: imgref
  env:
    OWNER: ${{ github.repository_owner }}
    REF: ${{ github.ref_name }}
  run: echo "ref=ghcr.io/${OWNER}/argus:${REF}" >> "$GITHUB_OUTPUT"
```
The same hardening applies to the `tags:` block (lines 55-56) and `build-args` (line 58),
though `build-push-action` inputs are lower risk than `run:`; the `run:` step is the real
sink.

### WR-02: `BUILD_ARCH` never passed — `io.hass.arch` label is always empty

**File:** `.github/workflows/build.yml:57-60`
**Issue:** The Dockerfile declares `ARG BUILD_ARCH` and consumes it in
`LABEL io.hass.arch="${BUILD_ARCH}"` (`argus/Dockerfile:57,64`). The CI `build-args` block
supplies `BUILD_VERSION`, `BUILD_DATE`, and `BUILD_REF` but **not** `BUILD_ARCH`, so every
published image carries an empty `io.hass.arch` label on both amd64 and arm64. HA tooling
reads this label.
**Fix:** Provide the per-platform target arch using buildx's `TARGETARCH` automatic arg:
```yaml
build-args: |
  BUILD_ARCH=${{ '' }}   # see note
```
Buildx exposes `TARGETARCH` automatically inside the Dockerfile, so the cleanest fix is to
default `BUILD_ARCH` from it in the Dockerfile rather than via CI:
```dockerfile
ARG TARGETARCH
ARG BUILD_ARCH=${TARGETARCH}
```
This yields `amd64`/`arm64` per manifest entry without CI changes.

### WR-03: `BUILD_DATE` is set to the run ID, not a date

**File:** `.github/workflows/build.yml:59`
**Issue:** `BUILD_DATE=${{ github.run_id }}` feeds the numeric workflow run ID into a build
arg named `BUILD_DATE`. The value is semantically wrong (a run ID is not a date). The
Dockerfile currently does not emit `BUILD_DATE` in a LABEL, so there is no functional break
today, but anything consuming this arg later (or a future `org.opencontainers.image.created`
label) would receive misleading data.
**Fix:** Use an actual timestamp:
```yaml
BUILD_DATE=${{ github.event.repository.updated_at }}
```
or generate one in a prior step (`date -u +%Y-%m-%dT%H:%M:%SZ`) and pass via `env`. If a
build identifier is genuinely wanted here, rename the arg to `BUILD_RUN_ID`.

## Info

### IN-01: Actions pinned to mutable major tags rather than commit SHAs

**File:** `.github/workflows/build.yml:22,25,35,38,41,48,76,79`
**Issue:** All third-party actions are pinned to mutable major-version tags
(`actions/checkout@v4`, `docker/build-push-action@v6`, `docker/setup-qemu-action@v3`, etc.).
A compromised or retagged action could inject code into a workflow that holds
`packages: write` and `GITHUB_TOKEN`. Supply-chain best practice is to pin to a full commit
SHA with the version in a trailing comment.
**Fix:** e.g. `uses: actions/checkout@<40-char-sha> # v4.2.2`. Dependabot can keep SHA pins
updated. This is a hardening recommendation, not a defect.

### IN-02: Size gate may run the loop body once on an empty digest list

**File:** `.github/workflows/build.yml:114-127`
**Issue:** `while IFS=' ' read -r arch digest; ... done <<< "$digests"` executes the body
once with empty `arch`/`digest` if `$digests` is empty (e.g. if the arch `select` filter
ever matched nothing). That iteration would call
`docker buildx imagetools inspect --raw "${IMG%:*}@"` and fail — which fails the step. The
behavior is fail-closed (acceptable), but the failure message would be confusing rather than
a clear "no matching arch" error.
**Fix:** Guard before the loop:
```bash
if [ -z "$digests" ]; then echo "FAIL: no amd64/arm64 manifests found"; exit 1; fi
```

### IN-03: Framework-dependent publish is reused across architectures (informational)

**File:** `.github/workflows/build.yml:29-32,47-61`
**Issue:** `dotnet publish --self-contained false` runs once on the amd64 runner, and the
resulting output is COPYed into both the amd64 and arm64 images (`argus/Dockerfile:42`).
This is correct because framework-dependent output is architecture-neutral IL and each
image installs the matching `dotnet-runtime-8.0`. Noting it explicitly because it is a
load-bearing assumption: if the publish ever switches to `--self-contained true` or adds a
`RuntimeIdentifier`, the arm64 image would silently ship amd64-native binaries. No change
needed now.

---

_Reviewed: 2026-06-30_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
