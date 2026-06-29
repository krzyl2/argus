# Pitfalls Research

**Domain:** Home Assistant add-on packaging — multi-process .NET 8 + Python ML gRPC app (Argus v2)
**Researched:** 2026-06-29
**Confidence:** HIGH

---

## Critical Pitfalls

### Pitfall 1: HA Base Images Are Alpine/musl — .NET Binaries Will Not Run

**What goes wrong:**
The current Dockerfiles (`Dockerfile.orchestrator`) use `mcr.microsoft.com/dotnet/runtime:8.0-jammy-chiseled` (Ubuntu, glibc). HA add-on base images (`ghcr.io/home-assistant/aarch64-base`, `ghcr.io/home-assistant/amd64-base`) are Alpine Linux — musl libc. A .NET binary published for `linux-arm64` (glibc) dropped into an Alpine container exits immediately with "Exec format error" or "not found" even though the ELF architecture matches. This is the single highest-risk item in v2.

**Why it happens:**
.NET self-contained binaries link against the C runtime at publish time. `linux-arm64` targets glibc; Alpine provides musl. The two ABIs are binary-incompatible. The `runtime:8.0-jammy-chiseled` final stage works in the v1 two-host compose setup but fails inside an HA base image.

**How to avoid:**
Two valid paths — pick one and commit to it in the scaffolding phase:

- **Path A (recommended): Use `ghcr.io/home-assistant/aarch64-base-debian` / `amd64-base-debian` as the add-on base image.** These are HA-maintained Debian (Bookworm) images that include s6-overlay and bashio. .NET publishes to `linux-arm64` and `linux-x64` (glibc) without any RID change. The detector's existing glibc Python wheels work without recompilation. This is the least-resistance path for this specific app.

- **Path B: Keep Alpine base, publish .NET self-contained with `linux-musl-arm64` / `linux-musl-x64` RID.** Requires `dotnet publish -r linux-musl-arm64 --self-contained true` and the final stage must be Alpine-based. The .NET SDK on an x86_64 build host cross-compiles correctly; musl binaries are available since .NET 6. However, all Python ML wheels must then be either musllinux wheels or compiled from source (see Pitfall 2).

Never mix: do not publish for glibc and run on Alpine, and do not publish for musl and run on Debian.

**Warning signs:**
- Container exits immediately with exit code 1 or 127 at startup, no logs from the .NET process.
- `docker run --rm <image> ldd /app/Argus.Orchestrator.dll` (or the native AOT binary) shows "not found" for libc dependencies.
- `file /app/Argus.Orchestrator` shows "dynamically linked" against a libc that does not exist in the image.

**Phase to address:**
Add-on scaffolding phase (first phase of v2). Base image selection drives every downstream decision. Lock Path A or Path B before writing a single s6 service file.

---

### Pitfall 2: Python ML Wheels Have No musl/musllinux Builds — Source Compilation on aarch64 QEMU Fails or Takes Hours

**What goes wrong:**
If Path B (Alpine) is chosen: River, PyOD, statsmodels, and scipy do not publish `musllinux_1_2_aarch64` wheels to PyPI as of mid-2026. `pip install` falls back to sdist compilation. Under Docker BuildKit QEMU emulation (x86_64 CI building for aarch64), compiling scipy from source takes 60–120 minutes and frequently times out. statsmodels requires a Fortran compiler (`gfortran`) that is not present in the Alpine base image by default. The build either fails or produces an image so large (multi-GB build layer) it cannot be pushed.

**Why it happens:**
PEP 656 standardized musllinux wheel tags but adoption by scientific Python projects is incomplete, especially for aarch64. manylinux wheels (glibc) are explicitly rejected by pip on musl systems. The triple combination — musl + aarch64 + QEMU — is the worst case.

**How to avoid:**
Use Path A (Debian base) for the detector. The existing `python:3.12-slim-bookworm` base from v1 already works and has all manylinux aarch64 wheels available. The detector Dockerfile needs no changes except replacing `ENTRYPOINT` with s6 service files.

If Alpine is required for another reason, install build dependencies in a multi-stage build (`apk add --no-cache gcc musl-dev g++ gfortran openblas-dev`) and build wheels during CI on native hardware, not QEMU. Cache the wheel layer aggressively.

**Warning signs:**
- `pip install` log shows "Building wheel for scipy (pyproject.toml)" during image build (not "Using cached").
- CI build step exceeds 30 minutes on the detector image.
- `apk info | grep gfortran` returns nothing and statsmodels install errors with "No module named 'numpy.distutils'".

**Phase to address:**
Add-on scaffolding phase — base image lock decision. CI/multi-arch build phase — verify wheel resolution before wiring the full build matrix.

---

### Pitfall 3: Conditional mTLS Loopback Trap — Channel Created with SSL When `detector_endpoint` Is Absent

**What goes wrong:**
v2 design: local detector = loopback, no mTLS; remote detector_endpoint = mTLS. The trap: orchestrator startup code reads `options.json`, and if `detector_endpoint` is absent (local mode), it must create an **insecure** gRPC channel to `http://localhost:50051`. If the same code path attempts to load cert files first (e.g., to validate they exist), or if a shared channel factory defaults to `GrpcChannel.ForAddress("https://...")`, the TLS handshake is attempted against a plaintext server. The Python server is not configured for TLS in local mode → immediate `UNAVAILABLE: SSL handshake failed`.

**Why it happens:**
In v1, mTLS was always on (D4). All channel-creation code uses `SslCredentials`. When v2 adds the "no mTLS on loopback" branch, developers add an `if (isLocal) skip_cert_load()` check but forget that `GrpcChannel.ForAddress` defaults to HTTPS if the URI scheme is `https://` or if `ChannelCredentials.SecureSsl` is used anywhere in the shared code path.

**How to avoid:**
The branch must be at the URI scheme level, not just at the cert-load level:

```csharp
// Local mode: explicitly HTTP (insecure)
var channel = GrpcChannel.ForAddress("http://localhost:50051");

// Remote mode: HTTPS with mTLS credentials
var credentials = new SslCredentials(caCert, new KeyCertificatePair(clientCert, clientKey));
var channel = GrpcChannel.ForAddress($"https://{detectorEndpoint}", new GrpcChannelOptions
{
    Credentials = credentials
});
```

Never use `"https://localhost:50051"` in local mode even if certs are present. Test both branches in integration tests: one with no cert files on disk, one with a cert-carrying remote endpoint.

**Warning signs:**
- Orchestrator logs `StatusCode.UNAVAILABLE` with "SSL handshake failed" or "received bytes" on channel connect in local mode.
- Container fails to start when `detector_endpoint` option is empty/absent.
- Works in remote mode, fails in local mode (different code path exercised).

**Phase to address:**
Conditional mTLS phase (the phase implementing `detector_endpoint` option and loopback default). Must be verified with a negative test: local mode with no cert files present must succeed.

---

### Pitfall 4: s6-overlay v3 Process Supervision Misconfigurations Crash the Container Silently

**What goes wrong:**
s6-overlay v3 (used in all current HA base images) has breaking changes from v2. The most common issues in multi-process add-ons:

1. **PID 1 enforcement**: v3 checks that it is PID 1. If the Dockerfile uses `CMD` with a shell (`sh -c`) wrapper and the add-on `config.yaml` does not set `init: false`, Docker adds its own init process. s6 exits with `"s6-overlay-suexec: fatal: can only run as pid 1"` and the container dies silently.

2. **Crashing longrun takes down the container**: A `longrun` service that exits non-zero repeatedly triggers s6 to mark it as `down`. If `S6_BEHAVIOUR_IF_STAGE2_FAILS` is not set to `2`, s6 enters a restart loop rather than killing the container — the add-on appears "running" in HA UI but is producing nothing. Set `S6_BEHAVIOUR_IF_STAGE2_FAILS=2` so a fatally crashing service causes the container to exit and HA Supervisor can detect and report the failure.

3. **finish script command changed**: In v2, finish scripts called `s6-svscanctl -t /var/run/s6/services` to halt. In v3 this is `/run/s6/basedir/bin/halt`. Using the v2 command in a v3 base image has no effect — the container continues running with a dead service.

4. **Service file permissions lost by git**: s6 service `run` scripts must be executable. Windows git (and some CI systems) strip execute bits. Use `git update-index --chmod=+x services/orchestrator/run` for every service script.

5. **Startup ordering — services start in parallel**: By default s6-rc starts all `longrun` services concurrently. The orchestrator starts before the detector is ready, gRPC connect fails, orchestrator crashes, s6 restarts it in a tight loop. Must declare an explicit dependency (`dependencies` file containing `base\norchestrator` for the orchestrator service) OR implement backoff-polling gRPC health check in the orchestrator before subscribing to HA events.

**Why it happens:**
v1 had no s6 at all (standalone containers). First-time s6 users copy v2 examples without checking which version the base image ships.

**How to avoid:**
- Set `init: false` in add-on `config.yaml`.
- Set `ENV S6_BEHAVIOUR_IF_STAGE2_FAILS=2` in Dockerfile.
- Write finish scripts using `/run/s6/basedir/bin/halt`.
- Create `etc/s6-overlay/s6-rc.d/orchestrator/dependencies` containing `base\ndetector` to order startup.
- `git update-index --chmod=+x` on every `run` and `finish` script.
- Test container exit behavior: kill the orchestrator process and verify the container exits (not loops).

**Warning signs:**
- Container shows "running" in HA Supervisor but no MQTT messages appear.
- `docker logs <container>` shows orchestrator startup messages repeating every few seconds (restart loop).
- `"s6-overlay-suexec: fatal: can only run as pid 1"` at container start.
- Finish script exits 0 but container keeps running.

**Phase to address:**
Add-on scaffolding phase (s6 service structure). The S6_BEHAVIOUR_IF_STAGE2_FAILS and dependency ordering must be in the initial skeleton before any application logic is wired in. Verify with a container-exit integration test.

---

### Pitfall 5: Darts Core Install Pulls Torch on Some Environments, Inflating Image to 3–5 GB

**What goes wrong:**
On a fresh `pip install darts==0.44.1`, the core package does NOT include PyTorch — but pip dependency resolution can pull it in transitively if any other installed package or an older Darts extras marker triggers the torch extra. On aarch64 (Raspberry Pi 4), PyTorch CPU wheel is ~1.8 GB uncompressed. The resulting detector image exceeds the Supervisor's implicit image size expectations and takes 15–30 minutes to pull on a home network.

The Argus detector uses Darts only for STL decomposition (classical model, no neural networks). torch is never needed.

**Why it happens:**
Darts INSTALL.md clearly separates `darts` (core) from `darts[torch]`, but if the requirements.txt was written as `darts[all]` or `darts[torch]` during development experimentation, the production image inherits it. River and PyOD also have optional heavy dependencies (River `[compat]` installs sklearn; PyOD installs torch via some detector classes).

**How to avoid:**
Pin `darts` (not `darts[torch]`, not `darts[all]`) in `requirements.txt`. Add a CI step that checks image size: fail if the detector image exceeds 2 GB compressed. Verify with `pip show torch` inside the built container — must show "not installed". For PyOD, pin only the base `pyod` package without extras; the detectors Argus uses (IForest, HBOS, OCSVM) are all sklearn-backed, not torch-backed.

**Warning signs:**
- `docker build` output shows "Downloading torch-*.whl (1.8 GB)".
- Final detector image is >2.5 GB.
- `docker history <image>` shows a pip layer >500 MB.
- Pi 4 runs out of RAM during model inference (torch allocating GPU buffers even in CPU mode).

**Phase to address:**
Add-on scaffolding phase (Dockerfile authoring for the combined image). Lock `requirements.txt` dependency extras before building the first multi-arch image.

---

## High-Risk Pitfalls

### Pitfall 6: Supervisor MQTT Service Discovery — `services: mqtt:need` Not Declared, or Mosquitto Not Installed

**What goes wrong:**
v2 gets MQTT credentials automatically via the Supervisor API (`bashio::services mqtt "host"`, etc.) instead of manual config. This requires two conditions to both be true: (a) `services: - mqtt:need` declared in add-on `config.yaml`, and (b) the official Mosquitto broker add-on installed and running. If either is absent, the Supervisor API call fails at startup and the orchestrator exits before connecting to MQTT.

A second failure mode: when Mosquitto is reinstalled or its add-on is updated, the Supervisor rotates the auto-generated MQTT credentials. The orchestrator caches credentials in memory from startup. After Mosquitto reinstall, all MQTT publishes fail with "Connection refused: not authorised" until the add-on is restarted.

**Why it happens:**
`services: - mqtt:want` (vs `need`) silently makes the service optional — the API returns empty strings for credentials instead of raising an error, and the orchestrator connects with blank username/password which Mosquitto rejects. The difference between `want` and `need` is easy to miss. Credential caching is the natural pattern for a one-time startup sequence.

**How to avoid:**
- Declare `services: - mqtt:need` (not `want`) unless graceful fallback is explicitly implemented.
- At startup, if `bashio::services.available "mqtt"` returns false, log a clear error and exit 1 rather than attempting a connection with empty credentials.
- Do NOT cache MQTT credentials past the initial connect. Re-read from Supervisor on each reconnect attempt. The Supervisor API is fast (local Unix socket) — there is no perf reason to cache.
- Add a fallback: if `services.available "mqtt"` is false, read MQTT credentials from `options.json` fields (`mqtt_host`, `mqtt_user`, `mqtt_password`) as a manual override. This lets the add-on work without the official Mosquitto add-on.

**Warning signs:**
- MQTT publishes fail immediately after Mosquitto add-on update even though the add-on itself was not restarted.
- "Connection refused: not authorised" in orchestrator logs.
- `bashio::services mqtt "username"` returns an empty string.
- HA entity state frozen at last value despite new sensor events arriving.

**Phase to address:**
Supervisor integration phase (MQTT auto-auth). Implement and test both paths (Mosquitto present vs absent) before declaring MQTT auth complete.

---

### Pitfall 7: `/data` Persistence — Model Files Written Outside the Persistent Volume

**What goes wrong:**
HA add-ons have exactly one persistent directory: `/data`. Everything else (`/app`, `/tmp`, `/etc`) is ephemeral and lost on add-on restart or update. If model files (`.joblib`, River pickle, Darts checkpoint) are written to `/app/models/` or `/tmp/argus/` (as in the v1 compose setup where `/etc/argus/models` was a bind-mount), they are wiped on every restart. The detector rebuilds models from scratch, causing a cold-start detection gap and increased CPU load.

**Why it happens:**
v1 used Docker Compose with an explicit volume mount for model storage. In the add-on, there is no volume declaration in `config.yaml` — the persistent path is fixed at `/data`. Developers porting v1 may forget to update model path config or leave the old `/etc/argus/models` path hardcoded.

**How to avoid:**
- Map all persistent paths to `/data/argus/models/`, `/data/argus/entities.yaml`, and any cert files for the remote mTLS path.
- At startup, print the resolved model path to logs so operators can verify.
- Add `/data` to the `map` list in `config.yaml`: `map: - data:rw`.
- The `options.json`-generated `entities.yaml` must also live in `/data` (not `/etc/argus`) so it survives updates.

**Warning signs:**
- After add-on restart, logs show "model not found, starting with untrained model" for all entities.
- Detection quality degrades immediately after a restart then slowly recovers (cold River HST).
- Model fit timestamps reset to "now" after each restart.

**Phase to address:**
Add-on scaffolding phase (volume and path layout). Confirm `/data` mapping in config.yaml and update all hardcoded paths before porting detector service logic.

---

### Pitfall 8: Add-on Schema Validation Rejects Valid Configuration, Blocking Startup

**What goes wrong:**
The add-on `config.yaml` `schema` block is validated by the Supervisor before the add-on starts. Common mistakes that cause silent rejection:

- **List of entity IDs**: The correct schema type is `[str]` (a YAML list containing a type string), not `str` or `list`. Using `entity_ids: str` accepts only a single string; the UI renders a text input instead of a list editor.
- **Optional fields without default**: To make a field optional, append `?` to the type in the schema block AND omit the key from `options` entirely (no default). If a field appears in `options` with any value (even `null`), it is treated as required and the user must fill it in before the add-on starts. `detector_endpoint: ""` in options makes it required (empty string is a valid value the user must confirm), not optional.
- **Password type**: Use `"password"` type for `influxdb_token` and any MQTT password override fields. Without it, the UI shows credentials in plaintext and they appear in Supervisor logs.
- **No entity selector**: Unlike HA UI Lovelace cards, the add-on schema does NOT support an `entity` selector type. Entity IDs must be entered as plain strings. Document this limitation to users.
- **Schema changes require user re-save**: If a schema field is added after users have already configured the add-on, Supervisor rejects startup until the user visits the UI and saves. New required fields added to a shipped add-on break all existing installs until users manually save.

**How to avoid:**
- Test schema with the Supervisor validation tool before shipping: `docker run --rm -v $PWD:/data homeassistant/amd64-supervisor validate-addon /data/addon`.
- Prefer optional fields with `?` suffix and document defaults explicitly in the description rather than hardcoding them in `options`.
- For `detector_endpoint`: schema `str?`, absent from `options` dict → optional, no default required.
- Treat `password` type as mandatory for any secret field.
- When adding new schema fields to an existing add-on, make them optional with a sensible default to avoid breaking existing installs.

**Warning signs:**
- Add-on fails to start with Supervisor error "Invalid configuration" without further detail.
- Entity ID list is stored as a single concatenated string in `options.json`.
- Credentials visible in Supervisor logs.
- Existing users report add-on won't start after an update (new required field added).

**Phase to address:**
Add-on scaffolding phase (config.yaml schema design). Validate schema with the Supervisor tool as part of the scaffolding phase acceptance criteria.

---

### Pitfall 9: Multi-Arch CI Build — aarch64 QEMU Emulation Makes Detector Builds Prohibitively Slow

**What goes wrong:**
Building the detector image for aarch64 via QEMU emulation on an x86_64 CI runner takes 45–90 minutes because pip must compile native extensions (Cython, numpy, scipy C extensions) under emulation. This makes CI unusable for normal development iteration. Worse, QEMU occasionally hangs on complex native builds, requiring CI timeout kills.

**Why it happens:**
Home Assistant's official builder (`ghcr.io/home-assistant/builder`) uses Docker BuildKit with QEMU for cross-arch builds. Even with Debian base (Path A), some pip packages (scipy, statsmodels, numpy) will compile C extensions if a pre-built manylinux2014_aarch64 wheel is not available or pip's ABI tag matching is too strict.

**How to avoid:**
- Use `--prefer-binary` in all `pip install` calls to force pre-built wheels over sdist compilation: `pip install --prefer-binary --no-cache-dir -r requirements.txt`.
- Explicitly pin versions that have pre-built aarch64 wheels. Verify with `pip index versions <package>` filtering by `linux_aarch64` before pinning.
- Use architecture-specific Dockerfiles (`Dockerfile.aarch64`) with pre-resolved wheel URLs for known-slow packages if needed.
- Add a `[build]` GitHub Actions matrix that runs aarch64 builds on native ARM runners (GitHub now provides arm64 runners) rather than QEMU. This is the correct long-term solution.
- Cache pip wheel layers by hashing `requirements.txt`: Docker layer cache + `--mount=type=cache,target=/root/.cache/pip` in BuildKit.

**Warning signs:**
- CI job exceeds 60 minutes on the detector build step.
- Build log shows "Building wheel for scipy" or "Compiling numpy".
- aarch64 build succeeds on the first run (cold cache) but takes 5x longer than amd64.

**Phase to address:**
Multi-arch build phase. Establish the build matrix and validate CI build times before adding more Python dependencies.

---

## Moderate Pitfalls

### Pitfall 10: AppArmor Denials — .NET GC and gRPC Access `/proc` and `/sys`

**What goes wrong:**
HA OS applies AppArmor profiles to add-ons. The default profile includes `deny /proc/** wl` (deny write/link to /proc). The .NET 8 GC reads `/proc/self/maps` and `/proc/self/status` at startup for memory layout. gRPC's native code may access `/proc/sys/net/core/somaxconn`. On a tight default profile, these accesses are denied and logged as AppArmor AVC denials, which may cause .NET startup latency or, in extreme cases, GC instability.

**How to avoid:**
Include a custom `apparmor.txt` in the add-on directory. Start in `complain` mode, run the add-on, collect denials via `journalctl _TRANSPORT=audit | grep apparmor`, then add the minimum necessary allow rules. A typical .NET add-on needs:
```
/proc/self/maps r,
/proc/self/status r,
/proc/self/statm r,
/sys/kernel/mm/transparent_hugepage/hpage_pmd_size r,
```
Do NOT set `privileged: true` — this disables AppArmor entirely and is a security regression.

**Warning signs:**
- `journalctl _TRANSPORT=audit | grep "DENIED"` shows `/proc/self/maps` denials with the add-on's profile.
- .NET startup is abnormally slow (>5 seconds for a lightweight app).
- GC-intensive operations (large batch scoring) cause unexpected pauses.
- Supervisor audit log shows repeated AppArmor denials.

**Phase to address:**
Hardening/packaging phase. Run the add-on in AppArmor complain mode during integration testing to capture all needed permissions before switching to enforce mode.

---

### Pitfall 11: Grpc.Net.Client on Loopback — Plaintext Channel Rejected by Default HttpClient Settings

**What goes wrong:**
`GrpcChannel.ForAddress("http://localhost:50051")` uses HTTP/2 cleartext (h2c). The default .NET `HttpClient` in some configurations (especially when `AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)` is not set) will refuse h2c connections, producing "Starting HTTP/2 without TLS is not supported" at channel creation.

**How to avoid:**
Add `AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)` in the orchestrator entry point, guarded behind the loopback/local-mode check. This is a known requirement for h2c in .NET and documented in the gRPC-dotnet README. Alternatively, use the gRPC `InProcess` channel (same-process in-memory) since the detector runs as a separate process in s6 but on the same host; h2c over loopback is fine.

**Warning signs:**
- Orchestrator throws `InvalidOperationException: Starting HTTP/2 without TLS is not supported` at channel creation in local mode.
- Works with HTTPS/mTLS in remote mode, fails in local mode.

**Phase to address:**
Conditional mTLS phase. Add as a test case: local mode must connect to h2c loopback without TLS.

---

### Pitfall 12: `version` Field in `config.yaml` Must Match Exactly — Add-on Store Rejects Otherwise

**What goes wrong:**
HA Supervisor requires the `version` field in `config.yaml` to be a plain semver string (e.g., `"2.0.0"`). Common mistakes: using a `v` prefix (`v2.0.0` → rejected), using a build metadata suffix (`2.0.0+build1` → rejected), or having `version` mismatch between `config.yaml` and `repository.yaml`. The add-on will fail to load with a cryptic Supervisor error.

**How to avoid:**
Use bare semver: `2.0.0`. Automate version bumping via a single source-of-truth file (e.g., `VERSION`) that CI writes into `config.yaml` at build time. Validate `config.yaml` in CI before pushing.

**Warning signs:**
- Add-on appears in HA store but shows an error badge instead of an Install button.
- Supervisor logs show "Invalid add-on version" or version mismatch errors.

**Phase to address:**
Add-on scaffolding phase.

---

## Technical Debt Patterns

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| Single Alpine base for both .NET and Python (Path B) | One Dockerfile | .NET musl RID complexity + source wheel compilation on aarch64 QEMU | Never — use Debian base (Path A) |
| `darts[all]` in requirements.txt | No need to track extras | +2 GB from torch on every aarch64 build | Never — always pin core `darts` without extras |
| `services: - mqtt:want` instead of `mqtt:need` | Add-on "starts" without Mosquitto | Cryptic empty-credential MQTT failures | Only if fallback manual MQTT config is fully implemented |
| Hardcode model path to `/app/models` | Simple | Models lost on every restart/update | Never — always use `/data/argus/models` |
| `privileged: true` in config.yaml to bypass AppArmor | Eliminates AppArmor investigation | Breaks HA OS security model; Supervisor may flag as unsupported | Never — write a minimal custom AppArmor profile |
| Cache MQTT credentials from first startup | Avoids Supervisor API call on reconnect | Stale credentials after Mosquitto update cause silent auth failures | Never — re-read credentials on every reconnect |
| `S6_BEHAVIOUR_IF_STAGE2_FAILS` unset | Default behavior | Container appears running but produces nothing when a service crashes | Never — always set to `2` |

---

## Integration Gotchas

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| Supervisor MQTT API | Call `bashio::services mqtt` without checking `bashio::services.available "mqtt"` first | Guard with availability check; fall back to manual options fields if unavailable |
| Supervisor MQTT API | Cache credentials in memory from startup | Re-read credentials from Supervisor on every MQTT reconnect attempt |
| s6-overlay v3 / HA base image | Use v2 `s6-svscanctl -t` in finish script | Use `/run/s6/basedir/bin/halt` in finish scripts |
| gRPC loopback (h2c) | Missing `Http2UnencryptedSupport` switch | Set `AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)` in local mode |
| HA Supervisor API | Read `SUPERVISOR_TOKEN` from environment only at startup | Token is stable within a session; but re-read if add-on update is suspected |
| `/data` volume | Write `options.json`-generated config to `/etc/argus/entities.yaml` | Write generated config to `/data/argus/entities.yaml`; use `/data` for all mutable state |
| mTLS cert path in local mode | Load cert files regardless of mode | Cert loading must be gated behind `detector_endpoint` presence check; no cert files in local mode |

---

## Performance Traps

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| Darts + torch in requirements.txt | Image >3 GB, 30-min pull on RPi | Pin `darts` (core only, no extras); CI image size gate | Day 1 of first multi-arch build |
| QEMU aarch64 pip source compilation | CI build >60 min for detector | `pip install --prefer-binary`; native ARM64 CI runners | Any time a package lacks aarch64 wheel |
| River HST model refit from scratch on every restart | 5–15 min cold-start detection gap | Save River model to `/data` every N inferences; load at startup | After add-on update wipes non-persistent path |
| gRPC health-poll tight loop before detector ready | CPU spike at orchestrator startup | Backoff: 100ms, 200ms, 400ms... max 10s; max 20 retries before exit | If detector s6 service starts slowly (large model load) |
| No `--prefer-binary` in pip install | Source compilation at build time even for packages with available wheels | Always use `--prefer-binary --no-cache-dir` in production Dockerfiles | aarch64 amd64 both; worse under QEMU |

---

## "Looks Done But Isn't" Checklist

- [ ] **Base image selection**: Verify `ldd /app/Argus.Orchestrator.dll` (or the binary) works inside the add-on container — not just on the build host.
- [ ] **s6 service exit propagation**: Kill the orchestrator process manually and confirm the container exits (exit code non-zero), not silently restarts forever.
- [ ] **Local mode mTLS off**: Start add-on with no `detector_endpoint` configured and no cert files in `/data/argus/certs/` — must connect successfully to loopback detector.
- [ ] **MQTT credential rotation**: Reinstall Mosquitto add-on, then verify Argus reconnects with new credentials within 60 seconds without manual intervention.
- [ ] **`/data` persistence**: Restart the add-on; confirm trained model files survive and cold-start detection gap does not occur.
- [ ] **aarch64 image**: Install on a Raspberry Pi 4 (not just x86_64 VM); confirm the add-on starts within 30 seconds with no AppArmor denials.
- [ ] **Darts no-torch**: `docker run --rm <detector-image> python -c "import torch"` must exit with `ModuleNotFoundError`.
- [ ] **Schema optional field**: Remove `detector_endpoint` from options JSON; confirm add-on starts normally (field is optional).
- [ ] **AppArmor complain sweep**: Run the add-on under `complain` AppArmor mode and confirm zero AVC denials remain after capturing and allowing all needed paths.
- [ ] **version field**: Supervisor shows correct version number (no `v` prefix, no build suffix).

---

## Recovery Strategies

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|----------------|
| Wrong base image (glibc binary on musl) | HIGH | Rebuild entire Dockerfile chain with Debian base or musl RID; re-test full stack |
| Torch in image (3 GB detector) | MEDIUM | Remove `[torch]` from requirements.txt, rebuild, republish; users must re-pull (15–30 min on RPi) |
| s6 restart loop (no S6_BEHAVIOUR_IF_STAGE2_FAILS) | MEDIUM | Add ENV, rebuild image, redeploy; diagnose underlying crash first |
| Model files lost (wrong path) | LOW | Models auto-rebuild; detection quality degrades temporarily; fix path and redeploy |
| MQTT stale credentials | LOW | Restart the Argus add-on; fix code to re-read credentials on reconnect |
| AppArmor denials | MEDIUM | Collect denial log, update apparmor.txt, rebuild add-on (no image rebuild needed); supervisor hot-reload |
| Schema breaking change for existing users | HIGH | Make new fields optional with defaults; never add required fields to a shipped add-on without a migration path |

---

## Pitfall-to-Phase Mapping

| Pitfall | Prevention Phase | Verification |
|---------|------------------|--------------|
| .NET on Alpine/musl — binary incompatibility | Scaffolding (base image lock) | `ldd` check inside container; run on real RPi |
| Python ML wheels on musl aarch64 | Scaffolding (base image lock) | `pip install --prefer-binary` dry-run for aarch64; confirm no source builds |
| Conditional mTLS loopback trap | Conditional mTLS phase | Integration test: local mode with no cert files must succeed |
| s6-overlay v3 misconfig | Scaffolding (s6 service structure) | Container-exit test; kill service and verify container exits |
| Darts pulling torch | Scaffolding (Dockerfile authoring) | `python -c "import torch"` must fail in detector image |
| Supervisor MQTT `need` vs `want` | Supervisor integration phase | Test add-on start with Mosquitto absent; confirm graceful error |
| `/data` persistence | Scaffolding (volume layout) | Restart add-on; confirm models survive |
| Add-on schema validation | Scaffolding (config.yaml design) | Supervisor validate-addon tool in CI |
| Multi-arch CI slowness | Multi-arch build phase | CI build time gate: fail if detector image build >20 min |
| AppArmor denials | Hardening phase | Run under complain mode, sweep denial log, enforce |
| h2c loopback rejected | Conditional mTLS phase | Unit test: local-mode channel creation succeeds |
| Version field format | Scaffolding (config.yaml) | CI lints version field against semver regex |

---

## Sources

- [S6-Overlay 3.x update on HA base images — HA Developer Blog](https://developers.home-assistant.io/blog/2022/05/12/s6-overlay-base-images/)
- [s6-overlay v3 README — just-containers/s6-overlay](https://github.com/just-containers/s6-overlay)
- [home-assistant/docker-base (Alpine Dockerfile)](https://github.com/home-assistant/docker-base/blob/master/alpine/Dockerfile)
- [HA Add-on Communication — Supervisor MQTT services API](https://developers.home-assistant.io/docs/add-ons/communication/)
- [HA Add-on Configuration — schema types, services, map](https://developers.home-assistant.io/docs/add-ons/configuration/)
- [Darts INSTALL.md — core vs torch extras](https://github.com/unit8co/darts/blob/master/INSTALL.md)
- [Wheels for musl (Alpine) — Python Packaging Discuss / PEP 656](https://discuss.python.org/t/wheels-for-musl-alpine/7084)
- [Arm64 Alpine dotnet self-contained issue — dotnet/dotnet-docker #4186](https://github.com/dotnet/dotnet-docker/issues/4186)
- [.NET RID Catalog — linux-musl-arm64](https://learn.microsoft.com/en-us/dotnet/core/rid-catalog)
- [AppArmor with HA addons — community discussion](https://community.home-assistant.io/t/apparmor-with-addons/131421)
- [hassio-addons/addon-debian-base — community Debian base images](https://github.com/hassio-addons/addon-debian-base)

---
*Pitfalls research for: HA add-on packaging — .NET 8 + Python ML gRPC multi-process app (Argus v2)*
*Researched: 2026-06-29*
