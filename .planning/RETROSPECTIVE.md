# Project Retrospective

*A living document updated after each milestone. Lessons feed forward into future planning.*

## Milestone: v4.0 — Group & Multivariate Anomaly Detection + UX

**Shipped:** 2026-07-06
**Phases:** 5 (Phases 5-9) | **Plans:** 18 | **Tasks:** 42

### What Was Built
- Group anomaly detection, both modes: peer-divergence (median/MAD, "which member diverges") and joint-multivariate (RobustScaler + PyOD ECOD/COPOD/PCA/IForest, "jointly abnormal") over a real 2D-matrix proto contract with dedicated ScoreGroupBatch/FitGroup RPCs.
- End-to-end batch group pipeline: GroupInfluxReader (aggregateWindow+pivot, staleness cap), count/mode-aware MQTT discovery + retraction, config-load unit/floor guards, BatchSchedulerWorker + MqttPublisherWorker wiring.
- Config UI rebuilt as a Preact+Vite SPA (built at Docker build-time, no Node in runtime), with a guided "what are you monitoring?" algorithm chooser, Low/Med/High presets + Advanced override, ranked joint attribution, friendly-name search, area/domain browse, and area-scoped group suggestions.
- 2-member group support: joint floor lowered 3→2, a PairwiseDeltaDetector reusing the proven single-entity MAD detector for 2-member peer_divergence, and an empirically-corrected guided default (ecod→copod).

### What Worked
- **Backend-before-UI sequencing.** Phases 5-6 (proto + Python detectors + batch pipeline) were verified in isolation (`passed`) before any UI depended on them — so the deferred UI verification at close is cleanly scoped to visual/interaction sign-off, not detection correctness.
- **Empirical decision-making.** The Phase 9 guided-default correction was driven by an actual 10-seed synthetic-fixture experiment (ECOD/PCA ~90% false positives vs COPOD/IForest), not intuition — decisions logged with the evidence.
- **Reuse over invention.** The 2-member peer_divergence case reused the v1.0 MAD detector on a derived delta signal rather than inventing new group math.

### What Was Inefficient
- **Live-HA UI verification kept slipping.** Phases 07 and 08 both closed `human_needed`, and 10 Phase 08 UAT scenarios were skipped — the same live-HA sign-off gap seen at v3.0 close. UI verification debt is now a recurring pattern across two milestones.
- **STATE.md drift.** At milestone close, STATE.md still read `status: verifying` / `current_phase: 09` with a stale Phase 08 blocker, despite Phase 09 having passed verification — progress metadata lagged the actual artifact state.

### Patterns Established
- **Mode-dependent validation floors** enforced consistently across three layers (client TS, server C#, config-load C#) — the canonical place to add group-shape rules.
- **Draft-then-operator-edit copy:** DetectorCatalog `BestFor` text shipped as a draft explicitly flagged for operator final wording — separating "technically correct" from "final voice."

### Key Lessons
1. UI verification debt compounds — deferring live-HA sign-off two milestones running means the SPA has never been formally verified against real Ingress. Tie the deferred checks to the planned UI rebuild so they don't silently persist.
2. Verify detection math empirically before shipping guidance defaults — the "obvious" detector (ECOD) was wrong for correlated pairs; a small synthetic experiment caught it pre-ship.
3. Advance STATE.md at phase close, not just at milestone close — stale current_phase/status made the progress report misleading until reconciled.

### Cost Observations
- Model mix: planner on opus, researcher/checker on sonnet (per config profile).
- Notable: 135 commits over 4 days (2026-07-02 → 2026-07-06); heavy fix-commit tail in Phases 8-9 (code-review-driven CR/WR fixes) suggests review caught real issues late but before close.

---

## Cross-Milestone Trends

### Process Evolution

| Milestone | Phases | Key Change |
|-----------|--------|------------|
| v3.0 | 4 | Ingress UI (server-rendered htmx); formal UAT deferred after live bring-up |
| v4.0 | 5 | Group detection + SPA rewrite; backend verified in isolation, UI sign-off deferred again |

### Cumulative Quality

| Milestone | Requirements | Backend Verified | Deferred (UI/UAT) |
|-----------|--------------|------------------|-------------------|
| v3.0 | (v3 set) | yes | 8 items (4 UAT + 4 verification) |
| v4.0 | 25/25 | Phases 05/06/09 passed | 3 items (07/08 verification + 10 UAT scenarios) |

### Top Lessons (Verified Across Milestones)

1. Live-HA UI verification is consistently deferred — a recurring debt that needs a standing plan (tie to next UI rebuild), not per-milestone acknowledgement.
2. Backend-before-UI phase sequencing keeps deferred UI work from blocking detection-correctness confidence.
