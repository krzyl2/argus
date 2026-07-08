---
phase: 11
slug: new-standalone-screens-dashboard-algorithms-settings
status: verified
threats_open: 0
asvs_level: 1
created: 2026-07-08
---

# Phase 11 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| Ingress client → orchestrator HTTP | Any request reaching `/api/settings` over the Supervisor Ingress prefix; untrusted until `IsAuthorizedRequest` confirms loopback or Supervisor IP | Read-only config projection (non-sensitive) |
| ConnectionSettings (in-process secrets) → JSON response | Secret material (HA token, MQTT user/password, Influx token, TLS cert/key) must not cross into the serialized response | Must NOT cross — allowlist boundary |

*Plans 11-02/11-03/11-04/11-05 are frontend-only (D-01): no new HTTP surface, no secrets. Theme state persists only a non-sensitive `'light'|'dark'` value in `localStorage` (`argus-theme`, Phase 10 mechanism, unchanged). They consume existing authorized read endpoints (`/api/sensors`, `/api/groups`, `/api/detectors/catalog`, `/api/settings`) via `apiGet`.*

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-11-01 | Information Disclosure | `GET /api/settings` response body | mitigate | `SettingsProjection.Build` (Web/SettingsProjection.cs) constructs the response field-by-field from an allowlist of 6 non-sensitive fields (`detectorEndpoint`, `influxUrl`, `influxBucket`, `batchIntervalMinutes`, `nightlyFitHour`, `logLevel`); never serializes `ConnectionSettings` as a whole. No `HaToken`/`MqttPassword`/`MqttUser`/`InfluxToken`/`TlsCa`/`TlsCert`/`TlsKey` referenced in the method. | closed |
| T-11-02 | Elevation of Privilege / unauthorized read | `GET /api/settings` handler | mitigate | `Program.cs:593` — `if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);` runs before `SettingsProjection.Build`. Identical loopback/Supervisor-IP guard (`IsAuthorizedRequest`, Program.cs:229) used by every other `/api/*` route. | closed |
| T-11-03 | Information Disclosure | `logLevel` source | accept | `logLevel` reads `IConfiguration["Logging:LogLevel:Default"]` — a non-sensitive ASP.NET log-verbosity level (`Debug`/`Information`/`Warning`), not a secret; `null` when unset. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| R-11-01 | T-11-03 | `logLevel` exposes only the ASP.NET log-verbosity level, which is not secret material and carries no credential/topology value. Surfacing it read-only on the Settings screen is intentional (SET-01). | Krzysztof Krawczyk | 2026-07-08 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-07-08 | 3 | 3 | 0 | Claude (gsd-secure-phase, plan-time register short-circuit) |

Register authored at plan time (11-01-PLAN.md `<threat_model>`); `threats_open: 0` with all plan-time threats verified CLOSED against implementation. Auditor spawn short-circuited per workflow Step 3 rule.

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-07-08
