---
phase: 02-live-sensor-discovery-entity-selection-ui
reviewed: 2026-07-01T00:00:00Z
depth: standard
files_reviewed: 14
files_reviewed_list:
  - argus/rootfs/etc/cont-init.d/10-config-gen.sh
  - orchestrator/Argus.Orchestrator.Tests/EntityPickerPageTests.cs
  - orchestrator/Argus.Orchestrator.Tests/GlobExpanderTests.cs
  - orchestrator/Argus.Orchestrator.Tests/HaSensorRegistryTests.cs
  - orchestrator/Argus.Orchestrator.Tests/SaveEndpointPatternsTests.cs
  - orchestrator/Argus.Orchestrator/Config/GlobExpander.cs
  - orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs
  - orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs
  - orchestrator/Argus.Orchestrator/Ha/IHaSensorRegistry.cs
  - orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs
  - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs
  - orchestrator/Argus.Orchestrator/wwwroot/css/argus.css
findings:
  critical: 0
  warning: 4
  info: 4
  total: 8
status: issues_found
---

# Phase 02: Code Review Report

**Reviewed:** 2026-07-01T00:00:00Z
**Depth:** standard
**Files Reviewed:** 14
**Status:** issues_found

## Summary

Phase 2 adds a live sensor registry, an entity-picker UI, and a `POST /api/sensors/save` endpoint
to an .NET 8 ASP.NET Core add-on for Home Assistant. The implementation is well-structured and
security-conscious: user strings are consistently HTML-encoded before interpolation, YAML is
serialized via YamlDotNet rather than string-formatted, the lock file is written after a successful
config write, and the interim auth helper correctly uses `RemoteIpAddress` (not a spoofable header).
The volatile-reference swap in `HaSensorRegistry` is a sound single-writer/many-reader pattern.

Four warnings require attention before this phase is considered production-ready:

1. The `IsAuthorizedRequest` guard accepts any request carrying `X-Ingress-Path` without verifying
   the header value, so any caller that can fabricate or forward that header bypasses auth entirely.
2. The lock-file write in the save handler is a plain `File.WriteAllTextAsync` — if the process
   crashes between the config write and the lock write, the file is absent and gen-entities.py will
   overwrite the UI config on the next container restart.
3. `GlobExpander.Resolve` adds a `manuallyChecked` entity id verbatim to the result set even when
   it is not present in the live snapshot; this allows a caller to inject an arbitrary entity id
   into `entities.yaml`.
4. `HaWebSocketClient.ReceiveMessageAsync` buffers WebSocket frames into a `MemoryStream` with no
   size cap; a malformed or adversarial HA message can grow the stream without bound until OOM.

Four info items cover stale/incorrect doc comments and redundant explicit `using` statements that
duplicate `ImplicitUsings`.

---

## Warnings

### WR-01: Interim auth — X-Ingress-Path check is trivially bypassable from the LAN

**File:** `orchestrator/Argus.Orchestrator/Program.cs:203-206`

**Issue:** `IsAuthorizedRequest` returns `true` for any request that includes an `X-Ingress-Path`
header, regardless of its value or the caller's IP address. Any process on the same LAN (or inside
the add-on network) can fabricate the header and pass the check. The comment acknowledges this is
interim, but the bypass surface is wider than the Supervisor-loopback/172.30.32.2 fallback would
allow on its own — the header check effectively undoes the IP restriction. The risk is bounded to
the add-on container network today but should be noted explicitly for the Phase 4 validate_session
replacement.

**Fix:** Document the bypass explicitly so the Phase 4 task is not forgotten; optionally tighten
now by requiring both the header AND the IP condition:

```csharp
// Option A — document clearly (minimal change):
// NOTE: X-Ingress-Path alone is not a secret; any LAN peer can set it.
// Full validate_session cookie auth is scheduled for Phase 4.
static bool IsAuthorizedRequest(HttpContext ctx)
{
    var remote = ctx.Connection.RemoteIpAddress;
    if (remote is null) return false;
    if (System.Net.IPAddress.IsLoopback(remote)) return true;
    if (remote.Equals(System.Net.IPAddress.Parse("172.30.32.2"))) return true;
    // Accept X-Ingress-Path only from already-allowed IPs (belt-and-suspenders):
    // return ctx.Request.Headers.ContainsKey("X-Ingress-Path");
    return false; // stricter: IP-only until Phase 4
}
```

The existing behaviour is acceptable for a home-network add-on provided the comment and Phase 4
ticket are in place; flag for Phase 4 tracking.

---

### WR-02: Lock file write is not atomic — crash window between config write and lock creation

**File:** `orchestrator/Argus.Orchestrator/Program.cs:321-323`

**Issue:** The save handler writes entities.yaml via `ConfigWriter.WriteAsync` (which does an
atomic temp-then-rename) and then writes `.ui_config_present` via `File.WriteAllTextAsync`.
If the process is killed, restarted by the Supervisor, or the Kestrel task is cancelled in the
window between the two writes, entities.yaml exists with the new UI content but `.ui_config_present`
is absent. On the next container start `10-config-gen.sh` will run `gen-entities.py` and
overwrite the UI-authored config, silently discarding the user's selection.

```csharp
// Current (lines 319–323):
await writer.WriteAsync(entitiesPath, fullYaml, ct);

var lockPath = Path.Combine(Path.GetDirectoryName(entitiesPath)!, ".ui_config_present");
await File.WriteAllTextAsync(lockPath, string.Empty, ct);
```

**Fix:** Write the lock file via `ConfigWriter` or at minimum use a synchronous `File.WriteAllText`
so a partial async write is less likely. Better: write the lock file in `ConfigWriter.WriteAsync`
as a second atomic step, or verify its existence inside the same try block and log a warning on
failure. A belt-and-suspenders approach is to check in `10-config-gen.sh` for entities.yaml
modification time rather than a separate sentinel, but the simplest safe fix in C# is:

```csharp
await writer.WriteAsync(entitiesPath, fullYaml, ct);
// Write lock synchronously — if WriteAsync succeeded the lock must also be durable.
File.WriteAllText(lockPath, string.Empty);
```

---

### WR-03: GlobExpander.Resolve accepts manually-checked IDs that are not in the live snapshot

**File:** `orchestrator/Argus.Orchestrator/Config/GlobExpander.cs:77-82`

**Issue:** Step 5 of `Resolve` unconditionally adds any non-empty id from `manuallyChecked` to
`patternSelected`, even if the id is not present in the live snapshot (`allIds`). The save handler
in `Program.cs` passes the raw form field values as `manuallyChecked`; an attacker (or accidental
form replay) can submit arbitrary strings as entity ids and have them written into `entities.yaml`.
Because `FriendlyName` falls back to `""` for unknown ids (line 287 Program.cs), the YAML entry
will have an empty friendly_name and a detector block — which may silently be picked up by the
pipeline.

```csharp
// Step 5 (lines 77-82):
foreach (var id in manuallyChecked)
{
    if (!string.IsNullOrWhiteSpace(id))
        patternSelected.Add(id);  // no snapshot membership check
}
```

**Fix:** Constrain manually-checked additions to ids that exist in the live snapshot:

```csharp
foreach (var id in manuallyChecked)
{
    if (!string.IsNullOrWhiteSpace(id) && allIds.Contains(id))
        patternSelected.Add(id);
}
```

The same constraint should be applied symmetrically in step 6 for `manuallyUnchecked` (a remove on
a non-existent id is a no-op today, so there is no functional bug there, but constraining it
prevents future confusion).

---

### WR-04: ReceiveMessageAsync has no frame-size cap — unbounded MemoryStream growth

**File:** `orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs:167-183`

**Issue:** `ReceiveMessageAsync` accumulates WebSocket frames into a `MemoryStream` until
`EndOfMessage` is true. There is no maximum size guard. A malformed HA message (e.g. a
`get_states` response from an HA instance with thousands of entities and large attribute blobs)
or an adversarial message injected on the LAN will grow the stream without bound. On a constrained
add-on container this can cause OOM and process termination.

```csharp
// Lines 168-183 — no size guard:
using var ms = new MemoryStream();
var buffer = new byte[8192];
WebSocketReceiveResult result;
do
{
    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
    ...
    ms.Write(buffer, 0, result.Count);
}
while (!result.EndOfMessage);
```

**Fix:** Add a maximum message size guard and throw if exceeded:

```csharp
private const int MaxMessageBytes = 4 * 1024 * 1024; // 4 MB — HA get_states is well under this

private async Task<JsonDocument> ReceiveMessageAsync(CancellationToken ct)
{
    using var ms = new MemoryStream();
    var buffer = new byte[8192];
    WebSocketReceiveResult result;
    do
    {
        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close)
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely,
                $"HA closed the WebSocket ({result.CloseStatus}: {result.CloseStatusDescription})");
        if (ms.Length + result.Count > MaxMessageBytes)
            throw new InvalidOperationException(
                $"HA WebSocket message exceeded {MaxMessageBytes} bytes — dropping connection.");
        ms.Write(buffer, 0, result.Count);
    }
    while (!result.EndOfMessage);

    ms.Position = 0;
    return await JsonDocument.ParseAsync(ms, cancellationToken: ct).ConfigureAwait(false);
}
```

---

## Info

### IN-01: IHaSensorRegistry doc comment incorrectly states HaStateDto is internal

**File:** `orchestrator/Argus.Orchestrator/Ha/IHaSensorRegistry.cs:8-9`

**Issue:** The XML doc comment on `IHaSensorRegistry` says "Internal because HaStateDto (WebSocket
DTO) is internal". `HaStateDto` was made `public` in this phase (confirmed in `HaWebSocketClient.cs`
line 11: `public sealed record HaStateDto`). The comment is now stale and misleading.

**Fix:**
```csharp
/// <summary>
/// Thread-safe read cache for the live numeric-sensor snapshot from Home Assistant.
/// Written exclusively by NetDaemonHaEventSource on every HA connect (get_states snapshot).
/// Read by Kestrel HTTP threads (Wave 2 entity-picker endpoints).
/// </summary>
public interface IHaSensorRegistry
```

---

### IN-02: EntitiesConfig.cs has redundant explicit using directives (CS8019 / ImplicitUsings)

**File:** `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs:1-2`

**Issue:** The project has `<ImplicitUsings>enable</ImplicitUsings>` in the csproj (confirmed).
`System.Collections.Generic` and `System.Globalization` are both covered by the .NET Web SDK
implicit usings. The two explicit `using` directives are redundant and will generate CS8019
warnings.

**Fix:** Remove both redundant usings:
```csharp
// Remove these two lines:
// using System.Collections.Generic;
// using System.Globalization;

namespace Argus.Orchestrator.Config;
```

---

### IN-03: 10-config-gen.sh — ARGUS_ENTITIES_PATH is exported unconditionally (correct), but the comment could be clearer about what is skipped

**File:** `argus/rootfs/etc/cont-init.d/10-config-gen.sh:111-116`

**Issue:** The guard at line 112 correctly skips only `gen-entities.py` when `.ui_config_present`
exists; `ARGUS_ENTITIES_PATH` is still written at line 111 regardless of the guard branch. This is
the correct behaviour. However, the `bashio::log.info` message at line 113 ("entities.yaml
preserved") does not mention that `ARGUS_ENTITIES_PATH` was still written, which could mislead a
developer debugging why the orchestrator still picks up the path. A clearer message would prevent
future confusion.

**Fix:**
```bash
bashio::log.info "UI config present — skipping gen-entities.py; \
ARGUS_ENTITIES_PATH=/data/entities.yaml already set."
```

---

### IN-04: SaveEndpointPatternsTests — LockFile_NotCreatedBeforeSave is a vacuous test

**File:** `orchestrator/Argus.Orchestrator.Tests/SaveEndpointPatternsTests.cs:233-247`

**Issue:** The test `LockFile_NotCreatedBeforeSave` creates a temp directory, asserts the lock file
does not exist (trivially true — it was never created), then deletes the directory. It does not
exercise any production code path and adds no regression value. It also ends with
`await Task.CompletedTask` to satisfy the `async Task` signature, which is a code smell.

**Fix:** Either remove the test entirely, or replace it with a meaningful negative test — e.g.,
assert that `ConfigWriter.WriteAsync` throwing an `IOException` does NOT result in the lock file
being created (which would actually test the ordering guarantee stated in the method comments).

---

_Reviewed: 2026-07-01T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
