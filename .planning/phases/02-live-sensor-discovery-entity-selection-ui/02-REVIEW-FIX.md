---
phase: 02-live-sensor-discovery-entity-selection-ui
fixed_at: 2026-07-01T08:30:00Z
review_path: .planning/phases/02-live-sensor-discovery-entity-selection-ui/02-REVIEW.md
iteration: 1
findings_in_scope: 8
fixed: 8
skipped: 0
status: all_fixed
---

# Phase 02: Code Review Fix Report

**Fixed at:** 2026-07-01T08:30:00Z
**Source review:** .planning/phases/02-live-sensor-discovery-entity-selection-ui/02-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 8 (4 warnings + 4 info)
- Fixed: 8
- Skipped: 0

## Fixed Issues

### WR-01: Interim auth — X-Ingress-Path check is trivially bypassable from the LAN

**Files modified:** `orchestrator/Argus.Orchestrator/Program.cs`
**Commit:** 1b33f0c
**Applied fix:** Removed the `if (ctx.Request.Headers.ContainsKey("X-Ingress-Path")) return true;` branch. `IsAuthorizedRequest` now checks only RemoteIpAddress (loopback or 172.30.32.2). Added explicit comment that full `validate_session` is deferred to Phase 4, and that X-Ingress-Path is not treated as an auth credential. The header is still consumed above (in the PathBase middleware) for `base-href` routing — that path is unchanged.

---

### WR-02: Lock file write is not atomic — crash window between config write and lock creation

**Files modified:** `orchestrator/Argus.Orchestrator/Program.cs`, `orchestrator/Argus.Orchestrator.Tests/SaveEndpointPatternsTests.cs`
**Commits:** 1b33f0c (Program.cs), 0f4f6e0 (test update)
**Applied fix:** Changed `await File.WriteAllTextAsync(lockPath, string.Empty, ct)` to `File.WriteAllText(lockPath, string.Empty)` (synchronous). This ensures the lock is durable before the handler returns — no async continuation window between config write and lock creation. The test `LockFile_NotCreatedBeforeSave` (vacuous — never exercised production code) was replaced with `LockFile_NotCreatedWhenWriteAsyncFails`, which verifies that when `ConfigWriter.WriteAsync` throws, the lock file is absent. The success-path test was also updated to use the synchronous `File.WriteAllText` to mirror production code.

---

### WR-03: GlobExpander.Resolve accepts manually-checked IDs that are not in the live snapshot

**Files modified:** `orchestrator/Argus.Orchestrator/Config/GlobExpander.cs`, `orchestrator/Argus.Orchestrator.Tests/GlobExpanderTests.cs`
**Commit:** 08941c1
**Applied fix:** Step 5 (manuallyChecked) now gates additions on `allIds.Contains(id)` — arbitrary form-submitted strings not present in the live snapshot are silently dropped. Step 6 (manuallyUnchecked) is constrained symmetrically; removing an absent id was already a no-op but the constraint prevents future confusion. Added test `Resolve_ManuallyChecked_ArbitraryIdNotInSnapshot_IsRejected` to cover the new security boundary.

---

### WR-04: ReceiveMessageAsync has no frame-size cap — unbounded MemoryStream growth

**Files modified:** `orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs`
**Commit:** 03ed722
**Applied fix:** Added `private const int MaxMessageBytes = 4 * 1024 * 1024;` and a guard inside the receive loop: `if (ms.Length + result.Count > MaxMessageBytes) throw new InvalidOperationException(...)`. The check fires before `ms.Write` so the stream never actually exceeds the cap. The exception propagates to the caller (NetDaemonHaEventSource) which will drop the connection and reconnect.

---

### IN-01: IHaSensorRegistry doc comment incorrectly states HaStateDto is internal

**Files modified:** `orchestrator/Argus.Orchestrator/Ha/IHaSensorRegistry.cs`
**Commit:** 0fe9573
**Applied fix:** Removed the stale sentence "Internal because HaStateDto (WebSocket DTO) is internal; all consumers are in this assembly." from the XML doc comment on `IHaSensorRegistry`. The class is `public` as is `HaStateDto`.

---

### IN-02: EntitiesConfig.cs has redundant explicit using directives (CS8019 / ImplicitUsings)

**Files modified:** `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs`
**Commit:** 8a69b2f
**Applied fix:** Removed `using System.Collections.Generic;` (covered by `Microsoft.NET.Sdk.Web` ImplicitUsings). `using System.Globalization;` was NOT removed — it is not in the implicit set (verified by inspecting `obj/Debug/net8.0/Argus.Orchestrator.GlobalUsings.g.cs`) and is required by `NumberStyles` and `CultureInfo` in `HstParams`. The review's claim that both were redundant was partially incorrect; only one was safe to remove.

---

### IN-03: 10-config-gen.sh — log message does not mention ARGUS_ENTITIES_PATH is still set

**Files modified:** `argus/rootfs/etc/cont-init.d/10-config-gen.sh`
**Commit:** 0399982
**Applied fix:** Updated `bashio::log.info` message from "UI config present — skipping gen-entities.py (entities.yaml preserved)." to "UI config present — skipping gen-entities.py; ARGUS_ENTITIES_PATH=/data/entities.yaml already set." to make clear that ARGUS_ENTITIES_PATH was written unconditionally at line 111 regardless of the guard branch.

---

### IN-04: SaveEndpointPatternsTests — LockFile_NotCreatedBeforeSave is a vacuous test

**Files modified:** `orchestrator/Argus.Orchestrator.Tests/SaveEndpointPatternsTests.cs`
**Commit:** 0f4f6e0
**Applied fix:** Replaced with `LockFile_NotCreatedWhenWriteAsyncFails` (see WR-02 above). This was handled as part of the WR-02 fix commit.

---

## Skipped Issues

None — all findings were fixed.

---

_Fixed: 2026-07-01T08:30:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
