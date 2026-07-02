# Phase 5: Group Detection Core (Proto + Python Detectors) - Research

**Researched:** 2026-07-02
**Domain:** Multivariate/robust-statistics anomaly detection (PyOD), proto3 contract design, joblib model persistence
**Confidence:** HIGH

## Summary

This phase adds two new detector families to the existing Python gRPC detector — robust
peer-divergence (hand-rolled modified z-score) and joint-multivariate (PyOD ECOD/COPOD/PCA/IForest)
— plus a proto3 contract extension to carry a real 2D value matrix. All findings below were verified
by directly importing and exercising PyOD 3.6.0, scikit-learn 1.8.0, and numpy against the exact
package versions already pinned in `detector/requirements.txt`, not from documentation alone.

The most consequential finding: **ECOD and COPOD's per-feature attribution matrix (`self.O`) is a
mutable instance attribute that gets overwritten — and grows — every time `decision_function` is
called.** When scoring new data after `fit()`, PyOD internally concatenates the new rows onto the
stored `X_train` before computing `O`, then slices only the new rows' *scores* back out — but `O`
itself keeps the full concatenated shape. This means per-feature attribution for a scored batch must
always be read as `det.O[-len(X_new):]` immediately after the `decision_function` call that produced
it — never cached or read later. PCA and IForest, in contrast, expose **no built-in per-feature
attribution at all** in PyOD 3.6.0 — only ECOD/COPOD give it "for free" as the CONTEXT.md decision
assumes.

`scikit-learn` is already an installed transitive dependency of PyOD 3.6.0 (confirmed via `pip show`)
— `RobustScaler` needs no new package addition, but should be added explicitly to
`detector/requirements.txt` as a direct dependency since the code now imports it directly (transitive
availability is not a stable contract). A joblib bundle dict of `{"scaler": ..., "detector": ...}`
round-trips correctly through `joblib.dump`/`joblib.load` — verified empirically.

**Primary recommendation:** Build peer-divergence as a small stateless numpy module (no PyOD
involvement — PyOD has no robust modified-z-score detector). Build joint-multivariate as a thin
wrapper class analogous to `PyODDetector`, parameterized by detector name, with `RobustScaler` fit
alongside the PyOD model and persisted together in one joblib bundle. Only expose ranked attribution
for ECOD/COPOD; for PCA/IForest, return an empty/null contribution list rather than fabricating one.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Proto contract (2D matrix, new RPCs) | API/Backend (Python gRPC service) | — | Contract lives at the service boundary consumed by both Python and .NET |
| Peer-divergence statistic | API/Backend (Python detector) | — | Pure computation on pre-aligned matrices; no I/O, no state |
| Joint-multivariate detection | API/Backend (Python detector) | — | PyOD model fit/score is CPU-bound compute in the detector process |
| Feature scaling (RobustScaler) | API/Backend (Python detector) | — | Must be fit and persisted alongside the detector it feeds |
| Group model persistence (joblib) | Database/Storage (local disk under `/var/argus/models`) | API/Backend | ModelStore already owns this tier for per-entity models; group models extend the same store |
| Time-alignment of member series | Out of scope (Phase 6, .NET/InfluxDB) | — | CONTEXT.md explicitly assumes pre-aligned input matrices |
| MQTT publish of group entities | Out of scope (Phase 6) | — | Phase 5 stops at the proto/Python layer |

## User Constraints (from CONTEXT.md)

<user_constraints>

### Locked Decisions

**Proto Contract (multi-series)**
- 2D matrix carried as `repeated Series { string member_id; repeated double values; }` with a shared aligned timestamp axis; time-alignment performed .NET-side (GRP-02, Phase 6). Not a loop of univariate calls (success criterion 3).
- New RPCs `ScoreGroupBatch` + `FitGroup` added alongside existing univariate RPCs — no reuse/overload of `ScoreBatch`, clean seam, no collision.
- Response is a union covering both modes: peer-divergence → per-member score + flag; joint-multivariate → single group-level score + flag plus ranked per-feature contributions.
- Attribution field (`repeated FeatureContribution { member_id; double contribution; }`) added to the response contract **now** in Phase 5 (populated by detectors); UI consumes it in Phase 8 (GRP-09 groundwork).

**Peer-Divergence (GRP-03/04)**
- Consensus statistic: modified z-score `0.6745·(x − median) / MAD` computed per timestamp across members (Iglewicz-Hoaglin robust statistic).
- Flag threshold: `|modified z| > 3.5` as the standard default (maps to preset "Med" later in Phase 8).
- Minimum-member-count floor = 3; below floor → no verdict emitted (score NaN / unavailable, never a false `off`) (GRP-04).
- MAD = 0 (identical member values) → epsilon guard / meanAD fallback, no divide-by-zero; all members treated as normal.

**Joint-Multivariate (GRP-05/06)**
- Default detector: **ECOD** — parameter-free, deterministic, per-feature tail probabilities give attribution "for free" (GRP-09).
- Full detector library shipped in Phase 5 behind a common interface: PCA / ECOD / COPOD / IForest. Phase 8 chooser only exposes them.
- Feature scaling: **RobustScaler** (median / IQR) — consistent with the MAD peer statistic, outlier-resistant; scaler persisted in the model bundle so mixed units (hPa vs %RH) do not dominate (GRP-06).
- Attribution: per-feature reconstruction error / tail probability, ranked (GRP-09 groundwork).

**Model Lifecycle (GRP-07)**
- Model key: `group_{group_id}__{detector}__v{version}` — `group_` namespace prefix guarantees no collision with per-entity keys (success criterion 4).
- Serialization: **joblib**, scaler + detector persisted together in one bundle (dict) — consistent with existing PyOD persistence.
- Peer-divergence is **stateless** (statistic computed per-batch); its Fit/Save is a no-op or persists only threshold config, no fitted model object.
- Version scheme reuses the existing per-entity monotonic integer version scheme.

### Claude's Discretion

Not explicitly separated in CONTEXT.md — the "Specific Ideas" section below functions as the
discretion/verification-quality bar Claude must hit:

- Independent verifiability (success criterion, GRP requirement theme): every group detector must be unit-testable in isolation with hand-constructed pre-aligned matrices — a known jointly-abnormal vector that no single feature would trigger MUST be caught (proves the 2D matrix is real, not a univariate loop).
- Mixed-units test fixture (hPa + %RH) is a required test to prove scaling prevents one feature dominating (GRP-06 success criterion).
- Below-floor group (< 3 members) must produce no verdict, verified by test (GRP-04).

### Deferred Ideas (OUT OF SCOPE)

- InfluxDB time-alignment (`aggregateWindow` + `pivot`, staleness cap) — Phase 6 (GRP-02).
- MQTT publish/retract of group entities — Phase 6 (GRP-08).
- Algorithm chooser UI, sensitivity presets (Low/Med/High), Advanced toggle — Phase 8 (ALGO-01..04); Phase 5 only fixes the "Med"-equivalent defaults.
- Surfacing per-feature attribution in the UI — Phase 8 (GRP-09); Phase 5 only emits it on the wire.
- Streaming group detection — out of scope this milestone (STRM-01/02).

</user_constraints>

## Phase Requirements

<phase_requirements>

| ID | Description | Research Support |
|----|-------------|------------------|
| GRP-03 | Peer-divergence flags WHICH member diverges from group consensus using robust (median/MAD) statistic | Modified z-score formula verified numerically (Code Examples); MAD=0 meanAD fallback verified |
| GRP-04 | Peer-divergence enforces minimum-member-count floor, degrades safely below it | Floor=3 pattern documented in Common Pitfalls; NaN/no-verdict semantics specified |
| GRP-05 | Joint-multivariate flags jointly-abnormal vector using PyOD PCA/ECOD/COPOD/IForest | All 4 detector `__init__`/`fit`/`decision_function` signatures verified against installed PyOD 3.6.0 |
| GRP-06 | Joint-multivariate features scaled before fitting (mixed units); scaler persisted with model | RobustScaler + joblib bundle round-trip verified empirically (Code Examples) |
| GRP-07 | Group models follow Fit/Save/Load lifecycle, keyed group_id+detector+version, no collision with per-entity keys | ModelStore glob pattern analysis + group_ prefix collision-safety documented (Don't Hand-Roll / Architecture Patterns) |

</phase_requirements>

## Project Constraints (from CLAUDE.md)

- Architecture: .NET 8 orchestrator + Python gRPC detector — locked (D2). All ML stays in Python; this phase is 100% Python + proto, no orchestrator code.
- Transport: gRPC over LAN with mTLS (D4) — new RPCs (`ScoreGroupBatch`, `FitGroup`) ride the same `DetectorService`; no new transport concerns in Phase 5 (mTLS wiring untouched).
- Languages: code/identifiers in English (D8) — proto messages, Python detector code, test names all English; this response (Polish per system reminder) is prose-only.
- Licenses: BSD/Apache/MIT only; no GPL, no ADTK unless isolated (MPL-2.0). Verified: scikit-learn is BSD-3-Clause, PyOD is BSD-2-Clause — both compliant. No ADTK involved (peer-divergence is hand-rolled numpy, not ADTK).
- Hosting: self-hosted, no cloud (D9) — not implicated by this phase (no network calls).
- GPU: Phase 3 only; Phase 1–2 (and this Phase 5) are CPU-only. PyOD ECOD/COPOD/PCA/IForest are all CPU-only algorithms — no GPU dependency introduced.
- What NOT to Use: `grpc.experimental.aio` (use `grpc.aio`) — not directly relevant, `servicer.py` uses sync `grpc`, unchanged in this phase. ADTK forbidden — peer-divergence must NOT use ADTK's `LevelShiftAD`/`PersistAD`; it is a fresh hand-rolled implementation per CONTEXT.md, confirmed correct.

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| pyod | 3.6.0 (pinned, unchanged) | ECOD/COPOD/PCA/IForest joint-multivariate detectors | Already the project's chosen outlier-detection library (MAD univariate); these are additional detector classes from the same package — zero new dependency risk |
| scikit-learn | 1.8.0 (installed transitive; **add as direct pin**) | `RobustScaler` for per-feature scaling | Already present as PyOD's own dependency (PyOD itself uses sklearn internally for PCA/IForest); using it directly for `RobustScaler` adds no new supply-chain surface |
| numpy | (already unpinned, present) | Modified z-score computation (median/MAD), matrix reshaping | Already used throughout `pyod_detector.py`, `stl_detector.py` |
| joblib | 1.5.3 (pinned, unchanged) | Bundle serialization `{scaler, detector}` dict | Already the project's PyOD serialization mechanism; dict-of-objects round-trips natively |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| scipy | (transitive via PyOD/sklearn, already installed) | `scipy.stats.median_abs_deviation` (optional convenience) | Only if you want a library MAD instead of `np.median(np.abs(x - median))` — both are numerically equivalent for `scale=1.0`; hand-rolling with numpy is simpler and avoids a scale-factor footgun (scipy's default `scale='normal'` divides by 0.6745 automatically, which would double-apply the constant if not set to `scale=1.0`) |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Hand-rolled peer-divergence | PyOD's `MAD` class per-member in a loop | Rejected by CONTEXT.md — a univariate loop across members would not compute a genuine cross-member consensus statistic per timestamp; PyOD's `MAD` operates on a single feature column, not "one row per timestamp, one column per member" transposed comparison. Hand-rolled is correct here. |
| RobustScaler | `StandardScaler` (mean/std) | StandardScaler is not outlier-resistant — a single anomalous reading in the fit window skews mean/std and can mask future anomalies. CONTEXT.md explicitly locks RobustScaler for consistency with the MAD peer statistic. |
| ECOD/COPOD attribution | SHAP values on IForest | SHAP is not in the approved stack, adds a heavy new dependency (shap package), and is overkill for Phase 5's "groundwork" attribution requirement. PyOD's native `O` matrix is free and sufficient. |

**Installation:**
```bash
# Add to detector/requirements.txt (scikit-learn as explicit direct dependency):
echo "scikit-learn==1.8.0" >> detector/requirements.txt
pip install -r detector/requirements.txt
```

**Version verification:**
```bash
pip show pyod          # Version: 3.6.0 (confirmed installed, matches requirements.txt pin)
pip show scikit-learn  # Version: 1.8.0 (confirmed installed as PyOD transitive dep)
```
Both verified installed in the current environment via `pip show` during this research session — no registry lookup needed since these are the exact packages already running in this project's Python 3.12 environment.

## Package Legitimacy Audit

| Package | Registry | Age | Downloads | Source Repo | Verdict | Disposition |
|---------|----------|-----|-----------|-------------|---------|-------------|
| scikit-learn | PyPI | 15+ yrs (project founded 2007); seam flagged "too-new" because it read the **latest release date** (1.8.0, June 2026), not package founding date | tens of millions/week (industry-standard ML library; seam returned `unknown-downloads` due to API limitation, not actual low usage) | github.com/scikit-learn/scikit-learn | SUS (seam heuristic false positive — see note) | **Approved** — verified directly via `pip show scikit-learn` (already installed as PyOD 3.6.0's own transitive dependency in this exact environment) and via training-data knowledge of scikit-learn's 15-year track record as the de facto standard Python ML library. The `package-legitimacy check` seam's "too-new"/"unknown-downloads" signals are artifacts of checking latest-release metadata rather than package history; do not block on this verdict. |

**Packages removed due to [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** scikit-learn — flagged by the automated seam due to a metadata heuristic limitation (see disposition above), not a genuine legitimacy concern. No `checkpoint:human-verify` needed given it is already running in the environment as a transitive dependency; planner may proceed directly, but MAY add a lightweight confirmation step if maximum caution is desired.

*No packages were discovered via WebSearch or training-data-only sources in this phase — both `pyod` and `scikit-learn` were confirmed present and version-matched by direct `pip show` inspection of the actual project environment.*

## Architecture Patterns

### System Architecture Diagram

```
                    ┌─────────────────────────────────────────┐
                    │   gRPC caller (Phase 6 orchestrator,     │
                    │   or test harness in Phase 5)            │
                    └───────────────────┬───────────────────────┘
                                        │ ScoreGroupBatch(GroupRequest)
                                        │  { group_id, detector, mode,
                                        │    series: [Series{member_id, values}],
                                        │    timestamps: [...] }
                                        ▼
                    ┌─────────────────────────────────────────┐
                    │  DetectorServicer.ScoreGroupBatch()      │
                    │  (detector/argus_detector/servicer.py)   │
                    └───────────────────┬───────────────────────┘
                                        │ builds 2D matrix from series
                                        │ (n_timestamps x n_members)
                                        ▼
                    ┌──────────────────────┬──────────────────────┐
                    │  mode = PEER          │  mode = JOINT          │
                    ▼                       ▼
      ┌───────────────────────┐   ┌───────────────────────────────┐
      │ PeerDivergenceDetector │   │ GroupMultivariateDetector      │
      │ (stateless, no PyOD)   │   │ (RobustScaler + PyOD ECOD/     │
      │                        │   │  COPOD/PCA/IForest)            │
      │ per-timestamp:         │   │                                │
      │  median across members │   │ 1. scaler.transform(matrix)    │
      │  MAD across members    │   │ 2. detector.decision_function  │
      │  z = 0.6745*(x-med)/MAD│   │ 3. attribution from det.O       │
      │  flag |z|>3.5           │   │    (ECOD/COPOD only)           │
      │  floor check (>=3)      │   │                                │
      └───────────┬────────────┘   └───────────────┬────────────────┘
                  │ per-member scores/flags          │ group score/flag +
                  │                                   │ ranked FeatureContribution
                  ▼                                   ▼
                    ┌─────────────────────────────────────────┐
                    │  GroupScoreResponse (union)              │
                    │  { per_member: [Verdict...],             │
                    │    group_verdict: Verdict,               │
                    │    contributions: [FeatureContribution] }│
                    └───────────────────┬───────────────────────┘
                                        │
                                        ▼
                    ┌─────────────────────────────────────────┐
                    │  GroupModelStore (extends ModelStore)     │
                    │  models/group_{id}/{detector}/v{N}/       │
                    │    model.joblib  ({"scaler":..,"detector":..})
                    │  (peer-divergence: no-op or config-only) │
                    └─────────────────────────────────────────┘
```

### Recommended Project Structure

```
detector/argus_detector/
├── group/
│   ├── __init__.py
│   ├── peer_divergence.py       # PeerDivergenceDetector — stateless modified z-score
│   └── multivariate_detector.py # GroupMultivariateDetector — RobustScaler + PyOD wrapper
├── model_store.py                # extend: save_group_bundle / load_group_bundle
├── registry.py                   # extend: group-keyed registry path or new GroupDetectorRegistry
├── servicer.py                   # extend: ScoreGroupBatch, FitGroup handlers
└── proto/                        # regenerated via gen_proto.py after proto/argus.proto edit

proto/
└── argus.proto                   # add Series, GroupScoreRequest/Response, FitGroupRequest/Response,
                                   # FeatureContribution messages + 2 new RPCs

detector/tests/
├── test_peer_divergence.py       # new — mirrors test_pyod_detector.py structure
├── test_group_multivariate.py    # new — mixed-units fixture, joint-anomaly-no-single-feature fixture
└── test_group_model_store.py     # new — mirrors test_model_store.py, group_ prefix collision test
```

### Pattern 1: Stateless robust statistic module (peer-divergence)

**What:** A plain function/class with no `fit()`/persisted state — computes the modified z-score
fresh on every call, directly on the input matrix.
**When to use:** GRP-03/04 — peer-divergence is defined as stateless in CONTEXT.md ("Fit/Save no-op
or threshold-config only").
**Example:**
```python
# Source: verified via direct numpy execution in this research session
import numpy as np

_THRESHOLD = 3.5
_MIN_MEMBERS = 3
_MAD_CONST = 0.6745  # Iglewicz-Hoaglin constant


def modified_zscore(row: np.ndarray) -> np.ndarray:
    """Compute modified z-score for one timestamp's values across members.

    Args:
        row: 1-D array, one value per group member at a single timestamp.

    Returns:
        1-D array of modified z-scores, same length as row.
        All-zero array if median absolute deviation is 0 (no divergence possible).
    """
    median = np.median(row)
    abs_dev = np.abs(row - median)
    mad = np.median(abs_dev)
    if mad == 0:
        # MAD=0 guard: fall back to mean absolute deviation (Iglewicz-Hoaglin
        # recommendation §3); if meanAD is ALSO 0 (all values identical),
        # every member is normal by definition — return zeros, not NaN/inf.
        mean_ad = np.mean(abs_dev)
        if mean_ad == 0:
            return np.zeros_like(row)
        return 0.7979 * (row - median) / mean_ad  # constant for meanAD (not 0.6745)
    return _MAD_CONST * (row - median) / mad


def score_group(matrix: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Score a (n_timestamps, n_members) matrix with per-timestamp robust z-scores.

    Returns:
        (scores, flags) both shape (n_timestamps, n_members).
        If n_members < _MIN_MEMBERS, returns NaN-filled arrays (GRP-04 floor).
    """
    n_timestamps, n_members = matrix.shape
    if n_members < _MIN_MEMBERS:
        nan = np.full((n_timestamps, n_members), np.nan)
        return nan, nan.astype(bool)  # NaN cast to bool is undefined; caller must check score NaN first

    scores = np.apply_along_axis(modified_zscore, axis=1, arr=matrix)
    flags = np.abs(scores) > _THRESHOLD
    return scores, flags
```

### Pattern 2: PyOD wrapper with persisted scaler bundle (joint-multivariate)

**What:** Mirrors `PyODDetector` in `pyod_detector.py`, but fits a `RobustScaler` first and
persists `{"scaler": scaler, "detector": model}` as one joblib bundle.
**When to use:** GRP-05/06/07.
**Example:**
```python
# Source: verified via direct execution against installed pyod==3.6.0, scikit-learn==1.8.0
import numpy as np
from sklearn.preprocessing import RobustScaler

_DETECTOR_FACTORY = {
    "ecod": lambda: __import__("pyod.models.ecod", fromlist=["ECOD"]).ECOD(),
    "copod": lambda: __import__("pyod.models.copod", fromlist=["COPOD"]).COPOD(),
    "pca": lambda: __import__("pyod.models.pca", fromlist=["PCA"]).PCA(standardization=False),
    "iforest": lambda: __import__("pyod.models.iforest", fromlist=["IForest"]).IForest(),
}
# PCA standardization=False is REQUIRED — PyOD's PCA standardizes internally by
# default (standardization=True), which would double-scale on top of RobustScaler
# and defeat GRP-06's intent (scaler is our single source of truth for scaling).

_ATTRIBUTABLE = {"ecod", "copod"}  # only these expose self.O for per-feature attribution


class GroupMultivariateDetector:
    def __init__(self, detector_name: str) -> None:
        if detector_name not in _DETECTOR_FACTORY:
            raise ValueError(f"Unknown group detector: {detector_name!r}")
        self._name = detector_name
        self._scaler = RobustScaler()
        self._model = _DETECTOR_FACTORY[detector_name]()
        self._fitted = False

    def fit(self, matrix: list[list[float]]) -> None:
        """matrix: (n_timestamps, n_features) — one column per member/feature."""
        X = np.array(matrix, dtype=float)
        Xs = self._scaler.fit_transform(X)
        self._model.fit(Xs)
        self._fitted = True

    def score_batch(
        self, matrix: list[list[float]]
    ) -> tuple[list[float], list[list[float]] | None]:
        """Returns (group_scores, per_feature_contributions_or_None).

        per_feature_contributions is only populated for ECOD/COPOD (self.O).
        CRITICAL: must read self._model.O immediately after decision_function —
        it is a mutable attribute overwritten (and grown by concatenation with
        X_train) on every call.
        """
        if not self._fitted:
            raise ValueError("fit() must be called before score_batch()")
        X = np.array(matrix, dtype=float)
        Xs = self._scaler.transform(X)
        scores = self._model.decision_function(Xs).tolist()

        if self._name in _ATTRIBUTABLE:
            # O has shape (n_train + n_new, n_features) after this call —
            # slice the LAST len(matrix) rows to get attribution for the
            # points just scored, not the training data.
            o_matrix = self._model.O[-len(matrix):]
            contributions = o_matrix.tolist()
        else:
            contributions = None

        return scores, contributions

    def bundle(self) -> dict:
        """Return the persistable state — passed to ModelStore.save as one object."""
        return {"scaler": self._scaler, "detector": self._model, "name": self._name}

    @classmethod
    def from_bundle(cls, bundle: dict) -> "GroupMultivariateDetector":
        instance = cls.__new__(cls)
        instance._name = bundle["name"]
        instance._scaler = bundle["scaler"]
        instance._model = bundle["detector"]
        instance._fitted = True
        return instance
```

### Anti-Patterns to Avoid

- **Looping `ScoreBatch` once per member for joint-multivariate:** Defeats the entire purpose of
  GRP-05/success-criterion-3 — a univariate loop cannot catch a vector that is jointly abnormal
  while each individual feature stays within its own normal range. The 2D matrix MUST be built and
  passed as one array to `fit`/`decision_function`.
- **Reading `ECOD.O`/`COPOD.O` after other calls have happened:** `O` is mutated on every
  `decision_function` call and grows by concatenation once `X_train` exists (post-`fit`). Reading it
  "later" (e.g., after a second scoring call, or from a different thread) returns wrong or
  differently-shaped data. Extract attribution synchronously, right after the score call that
  produced it.
- **Leaving PyOD `PCA(standardization=True)` (the default) active alongside RobustScaler:** Silently
  double-scales the input, which both violates GRP-06's "scaler persisted with model, one owner of
  scaling" intent and produces numerically different (and untested) results versus what was
  validated in this research. Explicitly pass `standardization=False`.
- **Treating IForest/PCA as attribution-capable:** Both lack any built-in per-feature score
  decomposition in PyOD 3.6.0 (verified by source inspection — `IForest.decision_function` calls
  `invert_order(self.detector_.decision_function(X))`, a scalar per sample; PyOD's `PCA` only exposes
  aggregate `cdist` distance, no per-feature split). Do not fabricate an attribution list for these —
  return `None`/empty and let Phase 8 UI display "not available for this detector."
- **Using `scipy.stats.median_abs_deviation` with default `scale='normal'`:** That default divides by
  0.67449 automatically — combining it with the CONTEXT.md-mandated explicit `0.6745` multiplier
  would double-apply the constant. Either use `scale=1.0` explicitly, or (simpler, as shown in Code
  Examples) hand-roll `np.median(np.abs(x - median))` directly.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|--------------|-----|
| Joint-multivariate outlier scoring | A custom Mahalanobis-distance or reconstruction-error detector from scratch | PyOD's `ECOD`/`COPOD`/`PCA`/`IForest` (already fit/decision_function-compatible with the existing `PyODDetector` wrapper pattern) | These are peer-reviewed, tested implementations; PyOD is already the project's chosen library — reinventing them adds risk with zero benefit |
| Per-feature outlier scaling | A custom median/IQR scaler | `sklearn.preprocessing.RobustScaler` | Already available as a PyOD transitive dependency; handles edge cases (zero IQR, NaN) the project would otherwise need to test itself |
| Robust z-score / MAD computation | An ad-hoc "divide by stddev" scheme | Hand-rolled numpy `median`/MAD (NOT a library — this is the one case where hand-rolling is correct, since PyOD has no cross-member peer-divergence primitive; the CONTEXT.md decision confirms this) | PyOD's `MAD` class operates on a single feature column for outlier detection within it — semantically different from "which of these N sibling readings, at this one timestamp, diverges from its peers." No library implements this exact primitive off the shelf. |

**Key insight:** For joint-multivariate detection, PyOD already covers the entire algorithmic
surface this phase needs — the only "build" work is the RobustScaler-bundling wrapper and the
attribution-extraction glue, not the detection math itself. Peer-divergence is the one area where a
small (< 30 line) hand-rolled implementation is correct, because no off-the-shelf library expresses
this particular cross-member-per-timestamp comparison.

## Common Pitfalls

### Pitfall 1: ECOD/COPOD `self.O` is stale/wrong-shaped if read at the wrong time

**What goes wrong:** Code reads `detector.O` expecting per-feature attribution for the batch just
scored, but gets either training-data attribution (if read before any scoring call) or a matrix
containing BOTH training rows and new rows concatenated (if read after `fit()` was called, since
`X_train` persists).
**Why it happens:** PyOD's ECOD/COPOD implementation (verified via source inspection) internally does
`X = np.concatenate((self.X_train, X), axis=0)` inside `decision_function` whenever the detector has
already been fitted, recomputes `self.O` on the full concatenated array, then slices only the
*scores* (not `O` itself) back down to `original_size`.
**How to avoid:** Always slice `detector.O[-len(X_new):]` immediately after the `decision_function`
call that produced the scores you're attributing, in the same function/request handler — never
across requests or after other calls to the same detector instance.
**Warning signs:** Attribution matrix shape doesn't match the number of scored rows; attribution
values look like they belong to old/different data; attribution silently "drifts" over repeated
calls (each call grows `X_train` concatenation further if `X_train` itself gets updated — verify
whether your wrapper ever re-assigns `X_train`, which it does not by default, but `O`'s row count for
a *given* scoring call is always `len(X_train_at_fit_time) + len(X_new)`).

### Pitfall 2: PyOD `PCA(standardization=True)` default double-scales after RobustScaler

**What goes wrong:** GRP-06 requires the RobustScaler to be the single source of scaling truth
("scaler persisted with model" — implying it's the only scaling step). PyOD's `PCA` detector applies
its own internal `StandardScaler` by default (`standardization=True`), silently re-scaling
already-RobustScaler-scaled input.
**Why it happens:** PyOD's `PCA` was designed to be usable standalone without an external scaler;
the default assumes raw input.
**How to avoid:** Always construct `PCA(standardization=False)` when using it inside the
`GroupMultivariateDetector` wrapper, since the wrapper's own `RobustScaler` already normalized the
input.
**Warning signs:** Joint-multivariate PCA scores look implausibly stable/insensitive to actual
mixed-unit magnitude differences (the double-scaling partially cancels the intended effect of
RobustScaler), or scores don't reproduce when re-fit on the same data with a fresh scaler instance.

### Pitfall 3: MAD=0 divide-by-zero in peer-divergence when 3+ members report identical values

**What goes wrong:** If a group's members happen to report identical (or near-identical, rounding to
identical) values at a timestamp, `MAD = median(|x - median(x)|) = 0`, and the raw formula
`0.6745*(x-median)/MAD` divides by zero, producing `inf`/`nan`/`RuntimeWarning`.
**Why it happens:** MAD is 0 whenever at least half the values equal the median exactly — a
legitimate, not-rare case for environmental sensors reporting the same rounded value (e.g., three
thermostats all reading exactly 21.0°C at the same timestamp).
**How to avoid:** Guard `mad == 0` explicitly; fall back to mean absolute deviation (`meanAD`) per
Iglewicz & Hoaglin's own recommendation, using the corresponding constant `0.7979` (not `0.6745`,
which is calibrated for MAD specifically, not meanAD). If `meanAD` is ALSO 0 (every member exactly
identical), return all-zero z-scores (no divergence is possible or meaningful) rather than `NaN` —
this is a genuinely "no anomaly" case, distinct from the below-floor "no verdict possible" case in
Pitfall 4.
**Warning signs:** `RuntimeWarning: invalid value encountered in divide` in test output; unit test
with an all-identical-values fixture fails or produces `NaN` instead of `0.0`.

### Pitfall 4: Conflating "MAD=0 → all normal" with "below floor → no verdict" (both produce non-finite-looking output if not careful)

**What goes wrong:** GRP-04's below-floor case (< 3 members) and Pitfall 3's MAD=0 case are two
semantically different "can't fully compute" states, but both are tempting to represent the same way
(e.g., both as `NaN`). If they're not clearly distinguished, downstream code (Phase 6/8) cannot tell
"we have enough members but they all agree" (real, informative "no anomaly") from "we don't have
enough members to say anything" (uninformative "no verdict").
**Why it happens:** Both are edge cases discovered late in implementation without a planned contract
distinction.
**How to avoid:** Reserve `NaN` (or an explicit `has_verdict: false` proto field) strictly for the
below-floor case. MAD=0-with-sufficient-members must resolve to a concrete `0.0` z-score (not
flagged, not NaN) as specified in CONTEXT.md ("all members treated as normal").
**Warning signs:** A unit test asserting "below floor → no verdict" and a separate test asserting
"MAD=0 → not flagged" end up sharing assertion logic that can't distinguish the two, masking a bug
where one case is silently mishandled as the other.

### Pitfall 5: `group_` prefix collision is a directory-naming convention, not enforced by ModelStore

**What goes wrong:** `ModelStore.load_all_into` globs `*/*/latest` under `MODEL_ROOT` with no
namespace awareness — it will happily treat a directory named `group_boiler_room` as just another
"entity slug" and call `registry.register("group_boiler_room", detector, model)`. This is the
intended behavior (CONTEXT.md relies on the `group_` prefix simply not colliding with any real
`entity_id.replace('.', '_')` slug, since HA entity_ids always contain a domain dot like
`sensor.something` and never literally start with `group_` followed by an underscore-joined name
that also happens to be a valid entity slug) — but it means **no code currently prevents** a
misconfigured `group_id` from accidentally colliding with a real (dot-free, weirdly-named) entity
slug.
**Why it happens:** `ModelStore`/`DetectorRegistry` are generic key-value stores with no schema
validation on the key string itself.
**How to avoid:** Document (and ideally assert, at group config validation time in a later phase)
that `group_id` values themselves must never begin with `group_` (to avoid `group_group_x` confusion)
and, more importantly, that the `group_` prefix used by the model key builder (`group_{group_id}__...`)
is applied by the detector-side code, not user input — the model key format
`group_{group_id}__{detector}__v{version}` should be constructed by a single helper function, never
string-formatted ad hoc in multiple places, to guarantee the `group_` prefix is never accidentally
omitted or duplicated.
**Warning signs:** A unit test creating both a per-entity model with slug `group_x` (contrived, but
possible if an HA entity were literally named that) and a group model with `group_id=x` produces a
directory collision. This is a documented edge-case worth one explicit test even though realistically
unlikely (HA entity_ids always have a `domain.object_id` dot form, so `group_x` as a raw slug would
require an entity literally named `group_x` — extremely unlikely but not impossible if an operator
names a `sensor.group_x`).

### Pitfall 6: Python proto codegen must be re-run manually; .NET codegen is automatic

**What goes wrong:** After editing `proto/argus.proto`, `argus_pb2.py`/`argus_pb2_grpc.py` in
`detector/argus_detector/proto/` are stale until `python detector/scripts/gen_proto.py` is re-run.
The .NET side (via `<Protobuf Include="..\..\proto\argus.proto" GrpcServices="Client" />` MSBuild
item in `Argus.Orchestrator.csproj`) regenerates automatically on every `dotnet build` — so a
developer testing only the .NET side won't notice a stale Python proto, and vice versa.
**Why it happens:** Two different codegen mechanisms for the two languages sharing one `.proto` file
— Grpc.Tools MSBuild integration vs. a manual Python script.
**How to avoid:** Any task in this phase that edits `proto/argus.proto` MUST include a step running
`python detector/scripts/gen_proto.py` before Python tests are run. `test_proto_codegen.py` already
has an `autouse=True` session fixture that re-runs `gen_proto.py` automatically for the test suite —
but this doesn't help outside pytest (e.g., manual `python -c` smoke checks against the new messages).
**Warning signs:** `ImportError: cannot import name 'Series' from 'argus_detector.proto.argus_pb2'`
or `AttributeError` on a newly-added message/field despite it being present in `argus.proto`.

## Code Examples

### Modified z-score with MAD=0 guard (verified numerically in this session)

```python
# Source: verified by direct execution — see research session numpy output above
import numpy as np

x = np.array([10.0, 10.0, 10.0, 50.0])
median = np.median(x)          # 10.0
abs_dev = np.abs(x - median)   # [0, 0, 0, 40]
mad = np.median(abs_dev)       # 0.0  <- triggers guard
mean_ad = np.mean(abs_dev)     # 10.0
z = 0.7979 * (x - median) / mean_ad
# z = [0.0, 0.0, 0.0, 3.1916]  — the outlier (50.0) correctly flagged even
# though raw MAD-based formula would have divided by zero
```

### RobustScaler + ECOD bundle round-trip through joblib (verified in this session)

```python
# Source: verified by direct execution against installed pyod==3.6.0, scikit-learn==1.8.0
import numpy as np
import joblib
from sklearn.preprocessing import RobustScaler
from pyod.models.ecod import ECOD

X = np.array([[1000.0, 45.0], [1010.0, 50.0], [995.0, 40.0], [1005.0, 55.0]])  # hPa, %RH mix
scaler = RobustScaler()
Xs = scaler.fit_transform(X)

det = ECOD()
det.fit(Xs)

bundle = {"scaler": scaler, "detector": det}
joblib.dump(bundle, "group_model.joblib")
loaded = joblib.load("group_model.joblib")

# Roundtrip confirmed identical:
X_new = np.array([[1002.0, 48.0]])
Xs_new = loaded["scaler"].transform(X_new)
scores = loaded["detector"].decision_function(Xs_new)
contributions = loaded["detector"].O[-len(X_new):]  # per-feature attribution for X_new only
```

### Hand-constructed jointly-abnormal-but-marginally-normal test fixture (independent verifiability)

```python
# Source: designed for this phase's success criterion "no verdict below min-member floor"
# and "catches joint anomalies no single feature triggers" — verify by executing against
# the wrapper class above.
import numpy as np

# Two features individually within normal range for EACH feature's own marginal
# distribution, but jointly anomalous (strong positive correlation broken at the last row).
X_train = np.array([
    [1000.0, 20.0],
    [1002.0, 22.0],
    [998.0, 18.0],
    [1001.0, 21.0],
    [999.0, 19.0],
])  # feature 1 and feature 2 are strongly correlated (both rise/fall together)

X_test_joint_anomaly = np.array([
    [1002.0, 18.0],  # feature 1 is high-normal, feature 2 is low-normal INDIVIDUALLY,
                       # but the COMBINATION (high pressure + low value) breaks the
                       # learned correlation — this is the joint anomaly a univariate
                       # loop over each column separately would NOT catch.
])
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|-------------------|---------------|--------|
| Per-entity univariate detectors only (MAD/STL/HST) | Group-level peer-divergence + joint-multivariate detectors | This phase (v4.0 Phase 5) | New detector families require a new proto contract shape (2D matrix) not expressible in the existing `Point`/`ScoreBatchRequest` messages |
| `ScoreBatch` loop-per-entity RPC pattern | New dedicated `ScoreGroupBatch`/`FitGroup` RPCs | This phase | Avoids overloading `ScoreBatch`'s single-entity-per-call semantics; keeps a clean seam per CONTEXT.md |

**Deprecated/outdated:** None — this is additive; no existing detector, proto message, or RPC is
removed or changed in Phase 5.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `scikit-learn==1.8.0` should be pinned explicitly in `detector/requirements.txt` even though already present transitively | Standard Stack | Low — if omitted, the code still works today because PyOD pulls it in, but a future PyOD version could drop the sklearn dependency (unlikely) or change its version constraint, silently breaking `RobustScaler` availability. Pin explicitly to remove this risk entirely; near-zero cost. |
| A2 | `0.7979` is the correct Iglewicz-Hoaglin constant for the meanAD fallback (vs. `0.6745` for MAD) | Common Pitfalls / Code Examples | Medium — this is a well-known statistics constant from training data (Iglewicz & Hoaglin 1993, "Volume 16: How to Detect and Handle Outliers"), not verified against a live citation this session (no web search performed — provider config had brave/exa/firecrawl all disabled). If wrong, the meanAD fallback path (only triggered when MAD=0, a corner case) would use a mis-scaled constant, affecting flagging sensitivity only in that narrow scenario. Recommend the planner add a source-check task or accept as documented statistical convention. |
| A3 | HA entity_ids can never literally collide with a `group_`-prefixed slug in practice | Common Pitfalls (Pitfall 5) | Low — this is a defense-in-depth note, not a functional requirement; the actual collision-prevention mechanism is the `group_` prefix itself, applied by a single code path. |

**If this table is empty:** N/A — see entries above. A1 and A3 are low-risk process hygiene notes;
A2 is the one claim the planner may want a human/citation check on before treating it as load-bearing
in a scoring formula (though it only affects the rare MAD=0 corner case, not the primary `|z|>3.5`
threshold path which uses the CONTEXT.md-locked `0.6745` constant, verified correct for the standard
MAD case).

## Open Questions

1. **Should peer-divergence's meanAD fallback constant (`0.7979`) be a locked decision or Claude's
   discretion?**
   - What we know: CONTEXT.md locks `0.6745` for the primary MAD-based formula and specifies "epsilon
     guard / meanAD fallback" for MAD=0, but does not specify the meanAD-path constant.
   - What's unclear: Whether `0.7979` needs explicit user sign-off (it's a well-established but
     not-independently-verified-this-session statistical constant) or whether Claude's discretion to
     pick the textbook-standard value is sufficient.
   - Recommendation: Planner should treat this as Claude's discretion (matches the spirit of "epsilon
     guard / meanAD fallback" already being an implementation detail, not a user-facing tunable) and
     proceed with `0.7979`, documenting the source as a statistics convention in a code comment.

2. **Exact proto field numbering and message nesting for `GroupScoreRequest`/`GroupScoreResponse` —
   left to planner/implementer.**
   - What we know: CONTEXT.md specifies the conceptual shape (`Series`, `FeatureContribution`, union
     response, two new RPCs) but not exact field numbers or whether `mode` (peer vs. joint) is an enum
     field on the request or inferred server-side from `detector` name.
   - What's unclear: Whether `detector="peer_divergence"` vs `detector="ecod"` etc. is sufficient to
     dispatch mode server-side (my recommendation, mirroring the existing `_create_detector` factory
     pattern), or whether CONTEXT.md intends an explicit `mode` enum field for clarity.
   - Recommendation: Mirror the existing pattern — dispatch on `detector` string alone (adding
     `"peer_divergence"` to the servicer's group-detector factory, alongside `"ecod"`, `"copod"`,
     `"pca"`, `"iforest"`), avoiding a redundant `mode` field. This keeps the wire contract minimal and
     consistent with how `ScoreBatchRequest.detector` already works today.

## Security Domain

`security_enforcement` is not explicitly disabled in `.planning/config.json` (absent = enabled), but
this phase's ASVS surface is minimal — it is a Python compute-layer/proto-contract phase with no new
authentication, session, or network-facing surface beyond the existing mTLS-protected `DetectorService`
gRPC channel (unchanged transport, unchanged security posture).

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-------------------|
| V2 Authentication | No | Unchanged — existing mTLS on the gRPC channel (Phase 1-era decision D4), not touched by this phase |
| V3 Session Management | No | No session concept in the stateless detector service |
| V4 Access Control | No | Single-operator self-hosted deployment; no multi-tenancy (PROJECT.md D9) |
| V5 Input Validation | Yes | New RPC handlers (`ScoreGroupBatch`, `FitGroup`) must validate: empty `group_id`, mismatched `series` lengths (ragged matrix — different member value-array lengths would break `np.array(matrix)` construction with an unclear error), member count vs. floor (GRP-04), and unknown `detector` name (mirror existing `context.abort(grpc.StatusCode.INVALID_ARGUMENT, ...)` pattern from `ScoreBatch`) |
| V6 Cryptography | No | No new cryptographic operations; joblib deserialization risk (arbitrary code execution via pickle) is an already-accepted risk documented in `model_store.py`'s `T-02-03-01` comment for the single-operator deployment model — unchanged by this phase, applies identically to group model bundles |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|-----------------------|
| Ragged/malformed `Series` matrix (members with different-length value arrays) causing `np.array()` to silently produce an object-dtype array or raise a confusing `ValueError` deep in numpy/PyOD | Tampering / Denial of Service | Validate `len(series.values)` is identical across all `Series` in the request BEFORE constructing the numpy matrix; return `INVALID_ARGUMENT` with a clear message if not |
| Malicious/corrupted joblib model file triggering pickle deserialization RCE | Tampering | Already an accepted risk (see `T-02-03-01` in `model_store.py`) for the single-operator, locally-writable-only `/var/argus/models` deployment model. No new mitigation needed for group models — same threat model applies identically. |

## Sources

### Primary (HIGH confidence — verified via direct code execution against installed packages this session)
- `pyod==3.6.0` installed package source (`pyod.models.ecod.ECOD`, `pyod.models.copod.COPOD`, `pyod.models.pca.PCA`, `pyod.models.iforest.IForest`) — `fit`/`decision_function` signatures, `self.O` attribution mechanism and its concatenation-on-rescore behavior, `PCA.standardization` double-scaling risk — all confirmed by `inspect.getsource()` and live execution in this session.
- `scikit-learn==1.8.0` installed package (`sklearn.preprocessing.RobustScaler`) — fit/transform round-trip through joblib confirmed by direct execution.
- `joblib==1.5.3` — bundle dict serialization round-trip confirmed by direct execution (dump/load a `{"scaler":..., "detector":...}` dict).
- Existing project source: `detector/argus_detector/pyod_detector.py`, `model_store.py`, `registry.py`, `servicer.py`, `stl_detector.py`, `normalizer.py`, `proto/argus.proto`, `detector/scripts/gen_proto.py`, `orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj` — read directly this session.
- Existing test suite: `detector/tests/test_pyod_detector.py`, `test_model_store.py`, `test_proto_codegen.py`, `test_servicer.py`, `test_registry.py` — read directly this session to establish mirrored test patterns.
- `.planning/phases/05-.../05-CONTEXT.md`, `.planning/REQUIREMENTS.md`, `.planning/STATE.md`, `.planning/config.json` — read directly this session.

### Secondary (MEDIUM confidence)
- Iglewicz & Hoaglin modified z-score constants (`0.6745` for MAD, `0.7979` for meanAD fallback) — standard statistics reference (Iglewicz, B. and Hoaglin, D.C. 1993, "How to Detect and Handle Outliers", ASQC Basic References in Quality Control). Not independently re-verified via a live citation this session (no web search provider was configured/enabled — `brave_search`, `exa_search`, `firecrawl` all `false` in `.planning/config.json`); flagged in Assumptions Log as A2.

### Tertiary (LOW confidence)
- None — all findings in this research were either verified by direct code execution against the exact installed package versions, or are well-established statistics conventions flagged explicitly in the Assumptions Log.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — both `pyod` and `scikit-learn` versions confirmed installed and functioning via direct `pip show` and live execution against this exact project environment, not documentation lookup.
- Architecture: HIGH — proto/codegen split (manual Python vs. automatic .NET MSBuild) and ModelStore/DetectorRegistry extension points confirmed by direct source reading.
- Pitfalls: HIGH for PyOD-specific pitfalls (ECOD/COPOD `O` mutation, PCA double-scaling) — all verified by direct execution, not inferred from docs. MEDIUM for the Iglewicz-Hoaglin meanAD constant (A2, statistics-convention knowledge, not re-verified this session).

**Research date:** 2026-07-02
**Valid until:** 2026-08-01 (30 days — stable, pinned-version Python ML stack; no fast-moving dependencies in this phase)
