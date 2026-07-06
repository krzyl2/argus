# SECURITY — Phase 08: group-config-ui-algorithm-chooser

**Audit date:** 2026-07-06
**Auditor:** gsd-security-auditor (retroactive threat-mitigation verification)
**Result:** SECURED — 11/11 threats closed
**Register source:** authored at plan time (`register_authored_at_plan_time: true`)
**Scope note:** Each declared mitigation was verified against implemented code (grep-confirmed at the cited file:line). Documentation/intent was not accepted as evidence.

---

## Threat Verification

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-08-01 | Tampering | mitigate | CLOSED | `_cast_float` try/except→default in `detector/argus_detector/group/peer_divergence.py:37-45` and `_cast_float`/`_cast_int` in `multivariate_detector.py:45-64`; both catch `(ValueError, TypeError)` and return the default, never raising. `from_params` (peer_divergence.py:113-122) and `_DETECTOR_FACTORY` (multivariate_detector.py:67-82) route all param reads through these casts. |
| T-08-02 | Denial of Service | accept | CLOSED | Accepted risk — see Accepted Risks Log below. Rationale (self-hosted single operator, D9) still holds; additionally strengthened (not required) by server-side param Min/Max enforcement in `GroupInputValidator.cs:145-171`. |
| T-08-03 | Elevation of Privilege | mitigate | CLOSED | `IsAuthorizedRequest` is the literal first line of all 4 new handlers: `GET /api/groups` (`Program.cs:460`), `POST /api/groups/save` (`Program.cs:482`), `GET /api/detectors/catalog` (`Program.cs:583`), `GET /api/groups/{id}/status` (`Program.cs:592`). Guard implementation: loopback + Supervisor-IP 172.30.32.2 only, `Program.cs:229-243`. |
| T-08-04 | Denial of Service | mitigate | CLOSED | `GroupInputValidator.MaxMembers = 100` cap enforced in `GroupInputValidator.cs:19,86-90`, invoked at `Program.cs:503` BEFORE the disk write at `Program.cs:560`. `ReadFromJsonAsync` wrapped in try/catch returning 400 on `JsonException` (`Program.cs:489-495`) plus null-body 400 guard (`Program.cs:497-501`). |
| T-08-05 | Information Disclosure | mitigate | CLOSED | `GET /api/groups/{id}/status` returns `200` with `{ status: null }` for unknown id (`Program.cs:594-595`) — no 404 existence oracle. |
| T-08-06 | Tampering | mitigate | CLOSED | Defensive parse of every field beyond id: `HaWebSocketClient.cs:124` (`TryGetProperty("area_id")` + null guard), `:159` (`TryGetProperty` with null fallback). `BuildEntityAreaNamesAsync` degrades to empty map on any exception (`NetDaemonHaEventSource.cs:237-241`). Unresolved area → `null` (`HaSensorRegistry.cs:46-47`) with domain derived independently (`:57`). |
| T-08-07 | Tampering | mitigate | CLOSED | Advanced-form param strings forwarded via `dict(request.params)` in `servicer.py:304,437,442` reach `_cast_float`/`_cast_int` default-fallback (see T-08-01). Registry threads params into both detector branches: `registry.py:305,309`. Client-side `paramSchema` bounds are additionally enforced server-side (`GroupInputValidator.cs:145-171`). |
| T-08-08 | Spoofing/EoP | mitigate | CLOSED | `apiGet`/`apiPost` throw on any leading-slash path (`client.ts:6-9,15-18`); `fetch()` exists only inside the wrapper. All production call sites pass relative paths: `api/groups`, `api/groups/save` (`groups.ts:73,136,160`), `api/detectors/catalog` (`groupEditor.ts:28`), `api/groups/{id}/status` (`AttributionPanel.tsx:29`), `api/sensors`/`api/sensors/save` (`sensors.ts:71,171`). No leading-slash or raw-fetch bypass found in any non-test file. Server-side `IsAuthorizedRequest` remains the authority (T-08-03). |
| T-08-09 | Tampering | accept | CLOSED | Accepted risk — see Accepted Risks Log below. Backend re-validation confirmed present: `GroupInputValidator.Validate` called at `Program.cs:503`, rejecting before any write. Client validation (`validation/groupParams.ts`) is fast-feedback only. |
| T-08-10 | Information Disclosure | mitigate | CLOSED | Same 200-null contract as T-08-05 (`Program.cs:594-595`); catalog endpoint (`Program.cs:581-586`) and status cache expose no secrets — catalog is static descriptive C#, status returns only verdict/contribution data. |
| T-08-11 | Tampering | mitigate | CLOSED | Server `_cast_float` default-fallback is authority (peer_divergence.py:37-45, multivariate_detector.py:45-64); client `paramSchema` bounds are decoration also enforced server-side in `GroupInputValidator.cs:145-171` (documented WR-02). |

---

## Accepted Risks Log

### T-08-02 — Extreme `n_estimators` inflating iforest fit cost (Denial of Service)
- **Disposition:** accept
- **Rationale:** Self-hosted, single-operator deployment (PROJECT constraint D9 — no cloud, no multi-tenancy). The Advanced form is the operator's own input against their own hardware; there is no adversarial multi-user surface. No server-side hard cap on fit cost this phase.
- **Residual note:** `GroupInputValidator` now enforces catalog `paramSchema` Min/Max bounds server-side (`GroupInputValidator.cs:145-171`). If the `iforest` `n_estimators` schema field carries a `Max`, extreme values are already rejected — this exceeds the accepted position but does not change the disposition. No dedicated fit-cost/time budget cap exists.
- **Status:** accepted risk stands; rationale confirmed still valid.

### T-08-09 — Client-only validation bypass (Tampering)
- **Disposition:** accept
- **Rationale:** The backend re-validates every save at `POST /api/groups/save` via `GroupInputValidator.Validate` (`Program.cs:503`), which runs before any YAML write. Client-side validation (`orchestrator/ui/src/validation/groupParams.ts`) is fast-feedback UX only and is never the authority. A crafted request bypassing the client is caught server-side.
- **Status:** accepted risk stands; server-side authority confirmed present.

---

## Unregistered Flags

None. No `## Threat Flags` section is present in any of `08-01-SUMMARY.md` through `08-04-SUMMARY.md`. All new attack surface introduced during implementation (4 new `/api/*` endpoints, HA area/entity registry WS calls, Advanced-form param passthrough, SPA API client) maps to registered threats in the phase register (T-08-03/04/05/10 for endpoints, T-08-06 for HA registry parse, T-08-01/07/11 for param passthrough, T-08-08 for the SPA client).

---

## Auditor Notes

- Implementation files were treated as READ-ONLY; no code was modified.
- Two auto-fixed executor deviations (08-03: `GetFiltered` friendly_name match; `/api/sensors` areaName/domain serialization) were reviewed for security impact: both are additive read-path enrichments behind the same `IsAuthorizedRequest` guard — no new attack surface, no threat mapping required.
- The peer-divergence server floor in `GroupInputValidator` is `MinMembers = 2` (`GroupInputValidator.cs:21`), while the Python detector enforces `_MIN_MEMBERS = 3` (`peer_divergence.py:28,139`). This is a functional (no-verdict) concern, not a declared threat — flagged here for awareness only, out of audit scope.
