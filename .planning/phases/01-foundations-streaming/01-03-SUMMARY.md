---
phase: 01-foundations-streaming
plan: "03"
subsystem: deploy/mTLS
tags: [mtls, certs, openssl, grpc, security]
dependency_graph:
  requires: [01-01, 01-02]
  provides: [mTLS trust material for gRPC channel]
  affects: [01-04 .NET gRPC channel, 01-05 detector mTLS config]
tech_stack:
  added: [openssl (self-signed CA + mTLS certs)]
  patterns: [self-signed CA, server SAN with IP+DNS, client cert for edge host]
key_files:
  created:
    - deploy/generate-certs.sh
    - deploy/certs/server-ext.cnf
    - deploy/CERTS.md
  modified: []
decisions:
  - "GPU_HOST_IP=192.168.1.100 and GPU_HOST_NAME=gpu-host used as PLACEHOLDER values per operator direction — real certs MUST be regenerated before deployment"
  - "Script uses //CN= prefix (double forward slash) to defeat Windows Git Bash MSYS path conversion on -subj arguments (cross-platform: Linux normalises // to /)"
  - "SAN extension written to _san_work.cnf temp file during signing; committed server-ext.cnf stays as placeholder template (T-03-02 mitigated)"
metrics:
  duration_minutes: 5
  completed_date: "2026-06-10"
  tasks_completed: 3
  files_created: 3
  files_modified: 0
requirements_addressed: [INFRA-06]
---

# Phase 1 Plan 3: mTLS Certificate Generation Summary

**One-liner:** Self-signed ArgusCA with 4096-bit RSA server cert (SAN: IP:192.168.1.100, DNS:gpu-host) and client cert, generated via parameterized bash script with Windows Git Bash compatibility fix.

## Tasks Completed

| Task | Status | Commit | Notes |
|------|--------|--------|-------|
| Task 1: Obtain GPU host values | Resolved by operator | — | Placeholder values provided: GPU_HOST_IP=192.168.1.100, GPU_HOST_NAME=gpu-host |
| Task 2: Write generate-certs.sh, server-ext.cnf, CERTS.md | Done | 3ad3078 | All acceptance criteria met |
| Task 3: Run script and verify SAN | Done | — (certs gitignored) | Chain verified; SAN confirmed; no git commit needed for ignored files |

## Artifacts

- `deploy/generate-certs.sh` — Parameterized cert generation; `set -euo pipefail`; fails loud on empty vars; SAN with IP+DNS; 730-day expiry; Windows Git Bash compatible
- `deploy/certs/server-ext.cnf` — Template with `__GPU_HOST_IP__`/`__GPU_HOST_NAME__` placeholders; committed to git (no real host detail)
- `deploy/CERTS.md` — Operator doc covering generation, SAN inspection, chain verification, distribution, 2-year rotation reminder, and placeholder cert warning
- `deploy/certs/{ca,server,client}.{crt,key}` — Generated on disk; gitignored by `deploy/certs/*.key` and `deploy/certs/*.crt` rules

## Verification Results

```
deploy/certs/server.crt: OK     (openssl verify -CAfile ca.crt server.crt)
deploy/certs/client.crt: OK     (openssl verify -CAfile ca.crt client.crt)

X509v3 Subject Alternative Name:
    IP Address:192.168.1.100, DNS:gpu-host
```

## IMPORTANT: Placeholder Certs — Must Regenerate Before Deployment

The certs generated in this plan use PLACEHOLDER values (`GPU_HOST_IP=192.168.1.100`, `GPU_HOST_NAME=gpu-host`). They are development-only certs to unblock Phase 1 implementation.

**Before deploying to the real environment:**
1. Determine the actual GPU host static LAN IP (set DHCP reservation if needed)
2. Determine the actual GPU host hostname
3. Run: `GPU_HOST_IP=<real-ip> GPU_HOST_NAME=<real-hostname> bash deploy/generate-certs.sh`
4. Distribute regenerated certs to both hosts (see `deploy/CERTS.md`)
5. Validate with `openssl verify` and a live Health RPC call

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Windows Git Bash MSYS path conversion breaks openssl -subj**
- **Found during:** Task 3 (first run of generate-certs.sh)
- **Issue:** On Windows Git Bash, `/CN=ArgusCA` in `-subj` argument gets converted to `C:/Program Files/Git/CN=ArgusCA` by the MSYS runtime before openssl sees it, causing: `req: subject name is expected to be in the format /type0=value0...`
- **Fix:** Use `//CN=` prefix (double forward slash) via `SUBJ_CA="//CN=ArgusCA"` variables. Git Bash treats `//` as "do not convert this path"; Linux/macOS shell normalises `//` to `/` at the kernel level. Standard workaround for Windows CI runners.
- **Files modified:** `deploy/generate-certs.sh`
- **Commit:** 0809923

**2. [Rule 2 - Security] Generated server-ext.cnf with real host values would be committed**
- **Found during:** Task 3 (post-run git status)
- **Issue:** The script originally overwrote `deploy/certs/server-ext.cnf` (a tracked file) with real IP/hostname values during signing, creating a T-03-02 threat (host details in git)
- **Fix:** Script now writes SAN extension to `_san_work.cnf` (temp, deleted after signing); committed `server-ext.cnf` template is never modified by the script
- **Files modified:** `deploy/generate-certs.sh`, `deploy/certs/server-ext.cnf`
- **Commit:** 0809923

## Known Stubs

None. The deliverables are infrastructure/cert artifacts, not UI or data-flow components.

## Threat Flags

No new threat surface introduced beyond the plan's threat model. T-03-01 (SAN mismatch) mitigated — SAN verified. T-03-02 (keys in git) mitigated — all `.key`/`.crt` gitignored; real-values `server-ext.cnf` never committed. T-03-04 (weak params) mitigated — 4096-bit RSA, 730-day expiry.

## Self-Check: PASSED

- [x] `deploy/generate-certs.sh` exists
- [x] `deploy/certs/server-ext.cnf` exists (placeholder template, no real IP)
- [x] `deploy/CERTS.md` exists
- [x] `deploy/certs/ca.crt` exists on disk (gitignored)
- [x] `deploy/certs/server.crt` exists on disk (gitignored)
- [x] `deploy/certs/client.crt` exists on disk (gitignored)
- [x] Commits 3ad3078 and 0809923 exist in git log
- [x] `openssl verify` reports OK for both server.crt and client.crt
- [x] SAN confirmed: IP Address:192.168.1.100, DNS:gpu-host
