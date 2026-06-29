# Project Research Summary

**Project:** Argus v2.0 -- Home Assistant Add-on
**Domain:** HA add-on packaging for a multi-process .NET 8 + Python 3.12 gRPC ML app
**Researched:** 2026-06-29
**Confidence:** MEDIUM (stack HIGH, architecture HIGH, pitfalls HIGH, features LOW)

## Executive Summary

Argus v2 repackages the existing v1 .NET 8 orchestrator + Python gRPC detector into a Home Assistant add-on -- a single container managed by the HA Supervisor. The move from docker-compose to the add-on model eliminates all manual env-var configuration (HA URL, token, MQTT credentials) and replaces it with Supervisor-injected auth and service discovery. The correct base image is `ghcr.io/home-assistant/base-debian:bookworm` -- not Alpine -- because .NET 8 requires glibc, and switching to Alpine forces musl RID cross-compilation plus unavailable Python ML wheels on aarch64. This base image decision is irreversible once the first Dockerfile is committed and must be locked in Phase 1.

The architecture maps cleanly to seven sequential phases: skeleton, config-gen, conditional channel factory fix, detector bind/model_root changes, s6 wiring + health gate, multi-arch CI, and integration test. Phases 3 and 4 modify existing v1 source files (`DetectorChannelFactory.cs`, `config.py`, `server.py`); Phases 1-2 and 5-7 are entirely new packaging work. The four entity-selection approaches in FEATURES.md converge on a single recommendation: `[str]` list (Option A) for v2, with startup-log discovery output as the UX gap mitigation. There is no native entity picker in the HA add-on schema system.

The top risk is the conditional mTLS path: `DetectorChannelFactory` must use scheme-level discrimination (`http://` => insecure, `https://` => mTLS), not just cert-load gating, or the loopback detector will fail with an SSL handshake error in local mode. The second risk is MQTT `need` vs `want`: using `mqtt:want` silently delivers empty credentials instead of failing loudly. Both risks have known prevention patterns and must be verified with negative-path integration tests.

---

## Key Findings

### Recommended Stack

The add-on base image must be `ghcr.io/home-assistant/base-debian:bookworm` (Debian 12). It bundles s6-overlay v3, bashio, and tempio. .NET 8 installs from the Microsoft Debian 12 package feed; Python 3.12 is in-repo. Alpine is categorically excluded due to .NET glibc/musl incompatibility and the absence of musllinux aarch64 wheels for scipy/statsmodels/River. The new composable GitHub Actions (`home-assistant/actions/prepare-multi-arch-matrix`, `build-image`, `publish-multi-arch-manifest`) replace the deprecated `home-assistant/builder` action (deprecated 2026.02.1). Both `amd64` and `aarch64` are required targets because Raspberry Pi (aarch64) is the dominant HA platform.

**Core technologies:**
- `ghcr.io/home-assistant/base-debian:bookworm`: add-on base image -- only Debian variant supports .NET 8 glibc ABI
- s6-overlay v3 (`/etc/services.d/` compat layout): process supervision for two long-running processes -- bundled in base image
- bashio: Supervisor API helper -- eliminates manual curl for MQTT discovery and options parsing
- Composable GHA actions (`prepare-multi-arch-matrix`, `build-image`, `publish-multi-arch-manifest`): multi-arch CI -- replaces deprecated `builder` action

**Critical version/flag notes:**
- `init: false` required in `config.yaml` when using the base image `/init` entrypoint
- `ENV S6_BEHAVIOUR_IF_STAGE2_FAILS=2` must be set so a crashing service exits the container rather than looping
- `pip install --prefer-binary` mandatory for aarch64 to avoid QEMU source compilation of scipy/statsmodels
- `darts` (not `darts[torch]`, not `darts[all]`) -- core only, no neural network extras; prevents 3-5 GB image inflation

### Expected Features

The add-on feature scope divides into packaging/UX (new) and ML detection (unchanged from v1). All seven v1 `ARGUS_*` credential env vars are eliminated by Supervisor integration.

**Must have (table stakes):**
- Custom repo + install from HA store (`repository.yaml` + add-on subfolder)
- Configuration tab: entity list (`[str]`), InfluxDB fields, `detector_endpoint?`, batch schedule
- Auto HA auth (`homeassistant_api: true` + `SUPERVISOR_TOKEN`) -- replaces `ARGUS_HA_URL` + `ARGUS_HA_TOKEN`
- Auto MQTT credentials (`services: [mqtt:need]` + bashio) -- replaces all `ARGUS_MQTT_*` vars
- Startup script: reads `/data/options.json`, generates `/data/entities.yaml`, sets s6 env vars
- s6 two-process supervision (detector + orchestrator as longrun services)
- Multi-arch build (amd64 + aarch64) -- aarch64 exclusion is unacceptable for HA
- Watchdog declaration (`tcp://` on gRPC port)
- DOCS.md + icon.png
- Startup log: print discovered numeric sensors (gaps the missing entity picker)

**Should have (differentiators for v2.1+):**
- `translations/en.yaml` for config tab labels -- low effort, high polish
- `include_patterns` / `exclude_patterns` glob fields -- reduces manual entity list burden

**Defer (v3+):**
- Ingress / sidebar panel -- no web UI to put there
- Auto-discovery-only mode (Option C) -- requires exclude-list UX first
- HACS submission -- requires stable public release

**Entity selection conclusion:** Use `[str]` list (Option A). No native entity picker exists in the schema type system. Close the UX gap with a startup log line per discovered numeric sensor. `include_patterns` belongs in a post-v2.0 patch.

### Architecture Approach

The add-on is a single container running two s6 longrun services. A `cont-init.d` oneshot runs first (before any service), reads `options.json`, writes s6 container environment variables for both processes, generates `entities.yaml`, and optionally writes a `down` file to disable the local detector in remote mode. The orchestrator `run` script polls the detector gRPC health endpoint before exec (local mode only). Config-gen is the integration seam between Supervisor and the two existing processes; the processes themselves require minimal code changes.

**Major components:**
1. `addon/rootfs/etc/cont-init.d/10-config-gen.sh` -- central integration: Supervisor => env vars => both processes; writes `down` file for remote mode
2. `addon/rootfs/etc/services.d/{detector,orchestrator}/run` -- s6 longrun scripts; orchestrator polls `wait-detector.py` before exec
3. `DetectorChannelFactory.cs` (modified) -- adds insecure loopback branch: `http://` scheme => `GrpcChannel` without TLS
4. `detector/argus_detector/config.py` + `server.py` (modified) -- adds `ARGUS_GRPC_BIND` and `ARGUS_MODEL_ROOT`; replaces `[::]` hardcode and `/var/argus/models` path
5. `addon/rootfs/usr/local/bin/gen-entities.py` -- converts `options.json` entity array to `entities.yaml` YAML structure
6. `addon/rootfs/usr/local/bin/wait-detector.py` -- synchronous gRPC health poller with backoff; used by orchestrator `run` script

**Data persistence:** `/data/` is the only persistent volume. Model files: `/data/models/`. Generated entities: `/data/entities.yaml`. mTLS certs (remote mode only): `/data/certs/`. All other paths are ephemeral.

### Critical Pitfalls

1. **.NET on Alpine/musl binary incompatibility** -- lock `base-debian:bookworm` in Phase 1 before writing any service file. Verify with `ldd` inside the container. Container exits immediately with no .NET logs if wrong base used. Cross-confirmed in STACK.md and PITFALLS.md: use Debian, never Alpine for this app.

2. **Conditional mTLS loopback trap** -- discriminate on URI scheme, not cert-load presence. `http://127.0.0.1:50051` => `GrpcChannel.ForAddress(...)` with no credentials. `https://` => existing mTLS path unchanged. Also requires `AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)` in local mode. Negative test: local mode with zero cert files must succeed.

3. **`mqtt:need` vs `mqtt:want`** -- `want` returns empty strings silently; Mosquitto rejects blank-credential connect. Use `need`. Guard with availability check; exit 1 if MQTT unavailable. Re-read credentials on every MQTT reconnect; never cache.

4. **s6-overlay v3 misconfiguration** -- set `init: false` in `config.yaml`, `ENV S6_BEHAVIOUR_IF_STAGE2_FAILS=2` in Dockerfile, `/run/s6/basedir/bin/halt` in finish scripts. Mark all `run`/`finish` scripts executable via `git update-index --chmod=+x`. Verify container exits (not loops) when a service dies.

5. **Darts pulling PyTorch** -- pin `darts` (no extras) in `requirements.txt`. CI gate: `python -c "import torch"` must fail inside built image. Image must stay under 2 GB compressed.

---

## Implications for Roadmap

### Phase 1: Add-on Skeleton
**Rationale:** Base image selection drives every downstream decision. Lock Debian base, repo layout, and config.yaml schema before any code changes.
**Delivers:** `repository.yaml`, `addon/config.yaml` (full options schema), bare `addon/Dockerfile`, `addon/build.yaml`, Supervisor `validate-addon` passing in CI.
**Addresses:** Entity list (`[str]`), InfluxDB fields, `detector_endpoint?`, watchdog, `boot: auto`, `init: false`, `S6_BEHAVIOUR_IF_STAGE2_FAILS=2`
**Avoids:** Pitfalls 1 (musl), 4 (s6 v3 misconfig), 5 (Darts/torch baseline), 8 (schema validation), 12 (version field format)

### Phase 2: Config-Gen Entrypoint
**Rationale:** Config-gen is the integration seam. Nothing downstream can be tested without knowing what env vars it produces. Must read `EntitiesConfigLoader` YAML structure before implementing `gen-entities.py`.
**Delivers:** `cont-init.d/10-config-gen.sh`, `gen-entities.py`, `wait-detector.py` stub, s6 env var injection for MQTT + HA auth + ARGUS_* vars, `/data/entities.yaml` generation, `down` file for remote mode, `/run/argus/mode` file
**Addresses:** Auto HA auth, auto MQTT creds, entities.yaml bridge, mode detection, /data path layout
**Avoids:** Pitfall 3 (MQTT need vs want), Pitfall 7 (/data persistence)
**Research needed:** Read `EntitiesConfigLoader` source; resolve whether `ARGUS_HA_URL/TOKEN` vars are needed or only `HomeAssistant__*`

### Phase 3: Conditional Channel Factory (Orchestrator)
**Rationale:** `DetectorChannelFactory.cs` must be fixed before s6 wiring can be tested end-to-end. Can run in parallel with Phase 4.
**Delivers:** Modified `DetectorChannelFactory.cs` with insecure loopback branch; `Http2UnencryptedSupport` switch in local mode; unit tests for both paths; negative test: local mode, no cert files, must succeed.
**Avoids:** Pitfall 2 (mTLS loopback trap), Pitfall 11 (h2c rejected by default HttpClient)

### Phase 4: Detector Local-Mode Bind
**Rationale:** Independent of Phase 3; both depend on Phase 2 env var contract. Run in parallel with Phase 3.
**Delivers:** `ARGUS_GRPC_BIND` + `ARGUS_MODEL_ROOT` in `config.py`; `server.py` uses `config.grpc_bind`, passes `config.model_root` to `serve()`; unit tests.
**Addresses:** Local mode binds to `127.0.0.1`; model files persist to `/data/models/`
**Avoids:** Pitfall 7 (model files outside /data)

### Phase 5: s6 Service Wiring + Readiness Gate
**Rationale:** Final assembly. Requires Phases 3 and 4. Health gate (`wait-detector.py`) blocks orchestrator until detector is SERVING.
**Delivers:** `services.d/detector/run`, `services.d/orchestrator/run` (with health poll), finish scripts; integration test: both processes start, gRPC call succeeds, container exits on process kill.
**Addresses:** Table stakes: s6 two-process supervision, watchdog
**Avoids:** Pitfall 4 (s6 v3 misconfig -- finish scripts, exit propagation, startup ordering)
**Research needed:** Confirm NetDaemon.Client internal proxy hostname (`supervisor` vs `homeassistant`) on live HA OS before finalising orchestrator `run` script.

### Phase 6: Multi-Arch CI
**Rationale:** Only after runtime is stable. Validates aarch64 wheel resolution and image size.
**Delivers:** `.github/workflows/build.yml` with composable GHA matrix; `--prefer-binary` pip flag; CI gate on image size (<2 GB) and torch absence; GHCR push on release tag.
**Addresses:** Multi-arch (amd64 + aarch64); replaces deprecated builder action
**Avoids:** Pitfall 9 (QEMU aarch64 CI slowness), Pitfall 5 (Darts/torch in image)
**Research needed:** Confirm native ARM64 GitHub Actions runner availability; fallback to QEMU + `--prefer-binary` + 20 min CI gate if unavailable.

### Phase 7: End-to-End HA Integration Test
**Rationale:** Full install flow from custom repo URL to live entity detection. Covers gaps not testable in container tests.
**Delivers:** Install from custom repo on live HA OS; entity detection working; DOCS.md; icon.png; startup log showing discovered sensors.
**Addresses:** All table stakes: install flow, DOCS.md, icon, startup entity discovery log
**Avoids:** Pitfall 6 (MQTT credential rotation -- verify reconnect after Mosquitto reinstall)

### Phase Ordering Rationale

- Phase 1 first: base image locks all subsequent build decisions
- Phase 2 before 3/4: config-gen defines the env var contract both processes consume
- Phases 3 and 4 can run in parallel: different codebases, same input from Phase 2
- Phase 5 after both 3 and 4: integration point for both
- Phase 6 after Phase 5: CI builds a stable runtime, not a work-in-progress
- Phase 7 last: requires publishable image from Phase 6

### Research Flags

Phases needing deeper research during planning:
- **Phase 2:** `EntitiesConfigLoader` YAML format not read during research; resolve before implementing `gen-entities.py`. Open question: does `NetDaemonHaEventSource` read `ARGUS_HA_URL/TOKEN` directly, or only via `IHomeAssistantClient` (`HomeAssistant__*`)? Answer determines how many env vars config-gen writes.
- **Phase 5:** Supervisor internal proxy hostname (`supervisor` vs `homeassistant`) must be confirmed on live HA OS before finalising orchestrator `run` script.
- **Phase 6:** Native ARM64 GitHub Actions runner availability; fallback plan if unavailable.

Phases with standard patterns (skip research):
- **Phase 3:** Exact code snippets documented in ARCHITECTURE.md and PITFALLS.md
- **Phase 4:** Two-file, two-line changes; standard env var pattern
- **Phase 7:** HA add-on install flow is documented and stable

---

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | Official HA developer docs + ghcr.io release tags; builder deprecation verified |
| Features | LOW | FEATURES.md source tier is LOW (web/webfetch); schema type system confirmed but entity-picker absence is documented limitation |
| Architecture | HIGH | Derived from live codebase reads; component boundaries and env var contracts are concrete |
| Pitfalls | HIGH | Multiple authoritative sources; each pitfall has confirmed warning signs and recovery steps |

**Overall confidence:** MEDIUM

### Gaps to Address

- **`EntitiesConfigLoader` YAML format**: Not read during research. `gen-entities.py` must match the loader expected structure exactly. Resolve at Phase 2 plan time by reading the loader source.
- **`ARGUS_HA_URL`/`ARGUS_HA_TOKEN` consumers**: Unknown whether `NetDaemonHaEventSource` reads these directly or delegates to `IHomeAssistantClient`. If only the latter, drop both vars from config-gen. Resolve at Phase 2 by searching callers of `ConnectionSettings.HaUrl` and `HaToken`.
- **Supervisor internal proxy hostname**: `supervisor` vs `homeassistant` -- confirm on live HA OS before Phase 5. Wrong hostname => NetDaemon.Client fails silently.
- **`mqtt:need` with Zigbee2MQTT embedded broker**: Supervisor service discovery returns nothing if user runs Z2M built-in broker (not official Mosquitto add-on). Deferred to v3; DOCS.md must warn users. No v2 code change.
- **mTLS cert base64 field length**: `tls_ca/cert/key` as `password?` fields -- HA schema may have implicit max string length that silently truncates base64 cert content. Verify against Supervisor validation before Phase 1 ships.
- **`trixie` vs `bookworm`**: `base-debian:trixie` (Debian 13) is available; `bookworm` is the correct v2 choice. Revisit for v3.

---

## Sources

### Primary (HIGH confidence)
- [HA Developer Docs -- Add-on Configuration](https://developers.home-assistant.io/docs/add-ons/configuration/) -- schema types, services, map, init, watchdog
- [HA Developer Docs -- Add-on Communication](https://developers.home-assistant.io/docs/add-ons/communication/) -- SUPERVISOR_TOKEN, homeassistant_api, Services API
- [github.com/home-assistant/docker-base](https://github.com/home-assistant/docker-base) -- base image variants, release 2026.06.1
- [github.com/just-containers/s6-overlay](https://github.com/just-containers/s6-overlay) -- v3.2.3.0 layout, services.d/ compat path
- [github.com/hassio-addons/bashio](https://github.com/hassio-addons/bashio) -- bashio::services, bashio::config signatures
- [HA Developer Blog -- S6-Overlay v3](https://developers.home-assistant.io/blog/2022/05/12/s6-overlay-base-images/) -- init: false requirement
- Live codebase reads: DetectorChannelFactory.cs, config.py, server.py, Program.cs

### Secondary (MEDIUM confidence)
- [github.com/home-assistant/addons-example](https://github.com/home-assistant/addons-example) -- official example using services.d/ layout
- [github.com/home-assistant/addons/mosquitto/config.yaml](https://github.com/home-assistant/addons/blob/master/mosquitto/config.yaml) -- mqtt:provide, watchdog tcp:// pattern
- [GH marketplace -- home-assistant/builder deprecation](https://github.com/marketplace/actions/home-assistant-builder)

### Tertiary (LOW confidence)
- [Darts INSTALL.md](https://github.com/unit8co/darts/blob/master/INSTALL.md) -- core vs torch extras; not verified against 0.44.1 pip resolution
- [Python Packaging PEP 656 -- musllinux wheel status](https://discuss.python.org/t/wheels-for-musl-alpine/7084) -- wheel availability as of mid-2026; may improve

---
*Research completed: 2026-06-29*
*Ready for roadmap: yes*