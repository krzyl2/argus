# Phase 5: Group Detection Core (Proto + Python Detectors) - Context

**Gathered:** 2026-07-02
**Status:** Ready for planning

<domain>
## Phase Boundary

Deliver peer-divergence and joint-multivariate group detection at the **Python/proto layer only** — correct, independently-verifiable scores before any orchestrator or UI code depends on them. In scope: proto contract extension for a real 2D value matrix, two new detector families (robust peer-divergence + PyOD joint-multivariate), per-feature scaling, group-model Fit/Save/Load lifecycle keyed without colliding with per-entity models. Out of scope: InfluxDB time-alignment (Phase 6), MQTT publish/retract (Phase 6), orchestrator wiring (Phase 6), algorithm chooser UI (Phase 8). Inputs are assumed pre-aligned value matrices — alignment is Phase 6's job (GRP-02).

Covers requirements: GRP-03, GRP-04, GRP-05, GRP-06, GRP-07 (GRP-09 attribution groundwork included in the proto/detector output but surfaced in Phase 8).
</domain>

<decisions>
## Implementation Decisions

### Proto Contract (multi-series)
- 2D matrix carried as `repeated Series { string member_id; repeated double values; }` with a shared aligned timestamp axis; time-alignment performed .NET-side (GRP-02, Phase 6). Not a loop of univariate calls (success criterion 3).
- New RPCs `ScoreGroupBatch` + `FitGroup` added alongside existing univariate RPCs — no reuse/overload of `ScoreBatch`, clean seam, no collision.
- Response is a union covering both modes: peer-divergence → per-member score + flag; joint-multivariate → single group-level score + flag plus ranked per-feature contributions.
- Attribution field (`repeated FeatureContribution { member_id; double contribution; }`) added to the response contract **now** in Phase 5 (populated by detectors); UI consumes it in Phase 8 (GRP-09 groundwork).

### Peer-Divergence (GRP-03/04)
- Consensus statistic: modified z-score `0.6745·(x − median) / MAD` computed per timestamp across members (Iglewicz-Hoaglin robust statistic).
- Flag threshold: `|modified z| > 3.5` as the standard default (maps to preset "Med" later in Phase 8).
- Minimum-member-count floor = 3; below floor → no verdict emitted (score NaN / unavailable, never a false `off`) (GRP-04).
- MAD = 0 (identical member values) → epsilon guard / meanAD fallback, no divide-by-zero; all members treated as normal.

### Joint-Multivariate (GRP-05/06)
- Default detector: **ECOD** — parameter-free, deterministic, per-feature tail probabilities give attribution "for free" (GRP-09).
- Full detector library shipped in Phase 5 behind a common interface: PCA / ECOD / COPOD / IForest. Phase 8 chooser only exposes them.
- Feature scaling: **RobustScaler** (median / IQR) — consistent with the MAD peer statistic, outlier-resistant; scaler persisted in the model bundle so mixed units (hPa vs %RH) do not dominate (GRP-06).
- Attribution: per-feature reconstruction error / tail probability, ranked (GRP-09 groundwork).

### Model Lifecycle (GRP-07)
- Model key: `group_{group_id}__{detector}__v{version}` — `group_` namespace prefix guarantees no collision with per-entity keys (success criterion 4).
- Serialization: **joblib**, scaler + detector persisted together in one bundle (dict) — consistent with existing PyOD persistence.
- Peer-divergence is **stateless** (statistic computed per-batch); its Fit/Save is a no-op or persists only threshold config, no fitted model object.
- Version scheme reuses the existing per-entity monotonic integer version scheme.
</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- `proto/argus.proto` (`package argus.v1`, `csharp_namespace = "Argus.Detector.V1"`) — existing `Point`/`Verdict`/`FitRequest`/`ScoreBatchRequest`/`Save|LoadModelRequest` messages and `DetectorService` RPCs. Add group messages + `ScoreGroupBatch`/`FitGroup` here; regen via `detector/scripts/gen_proto.py`.
- `detector/argus_detector/pyod_detector.py` — existing PyOD wrapper (MAD); model for the new joint-multivariate detector wrappers.
- `detector/argus_detector/model_store.py` — `ModelStore` with versioned layout `models/{slug}/{detector}/v{N}/model.joblib` + `latest` pointer + `version.json` sidecar, atomic latest write, 3-version retention, `load_all_into(registry)`. Extend for `group_` keys (bundle dict of scaler+detector).
- `detector/argus_detector/registry.py` — `DetectorRegistry` keyed by `(entity_id, detector)` with per-key locks, train-outside-lock + atomic swap (MDL-04), `_create_detector` factory. Add group registry path keyed by `(group_id, detector)` or extend the factory.
- `detector/argus_detector/servicer.py` — gRPC servicer implementing `DetectorService`; add `ScoreGroupBatch`/`FitGroup` handlers.
- Existing test suite pattern: `detector/tests/test_pyod_detector.py`, `test_model_store.py`, `test_registry.py`, `test_servicer.py`, `test_proto_codegen.py` — mirror for group detectors (independent verifiability is a success criterion).

### Established Patterns
- Detectors use `DoubleValue` wrappers in proto to distinguish null from 0.0 (see `Point.value`, `Verdict.score`).
- `_create_detector` factory maps detector-name string → instance; lazy imports of PyOD/River inside branches.
- Model persistence separates PyOD (joblib) vs River (pickle); group bundle uses joblib (no River involvement).
- RobustZScore does NOT exist in PyOD 3.6.0 — both "mad"/"robust_zscore" map to `PyODDetector(MAD)` (historical pitfall; peer-divergence here is a fresh robust implementation, not PyOD).

### Integration Points
- `proto/argus.proto` service block — new RPCs; both Python (`argus_pb2`) and .NET (`Argus.Detector.V1`) codegen consume it. Phase 6 orchestrator calls these RPCs.
- `ModelStore` glob `*/*/latest` in `load_all_into` — group models must slot into this scan (or a parallel `group_*` scan) without breaking per-entity load.
- `DetectorRegistry.register` — used by `load_all_into` to inject fitted models; group models need an analogous injection path.
</code_context>

<specifics>
## Specific Ideas

- Independent verifiability (success criterion, GRP requirement theme): every group detector must be unit-testable in isolation with hand-constructed pre-aligned matrices — a known jointly-abnormal vector that no single feature would trigger MUST be caught (proves the 2D matrix is real, not a univariate loop).
- Mixed-units test fixture (hPa + %RH) is a required test to prove scaling prevents one feature dominating (GRP-06 success criterion).
- Below-floor group (< 3 members) must produce no verdict, verified by test (GRP-04).
</specifics>

<deferred>
## Deferred Ideas

- InfluxDB time-alignment (`aggregateWindow` + `pivot`, staleness cap) — Phase 6 (GRP-02).
- MQTT publish/retract of group entities — Phase 6 (GRP-08).
- Algorithm chooser UI, sensitivity presets (Low/Med/High), Advanced toggle — Phase 8 (ALGO-01..04); Phase 5 only fixes the "Med"-equivalent defaults.
- Surfacing per-feature attribution in the UI — Phase 8 (GRP-09); Phase 5 only emits it on the wire.
- Streaming group detection — out of scope this milestone (STRM-01/02).
</deferred>
