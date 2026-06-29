# Pitfalls: Argus

Domain: home sensor anomaly detection, .NET + Python gRPC hybrid, MQTT HA discovery.
Researched: 2026-06-09.

---

## Alert Fatigue from Unsupervised Detectors on Home Data

**Warning signs**: Binary sensor flaps on every shower, HVAC startup, window-open event. Score sensor oscillates around threshold. Users disable entities because they cry wolf.

**Prevention**:
- Hysteresis gate at the orchestrator layer, not inside the detector. Detector emits raw scores; orchestrator holds state and only flips `binary_sensor` ON when score exceeds `high_threshold` for N consecutive readings, and only flips OFF when score drops below `low_threshold` for M consecutive readings. Tune per entity class (outdoor temp vs. indoor humidity need different thresholds).
- Calibration window on first deploy: run detector in score-only mode (no flag publishing) for 7–14 days, collect percentile distribution, then set thresholds at p95/p99 of normal-operation scores. Surface this as a per-entity calibration phase in `entities.yaml`.
- Context suppression: suppress anomaly publication during known-noisy windows (e.g. suppress shower-room humidity 06:00–09:00). Encode suppression windows in config, not in the model.
- For Half-Space Trees specifically: the anomaly score is not calibrated across entity types. Normalise scores to [0,1] per entity before applying global thresholds — raw HST scores are not comparable between entities with different feature ranges.

**Phase**: Phase 1 (streaming path). Implement hysteresis before first end-to-end demo or the system will appear broken from day one.

---

## MQTT Discovery: Orphaned Entities on Rename or Config Change

**Warning signs**: HA shows duplicate entities after an `entity_id` rename in `entities.yaml`. Old entities persist with "unavailable" state even after orchestrator restart. Entity names appear with `_2` suffix.

**Prevention**:
- `unique_id` in the discovery payload is the anchor. Derive it deterministically from `{source_entity_id}__{detector}__{component}` (e.g. `sensor.outdoor_temp__hst__score`). Never use a random UUID or timestamp.
- Changing `unique_id` creates a new entity; HA does NOT remove the old one. To rename a published entity you must first send a discovery payload with an empty string body (`""`) to the same config topic to tombstone it, wait for HA to remove it, then re-publish under the new `unique_id`.
- All discovery config payloads must use `retain: true`. Without this, HA loses the entity on broker restart and marks it unavailable. HA re-subscribes to discovery topics on restart and replays retained messages.
- If retain is set and config changes (e.g. `name` field), HA will update in place only if `unique_id` is stable. Test this explicitly.
- Wire an MQTT LWT (Last Will and Testament) on the availability topic for each entity group. Set LWT payload to `"offline"` on disconnect so HA marks entities unavailable rather than stale.

**Phase**: Phase 1. MQTT entity model must be defined before any detector work starts — retrofitting `unique_id` generation is painful.

---

## gRPC .NET + Python: Proto Field Naming and Default Value Traps

**Warning signs**: Fields with zero/false/empty values are missing from deserialized structs on the Python side. Timestamp fields are silently dropped. Score of 0.0 treated as "no score".

**Prevention**:
- Proto3 drops default values on the wire (0, false, "", empty repeated). A score of exactly `0.0` will not be transmitted. Never use 0.0 to mean "no anomaly" — use a wrapper type (`google.protobuf.FloatValue`) or a separate `has_score` bool field for optional scores.
- Use `google.protobuf.Timestamp` for all timestamps, not `int64 unix_ms`. grpc-dotnet and grpcio both have first-class support and it avoids epoch/precision mismatches.
- Field names in `.proto` must be `snake_case`. grpc-dotnet generates C# `PascalCase` properties; grpcio generates Python `snake_case` attributes. The wire encoding is field-number-based so there is no runtime mismatch, but codegen inconsistencies arise if you deviate from proto style guide.
- Pin `grpcio` and `grpcio-tools` to matching versions (e.g. both `1.68.x`). Mixed versions can cause silent channel errors. Pin in `requirements.txt` with `==`.
- Test the actual .NET → Python path with a real message in CI, not just unit tests of each side separately.

**Phase**: Phase 1 (proto contract definition). Fix proto design before any other code is written.

---

## mTLS: Certificate Hostname/SAN Mismatch and In-Memory Expiry

**Warning signs**: gRPC channel gets `RemoteCertificateNameMismatch` on .NET side at startup. Connection works on first boot then fails silently after certificate rotation until process restarts. `grpcio` on Python side raises `StatusCode.UNAVAILABLE` with SSL handshake error.

**Prevention**:
- Generate certificates with SANs that include both the hostname AND the LAN IP of the GPU host. If you use only CN, modern TLS rejects it. If the GPU host IP changes (DHCP), use hostname-based SAN and rely on local DNS/hosts file.
- grpc-dotnet loads certificates at channel creation and holds them in memory. If you rotate certificates, the .NET channel will use the old cert until the process restarts. Either: (a) rebuild the channel on cert file change (use `FileSystemWatcher`), or (b) set cert expiry to 1–2 years and do a full redeploy on rotation. Option (b) is fine for a single-operator setup; option (a) is the production-correct approach.
- Python `grpcio` accepts cert bytes at channel creation time — same problem. Wrap channel construction in a factory that re-reads cert files from disk and rebuild the channel after rotation.
- For self-signed CA: embed the CA cert in both the .NET `HttpClientHandler` trust store and the Python `ssl_credentials` call. Do not disable certificate validation even for LAN-only traffic.
- Minimum viable approach for this project: generate a self-signed CA, issue server + client certs from it, set 2-year expiry, store in `deploy/certs/`, document rotation procedure.

**Phase**: Phase 2 (two-host deployment). mTLS is not required for single-host dev but must be solved before cross-host integration.

---

## Model Drift: Seasonal Data Breaking Trained Models

**Warning signs**: Anomaly rate spikes every season change (March, October). Models trained in summer flag normal winter indoor-temperature ranges as anomalies. HST drift detection not triggered because the drift is gradual.

**Prevention**:
- For River HST (streaming detector): HST has a `window_size` parameter that controls how fast the model adapts. Tune this aggressively for outdoor sensors (smaller window = faster adaptation). Indoor sensors change more slowly; larger window is fine.
- Wire River's ADWIN or DDM drift detectors as a sidecar on each entity's streaming pipeline. When drift is detected, log it and optionally reset the model or reduce the window. Do not silently continue with a drifted model.
- For PyOD batch detectors: scheduled weekly re-fit using the last 30 days of InfluxDB history. Do not re-fit on the full history — it will smooth out the seasonality rather than adapting to current conditions. Store model versioned with a fit timestamp so you can roll back.
- STL residual detector is inherently seasonal-aware (it decomposes seasonality out), but it requires at least 2 full seasonal periods of history for reliable decomposition. Do not deploy STL for outdoor temp until you have 365+ days of data. Use HST as the primary detector in Phase 1.
- Store per-entity model metadata (fit_date, fit_window_days, detector_version) alongside the serialized model. Surface this as a `sensor.argus_{entity}_model_age_days` entity in HA so stale models are observable.

**Phase**: Phase 2 (batch path). Addressed during model lifecycle implementation. Phase 3 adds scheduled re-fit.

---

## Clock Skew: HA Events vs InfluxDB Timestamps

**Warning signs**: Streaming path score and batch path score diverge for the same event. InfluxDB backfill produces duplicate or missing points. River model receives out-of-order feature values during replay.

**Prevention**:
- HA `state_changed` events carry `last_changed` (ISO 8601) in the event data. Use this timestamp, not the orchestrator's `DateTime.UtcNow` at event receipt. The two can differ by up to 1–2 seconds under load.
- InfluxDB v2/v3 accepts writes with past timestamps within a configurable lag window (default 1 hour in v1, configurable in v2). If the edge host clock drifts, writes can be rejected silently. Run NTP (`chronyd`) on both hosts and verify synchronization.
- For the streaming path: River's online models are order-sensitive. If HA sends a burst of state_changed events after a reconnect, they arrive in delivery order, not `last_changed` order. The orchestrator must sort events by `last_changed` before feeding them to the gRPC stream or accept that brief reconnect bursts will produce minor score noise.
- Do not mix HA event timestamps and InfluxDB query timestamps in the same pipeline step. Pick one authoritative timestamp per record and carry it through to the MQTT publish. Include it in the payload as `last_updated` so HA can use it for history display.
- InfluxDB deduplication uses `{measurement, tag set, timestamp}` as the key. If you write the same point twice with the same timestamp, the second write silently wins. This is correct for idempotent retries but dangerous if timestamps have been truncated or rounded.

**Phase**: Phase 1 (data ingress). Must be handled before any correctness guarantees can be made.

---

## gRPC Streaming Backpressure: Python Server Getting Ahead of .NET Consumer

**Warning signs**: .NET orchestrator memory grows under high HA event rate. Python server-streaming RPC sends faster than orchestrator consumes. grpcio >= 1.4.0 relieves backpressure whenever the client consumes any message, causing the server to flood ahead.

**Prevention**:
- This affects server-streaming RPCs (Python → .NET). For the `ScoreStream` use case (orchestrator feeds sensor events to Python, Python returns scored results), this is a bidirectional or client-streaming pattern, which is less affected.
- If Python is the streaming server: add a `context.is_active()` check before each `yield` in the Python servicer. If the channel is not active, stop yielding. This prevents unbounded queue growth on the server side.
- On the .NET client side: use `IAsyncEnumerable` consumption with a bounded channel (`Channel.CreateBounded<T>(capacity)`) as a buffer between gRPC receipt and processing. If the buffer fills, apply backpressure upstream (stop ACKing events from HA WebSocket).
- For the HA WebSocket → gRPC path specifically: HA `state_changed` events for environmental sensors arrive at roughly 1–5 Hz under normal conditions. This is well within gRPC and River HST throughput. Backpressure only becomes a concern during HA restarts (burst of synthetic state_changed events for all entities) or bulk entity imports.
- Mitigation for startup burst: on reconnect to HA WebSocket, call `get_states` once and ingest only current values, then subscribe to `state_changed`. Do not replay the reconnect burst through the detection pipeline.

**Phase**: Phase 1 (streaming path). Implement bounded buffer at HA WebSocket event ingestion before wiring to gRPC.

---

## Restart Sequencing: Detection Gaps and Duplicate MQTT Publishes

**Warning signs**: After orchestrator restart, HA shows a period of `unknown` on anomaly sensors. After Python detector restart, orchestrator retries and publishes the same detection result twice, creating a spurious anomaly flip. River model state is lost if detector restarts mid-stream.

**Prevention**:
- Startup order: MQTT broker → HA (already running) → Python detector → .NET orchestrator. The orchestrator must not connect to HA WebSocket until the gRPC channel to the detector is healthy. Use a gRPC health check (`grpc.health.v1`) on the Python side and poll it from the orchestrator before subscribing to events.
- Python detector startup: load all persisted models from disk before accepting any gRPC connections. If a model file is corrupt or missing, start with a fresh untrained model and log a warning — do not crash.
- On orchestrator restart: re-publish all discovery config payloads with `retain: true` before subscribing to HA events. This ensures HA has fresh entity config even if it restarted while orchestrator was down.
- On detector restart: River HST models save/load state via `pickle`. Save model state to disk after every N inferences (e.g. N=100) not just on clean shutdown. Use atomic write (write to `.tmp`, rename to final) to prevent corrupt model files on crash.
- Idempotent MQTT publish: before publishing an anomaly state change, compare with the last published value stored in memory. Only publish if the value changed. This prevents spurious flips on reconnect.
- MQTT availability topic: publish `"online"` to the availability topic immediately after all models are loaded and gRPC server is listening. Publish `"offline"` via LWT. HA will correctly show entities as unavailable during gap.

**Phase**: Phase 2 (restart resilience requirement). Partially addressed in Phase 1 (LWT setup), fully hardened in Phase 2.

---

## PyOD/Joblib Model Persistence Across Python Upgrades

**Warning signs**: After `pip install --upgrade` of scikit-learn or PyOD, loading existing `.joblib` files raises `ValueError` or produces wrong results. No error is logged; model silently makes incorrect predictions.

**Prevention**:
- Pin the full Python environment in `requirements.txt` with `==` for PyOD, scikit-learn, numpy, and joblib. Lock transitive dependencies with `pip-compile` or use a `requirements.lock` file.
- Store the scikit-learn and PyOD version alongside each model file (in a sidecar `.json` metadata file). On model load, compare stored versions to runtime versions and refuse to load if they differ — force a re-fit instead.
- Treat model files as version-tagged artifacts: `model_{entity_id}_{detector}_{pyod_version}_{fit_date}.joblib`. Never overwrite in place; keep the last 2 versions.
- River models use a different serialization path (River's own `pickle`-based save/load). River is less prone to version breakage than PyOD/sklearn but apply the same metadata discipline.
- For the GPU host specifically: use a pinned Docker image (`FROM python:3.11.x`) or a virtualenv with version-locked deps. Do not use system Python.

**Phase**: Phase 2 (model lifecycle). Define versioning scheme before first model is written to disk.

---

## STL Seasonal Detector: Insufficient History and Anomaly Contamination

**Warning signs**: STL residuals have non-zero mean after decomposition (trend not fully removed). Anomaly contamination in training data causes seasonal component to absorb anomalies, masking them in the residual. Detector reports no anomalies during a genuine spike.

**Prevention**:
- STL requires a minimum of 2× the seasonal period for reliable decomposition. For daily seasonality (period=24h), you need 48h minimum; for weekly (period=168h), 336h minimum. Do not enable STL-based detection until sufficient history exists in InfluxDB.
- Use robust STL (available in `statsmodels.tsa.seasonal.STL` with `robust=True`) which downweights outlier influence during decomposition. Standard STL will absorb anomalies into the seasonal component, making them invisible in the residual.
- For Darts-based STL wrapping: verify that the model's `season_length` parameter matches actual data periodicity. Misconfigured season_length is the most common cause of STL detection failures on home sensor data.
- Do not use STL as the only detector in Phase 1. Deploy HST first; add STL as a secondary detector in Phase 3 once history is available. Compare their outputs rather than replacing one with the other.
- Outdoor pressure data rarely has strong daily seasonality; avoid STL for pressure, prefer RobustZScore/MAD.

**Phase**: Phase 3 (advanced detectors). Do not attempt STL before the batch history path is validated.

---

## HA WebSocket Reconnect: Missed Events and State Replay Burst

**Warning signs**: After HA or orchestrator restart, a burst of 50–200 `state_changed` events arrives as HA replays current state. River model receives an artificial spike of correlated readings. Anomaly scores temporarily spike and trigger false alerts.

**Prevention**:
- The HA WebSocket API has no event replay mechanism. There is no "give me events since timestamp X." On reconnect, call `get_states` to refresh current state, then subscribe to `state_changed` going forward. Accept the gap rather than replaying a burst.
- After a reconnect, suppress anomaly flag publication for a configurable cooldown period (e.g. 60 seconds). Continue scoring and updating the River model but do not flip the binary sensor during this window. This prevents the reconnect burst from causing a flapping episode.
- Implement exponential backoff on HA WebSocket reconnect: 1s, 2s, 4s, 8s, max 60s. Do not hammer the HA WebSocket on repeated failures — HA will rate-limit or close the connection.
- Track `last_seen` per entity. If an entity has been unseen for > 2× its normal reporting interval, mark it as stale in the orchestrator and publish `unknown` to HA rather than the last score. This prevents stale scores from being displayed as current.

**Phase**: Phase 1 (HA connection layer). Reconnect handling and cooldown must be in the first streaming implementation.

---

## Phase-Specific Warning Index

| Phase | Topic | Likely Pitfall | Mitigation |
|-------|-------|---------------|------------|
| 1 | Proto contract | Default-value field drops (score=0.0 lost) | Use FloatValue wrapper or explicit has_score bool |
| 1 | HA WebSocket | Reconnect burst triggers false anomalies | Post-reconnect cooldown, get_states refresh |
| 1 | MQTT discovery | unique_id instability causes duplicate entities | Derive unique_id deterministically, never random |
| 1 | Streaming | HST score not calibrated across entities | Per-entity score normalization before threshold |
| 1 | Clock | last_changed vs receipt time diverge | Use event's last_changed, not UtcNow |
| 2 | mTLS | SAN mismatch on first cross-host connect | Generate cert with both hostname and LAN IP in SAN |
| 2 | Restart | Orchestrator starts before detector is ready | gRPC health check gate before HA subscription |
| 2 | Model files | joblib incompatibility after Python upgrade | Pin deps, store version in sidecar metadata |
| 2 | MQTT | Entities unavailable after restart | LWT + retain on all discovery payloads |
| 3 | STL | Insufficient history causes bad decomposition | Gate STL behind 2× seasonal period minimum |
| 3 | Model drift | Summer model fails in winter | ADWIN sidecar + weekly re-fit on rolling 30d window |
| 3 | Certs | In-memory cert not refreshed after rotation | Rebuild gRPC channel on cert file change |
