---
phase: 04-multi-arch-ci-integration-documentation
fixed_at: 2026-06-30T00:00:00Z
review_path: .planning/phases/04-multi-arch-ci-integration-documentation/04-REVIEW.md
iteration: 1
findings_in_scope: 3
fixed: 3
skipped: 0
status: all_fixed
---

# Phase 4: Code Review Fix Report

**Fixed at:** 2026-06-30
**Source review:** .planning/phases/04-multi-arch-ci-integration-documentation/04-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 3 (all Warning; 0 Critical)
- Fixed: 3
- Skipped: 0

Info findings (IN-01, IN-02, IN-03) were out of scope (`fix_scope: critical_warning`) and were not addressed.

## Fixed Issues

### WR-01: Script-injection sink — `github.ref_name` interpolated into `run:` step

**Files modified:** `.github/workflows/build.yml`
**Commit:** f84783b
**Applied fix:** Moved `github.repository_owner` and `github.ref_name` into an `env:`
block on the "Export image ref" step (`OWNER`, `REF`) and changed the `run:` body to
reference them as quoted shell variables (`${OWNER}`, `${REF}`). The untrusted git tag
name is now passed as an environment value rather than expanded inside the shell parser,
closing the canonical Actions script-injection vector. The `tags:` / `build-args:` inputs
to `build-push-action` (lower-risk action inputs, not `run:` sinks) were left as-is per the
review's own note that the `run:` step is the real sink.

### WR-02: `BUILD_ARCH` never passed — `io.hass.arch` label is always empty

**Files modified:** `argus/Dockerfile`
**Commit:** 18b9075
**Applied fix:** Chose the Dockerfile-side fix recommended by the review over a CI
`build-args` change. In a multi-platform buildx build (`linux/amd64,linux/arm64`) a single
static CI build-arg cannot be per-arch-correct, so passing a static `BUILD_ARCH` would
stamp the wrong arch on at least one manifest entry. Instead declared buildx's automatic
`ARG TARGETARCH` and defaulted `ARG BUILD_ARCH=${TARGETARCH}`. Buildx populates `TARGETARCH`
(`amd64` / `arm64`) per platform during the build, so `io.hass.arch="${BUILD_ARCH}"` is now
correctly populated per manifest entry with no CI changes. Added an explanatory comment
documenting why the static-CI-arg approach was rejected.

### WR-03: `BUILD_DATE` is set to the run ID, not a date

**Files modified:** `.github/workflows/build.yml`
**Commit:** b6f53e2
**Applied fix:** Replaced `BUILD_DATE=${{ github.run_id }}` with a real RFC3339 UTC
timestamp. Added a "Compute build timestamp" step before the build step that runs
`date -u +%Y-%m-%dT%H:%M:%SZ` and writes it to `$GITHUB_OUTPUT`, then referenced
`${{ steps.builddate.outputs.date }}` in `build-args`. Preferred this over the review's
alternate suggestion of `github.event.repository.updated_at` because that context is null
on `workflow_dispatch` runs, whereas the computed timestamp works for both the tag-push and
`workflow_dispatch` triggers.

## Verification

- WR-01 / WR-03: workflow re-parsed with `yaml.safe_load` after each edit (YAML OK).
  Confirmed the `build-and-push` step order, `build-args` contents, and all three
  `image-facts` gates plus the `IMG` output wiring (`needs.build-and-push.outputs.image_ref`)
  remained intact.
- WR-02: Dockerfile has no Tier 2 syntax checker; verified via Tier 1 re-read that the
  `LABEL` block and surrounding `ARG` declarations are intact and the new `TARGETARCH` /
  `BUILD_ARCH` defaulting is well-formed.

`argus/DOCS.md` had no findings and was not touched.

---

_Fixed: 2026-06-30_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
