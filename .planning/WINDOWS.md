---
schema_version: 1
open_count: 1
waived_count: 0
fixed_count: 0
total_count: 1
last_updated: 2026-08-03T08:19:39.830Z
---

# Broken Windows Ledger

> Cross-phase defect register. `/gsd-ship` blocks while `open_count > 0`.
> Waive with `gsd-tools windows waive <id> "<reason>"` (reason required).
> Mark fixed with `gsd-tools windows fixed <id>`.

| id | phase | kind | file | line | description | status | reason | recorded_at | resolved_at |
|----|-------|------|------|------|-------------|--------|--------|-------------|-------------|
| 1 | 15 | skipped-test | detector/tests/test_restart_resilience.py | 146 | SIGTERM subprocess integration test (TestSigtermFlush) skipped on win32 — Windows has no catchable SIGTERM delivery from another process (TerminateProcess bypasses Python signal handlers); production target is the Linux s6-overlay add-on container where this holds | open |  | 2026-08-03T08:19:39.830Z |  |

````json
[
  {
    "id": 1,
    "kind": "skipped-test",
    "phase": "15",
    "file": "detector/tests/test_restart_resilience.py",
    "line": 146,
    "description": "SIGTERM subprocess integration test (TestSigtermFlush) skipped on win32 — Windows has no catchable SIGTERM delivery from another process (TerminateProcess bypasses Python signal handlers); production target is the Linux s6-overlay add-on container where this holds",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-08-03T08:19:39.830Z",
    "resolved_at": null
  }
]
````
