---
status: testing
phase: 01-ingress-scaffold-sdk-migration-config-seam
source: [01-VERIFICATION.md]
started: 2026-06-30
updated: 2026-06-30
---

## Current Test

number: 1
name: "Open Web UI" panel + placeholder page loads through Ingress
expected: |
  In HA add-on page, an "Open Web UI" entry appears. Clicking it serves the Argus
  placeholder page through the Supervisor Ingress proxy — no separate login, no 502/404.
awaiting: user response

## Tests

### 1. "Open Web UI" panel + placeholder page
expected: Panel appears on the add-on page; clicking serves the placeholder via Ingress (no login, no 502/404).
result: [pending]

### 2. v2.0 BackgroundService regression
expected: After restarting the add-on, all v2.0 workers (streaming, MQTT, health, batch) appear in the log and `binary_sensor.argus_addon_health` reads healthy.
result: [pending]

### 3. Static assets HTTP 200 + X-Ingress-Path behavior
expected: Browser DevTools Network shows `css/argus.css` and `js/htmx.min.js` each return HTTP 200 via the Ingress URL (not direct port). Record whether the Supervisor strips the ingress prefix (closes the STACK-vs-PITFALLS X-Ingress-Path open question).
result: [pending]

### 4. Kestrel bind address
expected: `ss -tlnp | grep 8099` inside the container shows `0.0.0.0:8099` (not loopback), with no stray `:5000` listener.
result: [pending]

## Summary

total: 4
passed: 0
issues: 0
pending: 4
skipped: 0
blocked: 0

## Gaps

(none recorded — all items pending live-HA test after add-on rebuild + deploy)

## Note

These items require the rebuilt v3.0 add-on image to be deployed to the live Home Assistant OS
instance. They cannot be executed in the build environment. Recommended: run all phases' live-HA
checks together after the milestone is built and a new add-on image is published. Drive with
`/gsd-verify-work 1`.
