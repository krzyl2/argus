# Features: Argus

**Domain:** Self-hosted environmental anomaly detection for Home Assistant
**Researched:** 2026-06-09
**Confidence:** HIGH (detection taxonomy), MEDIUM (HA UX patterns), HIGH (anti-features)

---

## Table Stakes

Features users expect from a functioning anomaly detection system. Absence means the system is not credible.

---

### Point Spike Detection

**Why table stakes:** Spikes are the most obvious anomaly class and the first thing any operator will notice being missed. A system that doesn't catch +15°C instantaneous spikes on a temp sensor is broken, not useful.

**What it is:** Detects single-sample deviations that exceed N standard deviations (or MAD units) from recent baseline. Covers sensor glitches, radio noise, ESPHome firmware bugs.

**Implementation:** RobustZScore / MAD via PyOD `MAD` or manual rolling implementation. Stateless per observation once baseline is established.

**Complexity:** Low

---

### Step Change / Level Shift Detection

**Why table stakes:** A sensor that reads +5°C permanently after a firmware update or physical disturbance (window left open all winter) is the second most common real-world failure mode. Operators will lose trust in the system if step changes are not surfaced.

**What it is:** Detects a sustained shift in the baseline level — distinguishable from a spike by persistence over multiple samples. Implemented as comparison between recent short window vs longer historical window.

**Implementation:** CUSUM or two-window mean comparison in PyOD / River. Alternatively handled by STL trend residual exceeding a threshold.

**Complexity:** Medium

---

### Frozen / Stuck Sensor Detection

**Why table stakes:** Zigbee/Z-Wave/WiFi sensors occasionally stop reporting updates or get stuck reporting the last known value indefinitely. Temperature stuck at 21.5°C for 6 hours while a window is open is a common real-world failure. HA community discussion confirms this is a widely reported issue. Without this, the system silently passes garbage data into downstream automations.

**What it is:** Detects when a sensor's variance over a rolling window drops below a floor threshold (near-zero variance) for a sustained period. Complements time-since-last-update watchdog (which catches total silence but not stuck-value repeats).

**Implementation:** Rolling variance check in orchestrator or detector. Simple rule: `stddev(last N readings) < epsilon` for N > threshold_minutes. No ML needed.

**Complexity:** Low

---

### Streaming Path with < 2s Latency

**Why table stakes:** Explicitly required in PROJECT.md as the core value proposition. Without streaming detection, Argus is a batch monitoring tool, not a real-time anomaly system.

**What it is:** HA WebSocket `state_changed` → orchestrator → gRPC ScoreStream → detector → MQTT discovery entity update, end-to-end under 2 seconds.

**Complexity:** High (integration complexity, not algorithmic complexity)

---

### MQTT Discovery Entity Creation (binary_sensor + score)

**Why table stakes:** The auto-created entity pair is the entire HA-visible surface of Argus. Manually created entities defeat the purpose. Stable `unique_id` is required so HA doesn't orphan entities on restart. The retain flag on state topics is required so HA gets current state on reconnect without waiting for the next sensor event.

**What it is:** For each monitored entity, publish two MQTT discovery payloads: one `binary_sensor` (anomaly on/off) and one `sensor` (raw anomaly score 0–1). Both grouped under a single MQTT device per source entity.

**Implementation:** homeassistant/binary_sensor/{entity_id}_anomaly/config + homeassistant/sensor/{entity_id}_score/config with `retain: true` on state topics.

**Complexity:** Medium

---

### Per-Entity Model Persistence

**Why table stakes:** Without persisting fitted models, every orchestrator restart requires a full warm-up period before detection is meaningful. For River Half-Space Trees this can be minutes; for PyOD batch models it can require re-fitting on historical data.

**What it is:** Save/load fitted model state keyed by (entity_id, detector_type, version). joblib for PyOD, native River/Darts serialization.

**Complexity:** Low-Medium

---

### Graceful Degradation to `unavailable`

**Why table stakes:** If Argus marks a sensor anomaly as `off` when the detector is unreachable, the operator gets false assurance. HA has a defined `unavailable` state for MQTT entities (via `availability_topic` and LWT). Using it correctly is the difference between a trustworthy system and one that silently lies.

**What it is:** When detector host is unreachable or gRPC call fails, orchestrator publishes `offline` to the entity's `availability_topic`. HA renders the anomaly sensors as `unavailable`, not `off`.

**Complexity:** Low (MQTT LWT pattern is well-documented)

---

### Hysteresis / Anti-Flap on Binary Sensor

**Why table stakes:** Without hysteresis, a score hovering at the detection threshold will flip the binary_sensor on/off repeatedly, generating spurious state history and automations. HA's Threshold helper supports this natively. Argus must implement equivalent logic in the score→binary translation layer.

**What it is:** Binary anomaly state only transitions `off→on` when score exceeds `threshold + hysteresis_up`; transitions `on→off` only when score drops below `threshold - hysteresis_down`. Configurable per entity.

**Complexity:** Low

---

### Config-Driven Entity Registration

**Why table stakes:** Adding a new room sensor must not require code changes or redeployment. A single `entities.yaml` edit and restart of the orchestrator is the acceptable operator workflow.

**Complexity:** Low

---

## Differentiators

Features that materially increase detection quality or operator experience beyond baseline. Not expected by default, but high value once the table stakes work.

---

### Seasonal / Diurnal Decomposition (STL)

**Why differentiating:** Environmental sensors have strong 24h and annual cycles. A temperature that reads 28°C is not anomalous on a hot afternoon but is at 3 AM in winter. A flat-threshold detector generates massive false positive rates without decomposing out seasonality. STL decomposition separates trend + seasonal from residual, and anomaly detection runs on the residual only. This is the single largest precision improvement available for this domain.

**What it is:** Darts `STLDecomposer` applied in the batch path on historical data. Residuals fed to PyOD. Streaming path approximates with a rolling seasonal baseline (hour-of-day rolling mean/std) until enough history exists for full STL.

**Dependency:** Requires batch path and InfluxDB history. Cannot be used cold-start; needs minimum ~7 days of history for daily periodicity, ~60 days for weekly.

**Complexity:** High (cold-start bootstrap, seasonal period selection, integration with streaming path)

---

### Outdoor Temperature / Pressure as Covariate

**Why differentiating:** Indoor temperature is strongly conditionally dependent on outdoor temperature and atmospheric pressure. A 3°C indoor drop is anomalous in a sealed house in summer but expected when outdoor temp drops 15°C overnight. Conditioning detection on outdoor covariates reduces weather-driven false positives and increases sensitivity to genuine HVAC/insulation failures.

**What it is:** Include outdoor sensor readings as conditioning features in multivariate detectors (e.g., HBOS, IForest in PyOD with multi-feature vectors). Outdoor sensors are not flagged themselves but provide context for indoor readings.

**Dependency:** Requires outdoor sensors to be reliably reporting. Falls back to univariate detection if outdoor sensors are unavailable.

**Complexity:** High (feature engineering, covariate availability handling, detector selection)

---

### Room-to-Room Correlation (Multivariate Group)

**Why differentiating:** Adjacent rooms should have correlated temperatures. A single room reading anomalously cold while all others are normal is a more reliable anomaly signal than a univariate spike alone. Reduces false positives from individual sensor hardware issues vs genuine environmental events.

**What it is:** Group all per-room temperature sensors into a multivariate detector. Score each room's reading in the context of the other rooms. Use PyOD's multivariate models (IForest, COPOD) with room readings as feature dimensions.

**Dependency:** Requires multiple room sensors (satisfied by v1 scope). Model fitting requires sufficient historical co-occurrence of all rooms' data.

**Complexity:** High (missing-sensor handling, feature alignment, entity grouping config)

---

### Batch Backfill / Historical Analysis

**Why differentiating:** Being able to run anomaly detection over the last 30 days of InfluxDB history and surface anomalies in HA as a batch sensor result (not just live stream) enables the operator to identify problems that occurred while Argus was down or models were warming up.

**What it is:** gRPC `FitBatch` / `ScoreBatch` calls, triggered on schedule or manually. Results published to MQTT as batch-scored anomaly events, potentially with timestamp metadata.

**Dependency:** InfluxDB with sufficient retention (Q2 open question). Batch path implementation.

**Complexity:** Medium

---

### Adaptive Threshold (Contamination Auto-Tuning)

**Why differentiating:** The PyOD contamination parameter (expected % of anomalies) directly determines the decision threshold. Setting it wrong causes either too many or too few alerts. Auto-tuning via PyThresh or percentile calibration from a "clean" historical window makes the system self-calibrating rather than requiring manual threshold adjustment per sensor.

**What it is:** After initial fit on historical data, use PyThresh to derive contamination/threshold from the score distribution. Re-evaluate on periodic retrain cycles. Expose the current effective threshold as a sensor in HA for operator visibility.

**Dependency:** Requires batch path and sufficient history. Cannot run streaming-only.

**Complexity:** Medium

---

### Model Version Tracking + Rollback

**Why differentiating:** When a model retrain produces worse detection (e.g., due to dirty training data or concept drift mis-handling), the operator needs to revert. Storing model versions keyed by (entity_id, detector, version_timestamp) with the ability to pin to a previous version is the difference between a system an expert operator can trust and one they can't debug.

**What it is:** Model files on disk include version metadata. Config allows pinning to a specific version. gRPC returns model version in score responses for traceability.

**Complexity:** Medium

---

### HA Entity Friendly Names in Polish

**Why differentiating for this deployment:** PROJECT.md constraint D8 mandates Polish friendly-names. The differentiator is that Argus auto-generates human-readable Polish names (e.g., "Anomalia temperatury — Salon") from entity_id components + a Polish label mapping in config, rather than requiring the operator to manually rename each discovered entity.

**What it is:** `friendly_name` field in MQTT discovery payload derived from a room-label map in `entities.yaml`. Falls back to raw entity_id slug if no label defined.

**Complexity:** Low

---

### Drift Detection (Gradual Sensor Aging)

**Why differentiating:** Zigbee temperature sensors drift over months — a sensor may read consistently 0.5°C low after 18 months. This is a real but subtle anomaly. Detecting gradual positive/negative trend in the STL trend component over weeks signals sensor calibration drift before it becomes a data quality problem.

**What it is:** Long-window trend monitoring on STL trend component. Alert when `trend_slope(last 30 days) > drift_threshold_per_day`. Different from step change (gradual, not sudden).

**Dependency:** Requires STL decomposition (seasonal detection differentiator above) + significant history (30+ days minimum).

**Complexity:** Medium (sits on top of STL infrastructure)

---

## Anti-Features

Deliberately excluded or tightly constrained to prevent alert fatigue and system complexity creep.

---

### Raw Threshold Alerts Surfaced Directly to User

**Why avoid:** If Argus creates automations, sends notifications, or triggers actions directly, the operator loses control over alert policy. PROJECT.md explicitly scopes Argus to entity exposure only. Operators wire reactions in HA/Node-RED.

**What to do instead:** Expose clean binary_sensor + score entities. Let the operator build their own threshold-based automations. Argus's job ends at the MQTT payload.

---

### Unbounded Alert Volume (No Rate Limiting on Binary Sensor Transitions)

**Why avoid:** Research shows 63% of anomaly alerts are false positives in naive systems, and alert fatigue causes operators to ignore everything. A score sensor oscillating around threshold with no hysteresis will fire automations continuously. Industry data shows rule-based grouping can reduce alert volume 60–80% — the equivalent here is hysteresis + minimum anomaly duration.

**What to do instead:** Require hysteresis (already listed as table stakes) + minimum duration: binary_sensor does not transition to `on` unless the anomaly score exceeds threshold for at least `min_duration_seconds` (configurable, default 60s for temperature sensors). This eliminates sensor-noise-driven single-sample false positives.

---

### Per-Second Score Publishing

**Why avoid:** Publishing anomaly scores to MQTT on every `state_changed` event at full rate creates MQTT broker load, inflates HA state history database, and saturates the HA logbook with noise. Temperature sensors update every 30–60 seconds; sub-minute publishing frequency is unnecessary.

**What to do instead:** Publish score updates at sensor native update rate (pass-through). Never publish if score and binary state are unchanged from last publish (deduplication in orchestrator). Configurable minimum publish interval per entity.

---

### ML-Based Root Cause Attribution

**Why avoid:** Explaining why an anomaly occurred (e.g., "window left open vs sensor failure vs HVAC malfunction") requires either labelled training data (unavailable) or complex multi-hypothesis reasoning that will be wrong as often as right. The complexity-to-reliability ratio is unfavorable for v1.

**What to do instead:** Surface anomaly type label (spike / step-change / frozen / drift) from the detector that fired. Let the operator diagnose root cause from the entity state and HA history. Anomaly type labeling is cheap; attribution is expensive.

---

### Automatic Model Retraining Without Operator Approval

**Why avoid:** Automatic retraining on live data risks silently incorporating anomalous periods into the "normal" baseline. If the heating system fails for a week and the model retrains on that cold week, it will never flag similar events again. Concept drift adaptation must be deliberate.

**What to do instead:** Schedule retraining only on operator-triggered events or explicit time windows marked as "clean" in config. River Half-Space Trees handle short-term drift by design (online learning). Reserve periodic batch retraining for PyOD/Darts models on a manual or semi-automatic schedule with a confirmation gate.

---

### Custom HA Dashboard / Lovelace Cards

**Why avoid:** Out of scope per PROJECT.md. Auto-created entities appear in HA's automatic dashboard. Custom cards require frontend development unrelated to detection quality.

---

### Multi-Sensor Anomaly Correlation Across Domains (Motion + Temp + Presence)

**Why avoid:** Correlating temperature anomalies with motion/presence/door sensors adds significant complexity and requires a unified event model. v1 scope is environmental sensors only. Cross-domain correlation is a v2+ feature if validated.

---

## Dependency Map

```
Frozen/Stuck Detection
    └── No dependencies (rule-based, standalone)

Point Spike Detection
    └── No dependencies (rolling stats, cold-start capable)

Step Change Detection
    └── No dependencies (two-window comparison, cold-start capable in ~minutes)

Hysteresis / Anti-Flap
    └── Depends on: Point Spike or any scored detector outputting 0-1 score

Streaming Path
    └── Depends on: At least one of (Spike, Step Change, Half-Space Trees)

MQTT Discovery Entities
    └── Depends on: Streaming Path (live scores available)

Per-Entity Model Persistence
    └── Depends on: Any fitted model (PyOD batch / River HST)

Graceful Degradation
    └── Depends on: MQTT Discovery Entities (availability_topic must be declared)

Config-Driven Entity Registration
    └── No dependencies (config layer above detection)

--- DIFFERENTIATORS ---

Seasonal / STL Decomposition
    └── Depends on: Batch Path + InfluxDB history (min 7 days)
    └── Enables: Drift Detection

Outdoor Covariate Conditioning
    └── Depends on: Outdoor sensors reliable + Multivariate detector (PyOD)
    └── Depends on: Batch Path (for fitting)

Room-to-Room Multivariate Group
    └── Depends on: Multiple room sensors (v1 scope satisfies this)
    └── Depends on: Batch Path (for fitting multivariate model)

Batch Backfill
    └── Depends on: InfluxDB with retention (Q2 open question)
    └── Enables: STL Decomposition, Adaptive Threshold, Covariate Conditioning

Adaptive Threshold (Contamination Auto-Tuning)
    └── Depends on: Batch Path + sufficient history (min 30 days)
    └── Depends on: PyThresh library (or manual percentile logic)

Drift Detection (Gradual Sensor Aging)
    └── Depends on: STL Decomposition (trend component)
    └── Depends on: 30+ days history

Model Version Tracking + Rollback
    └── Depends on: Per-Entity Model Persistence (extends it)

Polish Friendly Names
    └── Depends on: MQTT Discovery (friendly_name field in payload)
```

---

## MVP Boundary

**Phase 1 (streaming correctness):**
- Point Spike (MAD/RobustZScore)
- Frozen/Stuck Detection
- Streaming Path < 2s
- MQTT Discovery (binary_sensor + score)
- Hysteresis/Anti-Flap
- Graceful Degradation to unavailable
- Config-Driven Entity Registration
- Polish Friendly Names

**Phase 2 (batch + model quality):**
- Step Change Detection
- Batch Backfill path
- Per-Entity Model Persistence
- River Half-Space Trees (streaming ML model)
- STL Seasonal Decomposition (requires history from Phase 2)

**Phase 3 (advanced, GPU-backed):**
- Outdoor Covariate Conditioning
- Room-to-Room Multivariate Group
- Adaptive Threshold Auto-Tuning
- Drift Detection
- Model Version Tracking + Rollback

**Never build (anti-features above):** raw threshold notifications, per-second publishing, automatic retraining without approval, root cause attribution, cross-domain correlation.

---

## Sources

- PyOD documentation: https://pyod.readthedocs.io/
- River Half-Space Trees (streaming): https://github.com/Aditya-go1/Real-Time-Anomaly-Detection-in-Data-Streams
- STL decomposition for anomaly detection: https://www.researchgate.net/figure/Results-of-anomaly-detection-methods-a-STL-decomposition-method-b-isolation-forest_fig7_376720514
- Temperature building anomaly detection (school study): https://pmc.ncbi.nlm.nih.gov/articles/PMC10739742/
- HIPER-CHAD indoor environmental multivariate detection: https://www.ncbi.nlm.nih.gov/pmc/articles/PMC12788105/
- HA Threshold integration hysteresis: https://www.home-assistant.io/integrations/threshold/
- HA MQTT binary sensor retain / availability: https://newerest.space/mastering-custom-mqtt-device-integration-home-assistant/
- Alert fatigue anti-patterns: https://arxiv.org/pdf/2204.09670
- Concept drift smart environment detection: https://ieeexplore.ieee.org/document/10076623/
- Frozen sensor detection (HA community): https://community.home-assistant.io/t/detect-frozen-values-from-tasmota-units/92353
- HA entity naming standards: https://developers.home-assistant.io/blog/2022/07/10/entity_naming/
- Time series anomaly taxonomy: https://link.springer.com/article/10.1007/s11280-023-01181-z
