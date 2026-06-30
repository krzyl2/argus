<!--
  GSD PROJECT.md — persistent project definition.
  Drop this at .planning/PROJECT.md, then run /gsd-plan-phase 1 (Claude Code uses the
  hyphen form /gsd-...; the colon form /gsd:... is Gemini CLI only).
  Alternatively, feed it as your answers to /gsd-new-project's questioning phase.
  This file maps onto GSD's PROJECT.md sections: Vision/Goals, Users, Success Criteria,
  Constraints & Locked Decisions, Technical Context, Requirements — plus a Roadmap of phases.
  All identifiers/spec in English by design; Home Assistant entity labels stay Polish.
-->

# Argus — Home Assistant Anomaly Detection

> Codename "Argus" (the hundred-eyed watchman). Rename freely.
> A self-hosted, extensible anomaly-detection system for Home Assistant sensor data.
> Hybrid architecture: **.NET orchestration** + **Python detection service**, running on **two separate machines**.

---

## 1. Vision & Goals

Build a universal, **extensible** anomaly-detection system that watches Home Assistant sensors and surfaces anomalies back into HA as ordinary entities. The system must grow over time: adding a new monitored sensor is a config edit, and adding a new *kind* of detector is a single new class behind a stable interface — never a rewrite.

**Explicit non-goal: do not reinvent detectors.** All detection logic reuses mature, permissively-licensed libraries (PyOD, River, Darts). We build only the orchestration, the HA/InfluxDB/MQTT plumbing, the detector strategy registry, the per-entity model lifecycle, and the deployment.

**Primary outcomes**
- Anomalies on the v1 environmental sensors appear in HA as auto-created `binary_sensor` (anomaly flag) + `sensor` (anomaly score), with no manual entity creation and no HA restart.
- Two detection paths run side by side: a **streaming** path (fast, drift-adaptive) and a **scheduled batch** path (seasonality- and context-aware).
- The system is calibratable per entity so it does not produce alert fatigue.
- The whole thing is self-hosted; nothing leaves the home network.

---

## 2. Users / Audience

Single operator: the homeowner (a .NET developer who drives implementation through Claude Code + GSD). Not a multi-tenant or commercial product. Optimize for one expert user who values dense, fact-oriented specs and iterative, correction-driven work.

---

## 3. Success Criteria (measurable, verifiable)

The project is successful when all of the following hold on the v1 entity set:

1. **End-to-end visibility** — each monitored entity has a live `binary_sensor.*_anomaly` and `sensor.*_anomaly_score` in HA, created via MQTT discovery and grouped under one HA device per source entity.
2. **Streaming latency** — from a Home Assistant `state_changed` event to the corresponding MQTT verdict update is < 2 s under normal load.
3. **Detection of injected anomalies** — synthetic fault injection (step change, spike, frozen/stuck sensor, drift) on a replayed history window is correctly flagged by at least one configured detector, with documented precision/recall on that test set.
4. **Restart resilience** — orchestrator, detector service, MQTT broker, and HA can each restart independently without losing per-entity model state and without duplicating or orphaning HA entities (MQTT discovery is idempotent via stable `unique_id`).
5. **Extensibility — entities** — adding a new monitored entity requires only an edit to `entities.yaml`; no code change, no redeploy of binaries (config hot-reload or a restart of the orchestrator only).
6. **Extensibility — detectors** — adding a new detector type requires implementing the `IDetector` contract and registering it; the orchestrator and gRPC contract need no change.
7. **Graceful degradation** — if the GPU/detector host is unreachable, the orchestrator stays up, marks affected anomaly sensors `unavailable` (not falsely `off`), and recovers automatically when the detector returns.
8. **License hygiene** — no GPL dependencies; all detection libraries permissive (BSD/Apache/MIT).

---

## 4. Constraints & Locked Decisions

These were decided in the discuss phase and are **locked** unless explicitly revisited.

| # | Decision | Value | Rationale / Notes |
|---|----------|-------|-------------------|
| D1 | Methodology | **GSD (Get Shit Done)** spec-driven, phased | This file is the PROJECT.md; phases below drive `/gsd-plan-phase`. |
| D2 | Architecture | **Hybrid**: .NET orchestration + Python detection service | .NET owns all I/O, scheduling, config, model lifecycle commands, HA/Influx/MQTT. **All ML detection lives in Python.** |
| D3 | Topology | **Two separate machines** | Orchestrator runs on/near the HA host ("edge"); the Python detector runs on the **GPU host**. They communicate over the network. |
| D4 | Edge↔Detector transport | **gRPC** (recommended) over the LAN, with mTLS | Strongly typed, supports unary (batch) + bidirectional streaming (live points), excellent .NET↔Python interop. MQTT is the documented fallback if gRPC proves awkward. |
| D5 | Data ingress | **HA WebSocket API** (`state_changed`) for streaming + **InfluxDB** for history/backfill | Confirmed: target entities are already written to InfluxDB. **Do not read the recorder DB directly.** |
| D6 | Data egress | **MQTT discovery** (`homeassistant/` prefix) | Idempotent, survives restarts, no HA restart needed. Reuses existing MQTT + Zigbee2MQTT broker. `POST /api/states` is explicitly rejected (non-persistent). |
| D7 | Model persistence | **Per-entity models on disk**, on the GPU host | joblib/pickle for PyOD/sklearn-style; native save for River/Darts. Keyed by `entity_id` + detector name + version. |
| D8 | Languages | **Spec, code, identifiers in English**; HA entity friendly-names/labels in **Polish** | Better for Claude Code and code identifiers; HA UI stays Polish for the operator. |
| D9 | Hosting | **Self-hosted only**, no cloud services | Privacy + control. GPU is local, on the detector host. |
| D10 | Detector reuse | **PyOD + River + Darts** as the detection engines | No hand-rolled detectors beyond trivial statistical baselines. |

**Library license posture:** PyOD (BSD-2), River (BSD-3), Darts (Apache-2.0). All permissive. Avoid ADTK (MPL-2.0 file-level copyleft) unless isolated; avoid any GPL.

---

## 5. Technical Context & Architecture

### 5.1 Hosts

- **Edge host** (HA host or adjacent): Home Assistant, MQTT broker, **Argus.Orchestrator** (.NET 8 worker service). InfluxDB is reachable on the LAN (its physical location is not critical; see open question Q2).
- **GPU host** (separate machine): **argus-detector** (Python gRPC server), CUDA/PyTorch runtime, on-disk model store.

### 5.2 Components

**Argus.Orchestrator (.NET 8, `Microsoft.Extensions.Hosting` BackgroundService)**
- HA WebSocket client: authenticate, `subscribe_events` with `event_type: state_changed`, filter to configured entities, with reconnect + exponential backoff.
- InfluxDB reader: pull rolling history windows per entity for backfill, training, and batch scoring.
- Detection client: gRPC client to the detector service — unary `Fit`/`ScoreBatch`, bidirectional `ScoreStream`, plus `SaveModel`/`LoadModel`/`ListDetectors`/`Health`.
- MQTT publisher: MQTT discovery config + state publishing; idempotent via `unique_id`; one HA `device` per source entity grouping its anomaly + score sensors.
- Scheduler: drives the live streaming path continuously and the batch path on an interval (e.g., every 5–15 min) plus nightly retraining.
- Config loader: `entities.yaml` (entity → detectors → params; multivariate groups) + connection settings.
- Suggested NuGet: `Grpc.Net.Client`, `MQTTnet`, `InfluxDB.Client`, plus a HA WebSocket client (custom over `System.Net.WebSockets` or a community lib).

**argus-detector (Python gRPC server)**
- `IDetector` strategy interface mirroring the scikit-learn / PyOD contract: `fit(window)`, `score(point|window) -> float`, `predict(...) -> 0/1`, `save(entity_id)`, `load(entity_id)`.
- Detector registry: name → class, so new detectors are drop-in.
- Detector implementations wrap **PyOD**, **River**, **Darts**.
- Per-entity model store on local disk.
- Suggested deps: `grpcio`, `grpcio-tools`, `pyod`, `river`, `u8darts` (Darts), `numpy`, `pandas`, `pydantic` (config), `joblib`, `torch` (GPU, for Phase 3).

### 5.3 Data flow

1. **Streaming detection**: HA → (WebSocket `state_changed`) → Orchestrator normalizes → gRPC `ScoreStream` → Detector (River/streaming) → `Verdict` back → Orchestrator → MQTT → HA.
2. **Batch / training**: Orchestrator → InfluxDB query (rolling window) → gRPC `Fit` / `ScoreBatch` → Detector (PyOD/Darts) → verdicts → MQTT → HA.
3. **Model lifecycle**: Orchestrator issues `Fit` (on schedule or on demand) → Detector trains and `Save`s per-entity model on the GPU host; `Load` on startup.

### 5.4 gRPC contract (conceptual — finalized in Phase 0)

```
service Detector {
  rpc ListDetectors (Empty)            returns (DetectorList);
  rpc Fit           (FitRequest)       returns (FitResult);
  rpc ScoreBatch    (Window)           returns (VerdictList);
  rpc ScoreStream   (stream Point)     returns (stream Verdict);
  rpc SaveModel     (ModelRef)         returns (Ack);
  rpc LoadModel     (ModelRef)         returns (Ack);
  rpc Health        (Empty)            returns (HealthStatus);
}

message Point   { string entity_id = 1; int64 ts = 2; double value = 3; map<string,double> covariates = 4; }
message Window  { string entity_id = 1; repeated Point points = 2; }
message Verdict { string entity_id = 1; int64 ts = 2; double score = 3; bool is_anomaly = 4;
                  double expected = 5; double lower = 6; double upper = 7; string detector = 8; }
message FitRequest { string entity_id = 1; string detector = 2; map<string,string> params = 3; Window window = 4; }
```

### 5.5 Repository layout (mono-repo)

```
argus/
├── proto/                       # shared gRPC contract (detector.proto)
├── orchestrator/                # .NET 8
│   ├── Argus.Orchestrator/
│   ├── Argus.Orchestrator.Tests/
│   └── Argus.Orchestrator.sln
├── detector/                    # Python gRPC service
│   ├── argus_detector/
│   │   ├── detectors/           # IDetector implementations (one file per detector)
│   │   ├── registry.py
│   │   ├── store.py             # per-entity model persistence
│   │   └── server.py
│   ├── tests/
│   └── pyproject.toml
├── deploy/
│   ├── docker-compose.edge.yml  # orchestrator (+ optional CPU-only detector replica)
│   ├── docker-compose.gpu.yml   # detector on the GPU host
│   └── config/entities.yaml
├── .planning/                   # GSD artifacts (PROJECT.md, ROADMAP.md, phases…)
└── README.md
```

### 5.6 Example `entities.yaml`

```yaml
# TODO: replace placeholder entity_ids with the exact HA entity_ids (see Q1).
entities:
  - id: sensor.temperatura_zewnetrzna
    detectors: [robust_zscore, hst]
  - id: sensor.wilgotnosc_zewnetrzna
    detectors: [robust_zscore, hst]
  - id: sensor.cisnienie_atmosferyczne
    detectors: [robust_zscore, stl]

  # Per-room sensors (one block per room). Weather entities used as covariates in Phase 2.
  - id: sensor.salon_temperatura
    detectors: [stl, hst]
    covariates: [sensor.temperatura_zewnetrzna, sensor.cisnienie_atmosferyczne]
  - id: sensor.salon_wilgotnosc
    detectors: [stl]
    covariates: [sensor.wilgotnosc_zewnetrzna]

# Multivariate groups (Phase 2+)
groups:
  - id: weather_vector
    members: [sensor.temperatura_zewnetrzna, sensor.wilgotnosc_zewnetrzna, sensor.cisnienie_atmosferyczne]
    detectors: [ecod]
  - id: salon_climate
    members: [sensor.salon_temperatura, sensor.salon_wilgotnosc]
    detectors: [iforest]
```

---

## 6. Scope (v1)

**In scope — v1 monitored entities:**
- Outdoor temperature
- Outdoor humidity
- Atmospheric pressure
- Per-room temperature (one per room)
- Per-room humidity (one per room)

**In scope — capabilities:** streaming + batch detection, MQTT-discovery output, per-entity model lifecycle, config-driven entities, calibratable thresholds with hysteresis, two-host deployment.

**Out of scope (v1):**
- Image/camera anomaly detection (Anomalib) — only if camera data is added later.
- Acting on anomalies (automations, notifications, Telegram, TTS): Argus only *exposes* anomaly entities. The operator wires reactions in Node-RED/HA automations.
- Custom dashboards beyond the auto-created entities.
- Multi-user / remote-access concerns.
- ML.NET-based detection in .NET (per D2, all detection is Python).

---

## 7. Roadmap (GSD phases)

Each phase is an independently shippable increment with explicit must-haves the verifier checks.

### Phase 0 — Foundations, contracts & two-host skeleton
Stand up the skeleton so everything after it is "fill in detectors."
- [ ] Mono-repo layout created; .NET solution + Python package scaffolded.
- [ ] `detector.proto` finalized; codegen wired for both .NET and Python.
- [ ] `entities.yaml` schema defined and parsed by the orchestrator.
- [ ] Docker images for orchestrator and detector; `docker-compose.edge.yml` and `docker-compose.gpu.yml`; documented deploy to two hosts.
- [ ] Structured logging, health checks (`Health` RPC), secrets/config handling, gRPC mTLS between hosts.
- [ ] **Must-have (E2E echo):** orchestrator sends a dummy `Point`, detector returns a dummy `Verdict`, orchestrator publishes a test sensor via MQTT discovery that is visible in HA.

### Phase 1 — CPU streaming + statistical detection (the core value)
Real detection on the v1 entities, CPU only.
- [ ] HA WebSocket client: auth, `state_changed` subscription, entity filter, reconnect/backoff.
- [ ] InfluxDB reader: per-entity rolling history window for backfill + batch.
- [ ] `IDetector` interface + registry in the detector service.
- [ ] Detectors: **RobustZScore/MAD**, **River Half-Space Trees** (streaming), **STL seasonal-residual**.
- [ ] Per-entity model store (`Save`/`Load`) on the GPU host.
- [ ] MQTT-discovery publisher: `binary_sensor` + score `sensor` + one `device` per entity; idempotent `unique_id`.
- [ ] Scheduler: continuous streaming path + interval batch path.
- [ ] Per-entity calibration/thresholds with **hysteresis** (anti-flapping, mirroring the CWU control philosophy).
- [ ] v1 entities wired from `entities.yaml`.
- [ ] **Must-have:** injected synthetic anomalies (step, spike, stuck, drift) are correctly flagged in HA on a replayed window; adding an entity is config-only; full restart resilience demonstrated.

### Phase 2 — Seasonality, multivariate & weather covariates (CPU)
Reduce false positives by giving detectors context.
- [ ] Darts integration: a forecasting/filtering anomaly model + `PyODScorer`.
- [ ] **Covariates**: hour-of-day, day-of-week, and the weather entities (outdoor temp/humidity/pressure) conditioning the per-room sensors.
- [ ] PyOD multivariate group detectors (**ECOD**, **Isolation Forest**) over the `groups` (e.g., `weather_vector`, `salon_climate`).
- [ ] Evaluation harness: precision/recall on labeled/synthetic anomalies; per-entity threshold tuning.
- [ ] Nightly retraining job.
- [ ] **Must-have:** weather-conditioned room anomaly detection measurably lowers false positives vs the Phase 1 baseline on a fixed test window; a multivariate group flags a correlated anomaly that single-sensor detection missed.

### Phase 3 — GPU deep learning (targeted, optional)
Only the anomaly classes the CPU methods miss.
- [ ] GPU detector(s): PyOD AutoEncoder/VAE (or Darts neural) over the combined multi-room + weather vector.
- [ ] CUDA runtime verified on the GPU host; trained weights in the model store; optional ONNX export.
- [ ] Resource guard: GPU detectors run only on the GPU host; CPU detectors unaffected; graceful fallback if GPU unavailable.
- [ ] **Must-have:** at least one GPU detector trained and serving verdicts for a multivariate signal, covering an anomaly class the CPU detectors miss, with graceful degradation proven when the GPU is taken offline.

---

## 8. Cross-cutting / non-functional requirements

- **Resilience:** every external dependency (HA, InfluxDB, MQTT, detector) can drop and recover; affected anomaly sensors go `unavailable`, never silently `off`.
- **Idempotency:** MQTT discovery uses stable `unique_id`; re-publishing config never duplicates entities.
- **Observability:** structured logs both sides; basic metrics (events/s, verdict latency, detector errors).
- **Security:** mTLS on the gRPC link; secrets out of source control.
- **Versioning:** models versioned by detector name + schema; safe to retrain without breaking serving.
- **Testing:** unit tests per detector; an integration test that replays a history window and asserts verdicts; the E2E echo from Phase 0 kept green.
- **Deployment:** reproducible via the two compose files; documented host responsibilities.

---

## 9. Open questions / risks (resolve during planning)

- **Q1 — exact entity_ids.** The v1 entities are described functionally; the precise HA `entity_id`s for outdoor temp/humidity/pressure and each room's temp/humidity must be filled into `entities.yaml`. *(Owner: operator, before Phase 1.)*
- **Q2 — InfluxDB location & retention.** Where InfluxDB physically runs and how far back its retention reaches (this bounds backfill/training depth). *(Before Phase 1.)*
- **Q3 — transport confirmation.** gRPC (D4) is recommended; confirm vs MQTT for the edge↔detector link before Phase 0 locks the contract.
- **R1 — GPU host uptime.** All detection lives on the GPU host (D2/D3). If it is down, there is no detection. Mitigation/decision: optionally deploy a second **CPU-only** detector replica on the edge host for resilience of the lightweight detectors (Phase 1 detectors are CPU-only and could run there). Flag for decision in Phase 0.
- **R2 — alert fatigue.** Anomaly detection is unsupervised and noisy; many "anomalies" in home data are expected (a humidity spike during a shower). Per-entity calibration, hysteresis, and covariates (Phase 2) are the primary mitigations; budget tuning time.
- **R3 — clock/skew & out-of-order points.** WebSocket and InfluxDB timestamps must be reconciled; define handling for late/out-of-order points in the streaming path.

---

## 10. How to drive this with GSD

1. Save this file as `.planning/PROJECT.md` in the `argus/` repo.
2. (Optional) run `/gsd-new-project` and use sections 1–6 as your answers, or proceed directly.
3. Run `/gsd-plan-phase 1` (start at Phase 0 if you want the skeleton planned explicitly) → review the generated plan → `/gsd-execute-phase` → `/gsd-verify-work`.
4. Advance phase by phase. Each phase's **Must-have** is the verifier's contract.

*(Claude Code uses the hyphen command form `/gsd-...`; the colon form `/gsd:...` is Gemini CLI only.)*
