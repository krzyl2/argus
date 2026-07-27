---
status: resolved
trigger: "Frozen sensor branch publishes binary_sensor flag ON but never publishes a score, leaving the score entity stuck at unknown in HA (observed on sensor.load_5m)."
created: 2026-07-24
updated: 2026-07-24
---

# Debug Session: frozen-flag-no-score

## Symptoms

**Expected behavior:**
When an entity is detected as frozen, the flag/score entity pair stays coherent — the score entity holds a numeric value. Class-level invariant (ScoreStreamPipeline.cs line 25) states "Score is always published".

**Actual behavior:**
On frozen detection the binary_sensor flag is published ON but the score entity is never published, so it remains `unknown` in Home Assistant. Observed on `sensor.load_5m` (System Monitor Load 5min): score sensor state = `unknown` while flag = `on`.

**Error messages / logs:**
```
warn: Argus.Orchestrator.Detection.ScoreStreamPipeline[0]
      Entity sensor.load_5m is frozen (variance < threshold) — publishing frozen flag
info: Argus.Orchestrator.Mqtt.StatePublisher[4005]
      Flag sensor.load_5m → ON
```
Score sensor `sensor.argus_sensor_load_5m_..._anomalia_score` = `unknown`.

**Timeline:**
Present since the frozen-detection branch was added. Surfaced during live UAT of System Monitor detectors.

**Reproduction:**
Feed an entity a low-variance (frozen) series → FrozenSensorDetector.IsFrozen trips → PublishFrozenAsync runs → flag ON, no score.

## Located root cause (pre-filled by orchestrator — verify, do not assume)

`ScoreStreamPipeline.PublishFrozenAsync` (orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs lines 230-238) calls `PublishFlagAsync(on: true)` + `PublishAvailabilityAsync(online: true)` but never `PublishScoreAsync`. This violates the documented "Score is always published" invariant (line 25).

Existing test `FrozenReading_PublishesFrozenFlag_AndAvailability` (ScoreStreamPipelineTests.cs line 240) asserts flag + availability but NOT `ScorePublished`, despite its comment claiming "still sends score" — so the gap was never caught.

## Proposed fix direction

1. Publish a score on the frozen branch so the flag/score pair stays coherent (decide the value — likely last-known / 0 / sentinel; verify what HA + hysteresis expect).
2. Correct the frozen test to assert the score-published invariant.
3. Add a flag-implies-score coherence invariant test (whenever a flag is published for an entity, a score was also published for that entity) — catches this class of bug generically.

## Current Focus

- hypothesis: CONFIRMED — PublishFrozenAsync omits PublishScoreAsync. For a frozen/low-variance entity the frozen branch is the only *guaranteed* publish path (the verdict path depends on the detector returning a verdict for constant input), so the score entity never receives a value and stays unknown in HA.
- next_action: apply fix — publish max-anomaly score (1.0) on the frozen branch; correct the frozen test; add a flag-implies-score coherence invariant test.

## Evidence

- timestamp: 2026-07-24 — ScoreStreamPipeline.cs:230-238 PublishFrozenAsync calls PublishFlagAsync(on:true) + PublishAvailabilityAsync(online:true), no PublishScoreAsync. Confirms code-level gap.
- timestamp: 2026-07-24 — ScoreStreamPipeline.cs:199-200 (ProcessVerdictAsync) is the ONLY path that calls PublishScoreAsync. Frozen readings are still forwarded to the detector (line 176), but score publication depends on a verdict returning; for a frozen (near-constant) input that verdict may be delayed/absent, so PublishFrozenAsync is the sole guaranteed publish for such an entity — and it omits the score.
- timestamp: 2026-07-24 — StatePublisher.cs:66-71 score is published with retain:false; a never-published score topic leaves HA at `unknown` (matches symptom).
- timestamp: 2026-07-24 — EntityRuntimeState.cs tracks LastPublishedFlag but NO last-known score, so republishing a prior score is not possible when the entity was frozen from the start → a fixed max-anomaly value (1.0) is the robust choice.
- timestamp: 2026-07-24 — ScoreStreamPipelineTests.cs:240-256 FrozenReading_PublishesFrozenFlag_AndAvailability asserts flag + availability but NOT ScorePublished, despite the section comment "still sends score". Test gap confirmed. FakeStatePublisher (line 311) records ScorePublished bool but not the value.

## Eliminated

- Verdict path (ProcessVerdictAsync) — it correctly publishes score always (line 200); not the source of the gap.
- Publishing last-known score on frozen — rejected: no last-score state exists and an entity frozen from start never had one.

## Resolution

- root_cause: ScoreStreamPipeline.PublishFrozenAsync published the binary_sensor flag ON + availability online but never published a score. For a frozen (near-constant) entity the frozen branch is the only guaranteed publish path — the verdict path depends on the detector returning a verdict for constant input — so the score topic (retain:false) was never written and the HA score entity stayed `unknown` while the flag read ON, violating the documented "Score is always published" invariant.
- fix: PublishFrozenAsync now publishes a max-anomaly score (const FrozenScore = 1.0) before the forced-ON flag, keeping the flag/score pair coherent (score 0.0 would read as a false positive; 1.0 matches the forced-ON flag and needs no new last-score state). Corrected FrozenReading_PublishesFrozenFlag_ScoreAndAvailability to assert ScorePublished + value; extended FakeStatePublisher with LastScoreValue; added generic PublishedFlag_AlwaysAccompaniedByScore_AcrossFrozenAndVerdictPaths coherence-invariant test (+ CoherenceTrackingPublisher fake) that fails if any path publishes a flag without a score.
- files: orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs; orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs
- verification: 2 target tests pass (2/2). Full ScoreStreamPipelineTests suite 8/9 pass; the 1 failure (RunAsync_CompleteAsyncCalledBeforeReadTaskAwaited) is a PRE-EXISTING flaky timing race unrelated to this fix (1 fail / 4 pass across 5 isolated re-runs; OrderTrackingDuplexCall completes its verdict channel in its ctor so the read loop can finish before CompleteAsync is recorded). Not touched — out of scope for this session.

## Blameless Postmortem

- why not caught: a test existed (FrozenReading_PublishesFrozenFlag_AndAvailability) with a comment claiming "still sends score" but it asserted only flag + availability, never the score — a false-confidence gate that could not fail when the score was dropped.
- guard added: (1) frozen test now asserts ScorePublished + value == 1.0; (2) generic flag-implies-score coherence-invariant test across frozen + verdict paths (PublishedFlag_AlwaysAccompaniedByScore_AcrossFrozenAndVerdictPaths) catches this class of bug for any future publish path.
- follow-up (out of scope): flaky RunAsync_CompleteAsyncCalledBeforeReadTaskAwaited ordering test should be made deterministic (it measures read-loop completion vs CompleteAsync call, which races when the verdict stream is pre-completed).
