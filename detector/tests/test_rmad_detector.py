"""
Tests for RmadDetector — the rolling median/MAD robust-z scorer (WS1, D-A/D-B).

Every test here encodes WHY a rule exists, not just what the code does. The
findings they defend are the F-numbers measured on the operator's live HA
instance on 2026-09-03 (docs/FIX-PLAN.md section 1):

  F1/F2 five binary_sensors stuck ON for >24 h because the release threshold
        was arithmetically unreachable.
  F3    the only sensor with real precision (fridge compressor, 83%) must not
        be silenced by the fix.
  F4    HalfSpaceTrees scores RARITY, so a rare-but-normal quantized level
        (101 W) outscored the modal level (107 W).
  F5    the unbounded MinMaxScaler collapsed the entire normal band to ~0.3%
        of [0,1] after a single excursion, permanently.
  F6    per-sensor score distributions made one global threshold impossible.
  F7    nothing observed the score distribution.

The gate replica below is a faithful copy of the .NET
Detection/HysteresisGate.Apply state machine (fire on min_consecutive scores
strictly above high, release on min_consecutive strictly below low, hold state
in the dead zone). It is duplicated here on purpose: WS1 must be able to prove
its score contract against the REAL event layer without the orchestrator, and
the .NET class is explicitly out of scope for this workstream.
"""

from __future__ import annotations

import copy
import logging
import math
import pickle
import random
import statistics

import pytest

from argus_detector.rmad_detector import (
    RmadDetector,
    _MAD_CONST,
    _mad_sorted,
    _median,
)

# D-B defaults the orchestrator will run with. Duplicated as literals so a
# silent change to the module constants cannot make these tests agree with
# themselves.
HIGH = 0.5
LOW = 0.375
MIN_CONSECUTIVE = 3


class _HysteresisGate:
    """Replica of Detection/HysteresisGate.cs (unchanged by this fix, D-C)."""

    def __init__(self, high=HIGH, low=LOW, min_consecutive=MIN_CONSECUTIVE):
        self._high = high
        self._low = low
        self._min = min_consecutive
        self._high_run = 0
        self._low_run = 0
        self.is_anomalous = False

    def apply(self, score: float) -> bool:
        if score > self._high:
            self._high_run += 1
            self._low_run = 0
            if self._high_run >= self._min:
                self.is_anomalous = True
        elif score < self._low:
            self._low_run += 1
            self._high_run = 0
            if self._low_run >= self._min:
                self.is_anomalous = False
        else:
            self._high_run = 0
            self._low_run = 0
        return self.is_anomalous


def _gate_stats(scores: list[float]) -> tuple[int, float]:
    """(episode count, on-time percent) for a score series through the gate."""
    gate = _HysteresisGate()
    episodes = 0
    on = 0
    previous = False
    for score in scores:
        state = gate.apply(score)
        if state and not previous:
            episodes += 1
        if state:
            on += 1
        previous = state
    return episodes, on / len(scores) * 100.0


def _stream(series: list[float], **kwargs) -> list[float]:
    det = RmadDetector(**kwargs)
    return [det.score_one(v) for v in series]


# ---------------------------------------------------------------------------
# Exact MAD
# ---------------------------------------------------------------------------


class TestExactMad:
    """The scale estimate is the DENOMINATOR of every score, so an off-by-one
    in the merge walk would silently rescale every alarm on every sensor
    instead of failing loudly."""

    def test_mad_merge_walk_equals_statistics_median_on_3000_windows(self):
        rng = random.Random(7)
        shapes = (
            lambda: rng.gauss(20.0, 1.5),
            lambda: float(rng.choice([101, 103, 105, 107, 109])),  # F4 histogram
            lambda: rng.choice([0.0, 984.0]),  # fridge compressor, bimodal
        )
        for trial in range(3000):
            n = rng.randint(1, 60)  # covers both even and odd windows
            draw = shapes[trial % 3]
            window = sorted(draw() for _ in range(n))
            med = _median(window)

            expected = statistics.median([abs(x - med) for x in window])
            assert _mad_sorted(window, med) == pytest.approx(expected, abs=1e-9)


# ---------------------------------------------------------------------------
# F4 — rarity inversion
# ---------------------------------------------------------------------------


class TestF4RarityInversion:
    """A score must mean "far from normal", never "rarely seen".

    F4 measured HalfSpaceTrees on the real zamrazarkapiwnica_power histogram:
    the rare level 101 W scored 0.997 while the MODAL level 107 W scored 0.560
    — the detector's opinion was inverted with respect to deviation, so no
    threshold placed anywhere could separate anomalies from normal readings.
    """

    HISTOGRAM = {101: 10, 103: 41, 105: 148, 107: 230, 109: 113}

    def test_quantized_level_scores_are_monotone_in_deviation_and_never_fire(self):
        rng = random.Random(1)
        population = [
            float(level) for level, count in self.HISTOGRAM.items() for _ in range(count)
        ]
        draws = [rng.choice(population) for _ in range(5000)]

        det = RmadDetector()
        scores = det.score_batch(draws)

        # Ignore the fill-up phase: from 720 on, the window is the steady state.
        by_level: dict[float, list[float]] = {}
        for value, score in zip(draws[720:], scores[720:]):
            by_level.setdefault(value, []).append(score)
        mean = {lvl: sum(v) / len(v) for lvl, v in by_level.items()}

        # Exactly reproducible, not a fit: median 107, MAD 2, so
        # sigma = 1.4826 * 2 = 2.9652 and score = z / (z + 5).
        def expected(level: float) -> float:
            z = abs(level - 107.0) / (_MAD_CONST * 2.0)
            return z / (z + 5.0)

        for level in (101.0, 103.0, 105.0, 107.0, 109.0):
            assert mean[level] == pytest.approx(expected(level), abs=1e-9)

        # The published F13 figures (docs/FIX-PLAN.md): 0.288 / 0.213 / 0.119 /
        # 0.000 / 0.119, against HalfSpaceTrees' 0.997 / 0.988 / 0.663 / 0.560 /
        # 0.882 on the same histogram.
        assert mean[107.0] == pytest.approx(0.000, abs=1e-3)
        assert mean[105.0] == pytest.approx(0.119, abs=1e-3)
        assert mean[109.0] == pytest.approx(0.119, abs=1e-3)
        assert mean[103.0] == pytest.approx(0.213, abs=1e-3)
        assert mean[101.0] == pytest.approx(0.288, abs=1e-3)

        # Strictly monotone in |x - 107|, and symmetric at equal distance.
        assert mean[101.0] > mean[103.0] > mean[105.0] > mean[107.0]
        assert mean[105.0] == pytest.approx(mean[109.0], abs=1e-9)

        # A perfectly normal quantized series must not produce a single alarm.
        assert max(scores) < HIGH
        assert _gate_stats(scores) == (0, 0.0)


# ---------------------------------------------------------------------------
# F2 — release must be reachable
# ---------------------------------------------------------------------------


class TestF2ReleaseIsReachable:
    """F1/F2: the field showed flags ON for >24 h because every 24 h minimum
    score (0.480 / 0.830 / 0.562 / 0.492 / 0.497) sat above the release
    threshold. An unbounded episode has to be STRUCTURALLY impossible, not
    merely unlikely: once a shifted level fills the baseline window it becomes
    the new normal, so the score returns to 0."""

    def test_sustained_level_shift_releases_within_one_window(self):
        rng = random.Random(11)
        series = [20.0 + rng.uniform(-0.05, 0.05) for _ in range(900)]
        series += [30.0] * 720

        scores = _stream(series)
        post = scores[900:]

        assert post[0] > HIGH, "a sustained 200-sigma level shift must fire"
        released = [i for i, s in enumerate(post) if s < LOW]
        assert released, "release is unreachable — this is F2 all over again"
        # Measured: the median crosses to the new level exactly at the halfway
        # point of the 720-sample baseline window.
        assert released[0] == 360
        assert released[0] <= 720


# ---------------------------------------------------------------------------
# F5 — one extreme must not collapse the band
# ---------------------------------------------------------------------------


class TestF5NormalBandSurvivesAnExtreme:
    """F5: MinMaxScaler kept unbounded running min/max, so after ONE 13.01
    reading on a series whose p50 was 0.54 the whole normal band collapsed into
    ~0.3% of [0,1] and never recovered. A rolling median/MAD window forgets the
    excursion by construction — a single sample cannot move a median."""

    def test_spike_does_not_collapse_subsequent_normal_scores(self):
        rng = random.Random(3)
        before = [rng.uniform(0.50, 0.60) for _ in range(500)]
        after = [rng.uniform(0.50, 0.60) for _ in range(200)]

        scores = _stream(before + [13.01] + after)

        assert scores[500] > HIGH, "the 13.01 excursion itself must fire"
        post = scores[501:]
        assert max(post) < LOW, (
            "a normal reading after the excursion must be releasable; "
            f"measured max {max(post)}"
        )


# ---------------------------------------------------------------------------
# F3 — recall on the only sensor with real precision
# ---------------------------------------------------------------------------


class TestF3RecallPreserved:
    """F3 measured lodowkababcia_power at 83% precision — the only genuinely
    working alarm in the whole system. Two compressor runs (0 W baseline,
    984 W while running) over 1546 Recorder rows (F12). Killing false alarms
    must not kill this."""

    @staticmethod
    def _fridge_series() -> list[float]:
        series = [0.0] * 1546
        for start in (720, 1400):
            for i in range(90):
                series[start + i] = 984.0
        return series

    def test_compressor_transition_fires_and_baseline_is_silent(self):
        series = self._fridge_series()
        scores = _stream(series)

        run_one = scores[720:810]
        run_two = scores[1400:1490]

        # First point of the first run sees a perfectly constant window
        # (MAD = 0, MeanAD = 0, scale_floor = 0) — scale-ladder rung 4.
        assert run_one[0] == 1.0
        # From the second point rung 2 (MeanAD) carries it: one 984 in a window
        # of 720 gives MeanAD = 984/720, hence z = 720.
        assert run_one[1] == pytest.approx(720.0 / 725.0, abs=1e-12)

        assert all(s > HIGH for s in run_one), f"run 1 min {min(run_one)}"
        assert all(s > HIGH for s in run_two), f"run 2 min {min(run_two)}"

        # The compressor-off baseline is the median, so it scores exactly zero —
        # a silent baseline is what makes the two runs readable as events.
        assert all(s == 0.0 for v, s in zip(series, scores) if v == 0.0)

        episodes, on_time = _gate_stats(scores)
        assert episodes == 2
        assert on_time == pytest.approx(11.64, abs=0.1)


# ---------------------------------------------------------------------------
# Degenerate scale and scale_floor
# ---------------------------------------------------------------------------


class TestDegenerateScale:
    """MAD == 0 is the COMMON case on this installation, not an edge case:
    lodowkababcia_power is 88% zeros. A ZeroDivisionError here does not lose
    one reading — servicer.py turns any exception into context.abort(INTERNAL),
    which tears down the whole multiplexed ScoreStream for every entity."""

    def test_constant_window_returns_zero_and_never_raises(self):
        scores = _stream([7.5] * 800)
        assert set(scores) == {0.0}

    def test_single_outlier_against_a_constant_window_does_not_break_score_one(self):
        det = RmadDetector()
        for _ in range(720):
            det.score_one(7.5)
        assert det.score_one(9.0) == 1.0

    def test_scale_floor_damps_a_low_noise_quantized_series(self):
        """D-I: scale_floor is a floor on SIGMA (rung 3), so it damps rung 1 as
        well — and rung 1 is what actually bites on a 1-decimal percent series.
        Shape of memory_use_percent: 5653 samples, levels 0.1 apart, occasional
        gentle 1.1 pp moves. MAD = 0.1 -> sigma = 0.148, so a 1.1 pp move is
        z = 7.4 and fires. Without this floor three of five sensors would ship
        a BRAND NEW false alarm."""
        n = 5653
        series = [round(38.0 + 0.1 * ((i % 3) - 1), 1) for i in range(n)]
        for start in (900, 2100, 3300, 4500):
            for i in range(99):
                series[start + i] = 39.1

        measured = {}
        for floor in (0.0, 0.05, 0.1, 0.3):
            scores = _stream(series, scale_floor=floor)
            measured[floor] = _gate_stats(scores) + (max(scores),)

        episodes, on_time, peak = measured[0.0]
        assert episodes == 4
        assert on_time == pytest.approx(7.0, abs=0.1)
        assert peak == pytest.approx(0.597, abs=5e-4)  # z = 1.1 / 0.14826

        # 0.05 and 0.1 are below 1.4826 * MAD, so they change nothing at all.
        assert measured[0.05] == measured[0.0]
        assert measured[0.1] == measured[0.0]

        # 0.3 pushes z down to 1.1/0.3 = 3.67 -> 0.42, under the 0.5 threshold.
        assert measured[0.3][0] == 0
        assert measured[0.3][1] == 0.0

    def test_scale_floor_suppresses_a_one_lsb_quantisation_step(self):
        """Shape of disk_use_percent: a coarse sensor that steps by one least
        significant bit and stays there. Accepted cost of scale-ladder rung 2
        (section 4 "Ryzyka"): the step DOES alarm, but the episode is bounded
        and self-extinguishing — it cannot become another F1. A scale_floor
        above the step size removes it entirely."""
        series = [45.2] * 1000 + [45.3] * 1000

        scores = _stream(series, scale_floor=0.0)
        episodes, on_time = _gate_stats(scores)
        assert episodes == 1
        assert on_time == pytest.approx(12.05, abs=0.1)
        # Self-extinguishing: the gate is OFF again well before the series ends.
        assert _HysteresisGate().apply(scores[-1]) is False

        assert _gate_stats(_stream(series, scale_floor=0.5)) == (0, 0.0)


# ---------------------------------------------------------------------------
# F13 — the remaining two of the five measured sensors
# ---------------------------------------------------------------------------


class TestF13RemainingSensorShapes:
    """F13 is a FIVE-sensor acceptance criterion (docs/FIX-PLAN.md section 5):
    zamrazarka 0 ep / 0%, memory 4 ep / 7.02%, load_5m 3 ep / 0.83%,
    processor_use 1 ep / 2.97%, lodowka 2 ep / 11.6%. Three of those pairs are
    already pinned above (TestF4RarityInversion, TestDegenerateScale,
    TestF3RecallPreserved); the two here close the set.

    The rule they encode is the whole point of WS1: the F1 field state was five
    flags stuck ON at 100/100/99/91/25% on-time. An alarm RATE regression on
    either of these two shapes — a detector change that quietly doubles the
    episode count or the time spent in alarm — is exactly the failure this fix
    exists to prevent, and it would otherwise pass every other test in this
    file, because none of them measures a rate on a mostly-normal series.

    Both fixtures use the F13 measurement parameters verbatim: window 720,
    min_samples 60, scale_floor 0.0, gate 0.5 / 0.375 / 3.
    """

    @staticmethod
    def _load_5m_series() -> list[float]:
        """Shape of sensor.load_5m: 5082 samples/24 h (the highest cadence on
        the installation, section 1 F12), a load average quantised to two
        decimals, plus three short genuine load bursts."""
        rng = random.Random(5)
        series = [round(0.50 + rng.uniform(-0.15, 0.15), 2) for _ in range(5082)]
        for start in (1200, 2600, 4100):
            for i in range(14):
                series[start + i] = round(2.40 + rng.uniform(-0.1, 0.1), 2)
        return series

    @staticmethod
    def _processor_use_series() -> list[float]:
        """Shape of sensor.processor_use: ~1440 samples/24 h (60/h — the plan's
        "~1 h to min_samples", section 5 F7), a percent series with one decimal
        idling near 4%, plus one genuine busy burst."""
        series = [round(4.0 + 0.1 * ((i % 3) - 1), 1) for i in range(1440)]
        for i in range(43):
            series[900 + i] = round(28.0 + 0.1 * (i % 5), 1)
        return series

    def test_load_5m_shape_fires_only_on_the_three_bursts(self):
        scores = _stream(self._load_5m_series())

        episodes, on_time = _gate_stats(scores)
        assert episodes == 3
        assert on_time == pytest.approx(0.83, abs=0.01)  # 42 of 5082 samples

        # The rate is low because the BASELINE is silent, not because the gate
        # is slow: with HalfSpaceTrees 80% of this sensor's samples scored above
        # 0.7 (F6). Every score above the fire threshold has to belong to a
        # burst, or the episode count is right for the wrong reason.
        burst = set()
        for start in (1200, 2600, 4100):
            burst.update(range(start, start + 14))
        assert all(i in burst for i, s in enumerate(scores) if s > HIGH)

    def test_processor_use_shape_fires_once_and_the_scale_floor_keeps_it(self):
        series = self._processor_use_series()

        episodes, on_time = _gate_stats(_stream(series))
        assert episodes == 1
        assert on_time == pytest.approx(2.97, abs=0.1)  # 43 of 1440 samples

        # D-J asks for "<= 2% on-time" here while F13 measures 2.97% — the plan
        # records that conflict (section 8) and WS1 ships the measurement, not a
        # tuned number. Pinning it is what makes the conflict visible if anyone
        # later "fixes" the criterion by weakening the detector.
        assert episodes <= 3

        # D-I makes WS2 set scale_floor=0.3 on every percent sensor, and this is
        # the sensor that proves the floor is safe: it damps the 0.1 pp
        # quantisation noise (see test_scale_floor_damps_a_low_noise_quantized_
        # series) WITHOUT costing a real 24 pp excursion its episode.
        assert _gate_stats(_stream(series, scale_floor=0.3)) == (episodes, on_time)


# ---------------------------------------------------------------------------
# Warm-up contract
# ---------------------------------------------------------------------------


class TestWarmUp:
    """D-M: the rolling window IS the calibration, so there is no separate
    calibration phase. min_samples (60) is the only gate, and it is what
    Verdict.window must report — ScoreStreamPipeline suppresses the flag while
    !warmed_up, and the UI renders "Rozgrzewka n_seen/window"."""

    def test_cold_phase_returns_exact_zero_until_min_samples(self):
        det = RmadDetector()
        assert det.window == 60
        assert det.baseline_window == 720

        for i in range(60):
            assert det.score_one(20.0 + i * 0.01) == 0.0
            assert det.n_seen == i + 1
            # Every one of these 60 readings was scored against fewer than
            # min_samples values, so none of them may claim to be warmed up.
            # This used to assert `is_warmed_up is (n_seen >= 60)`, which made
            # the last iteration assert BOTH "score == 0.0" and "warmed_up" —
            # the contradiction itself, written down as the contract.
            assert det.is_warmed_up is False

        # The 61st reading is the first one measured against a full window.
        det.score_one(20.6)
        assert det.is_warmed_up is True
        assert det.n_seen == 61
        assert det.window == 60

    def test_warmed_up_never_travels_with_the_structural_zero(self):
        """warmed_up describes the score returned WITH it, not the next one.

        Verdict.warmed_up and Verdict.score leave the detector together, and
        the .NET gate reads them together: warmed_up=true is what makes
        AlertPolicy trust the score channel and push the score into the rank
        window. A tick that pairs warmed_up=true with the structural 0.0
        therefore seeds the entity's own rank distribution with a value the
        sensor never produced — on the very first point the gate ever looks at.

        The ramp is strictly increasing, so every reading is the window maximum
        and a genuinely computed score can never come out as 0.0. Any zero in
        the warmed-up region is the structural one.
        """
        det = RmadDetector(window=720, min_samples=10)

        warmed_scores = [
            score
            for score in (det.score_one(20.0 + i) for i in range(30))
            if det.is_warmed_up
        ]

        assert warmed_scores, "must warm up within 30 readings"
        assert all(s > 0.0 for s in warmed_scores)

    def test_restored_checkpoint_is_warmed_from_its_own_window(self):
        """A pre-upgrade checkpoint has no _warmed_up field; it must not re-warm.

        The field is derived from the restored window instead of defaulting to
        False, because the next score_one really is measured against those
        values. Defaulting to False would suppress the flag on every entity in
        the house for one reading after every image upgrade.
        """
        det = RmadDetector(min_samples=10)
        for i in range(50):
            det.score_one(20.0 + i)

        legacy_state = dict(det.__dict__)
        legacy_state.pop("_warmed_up")

        restored = RmadDetector(min_samples=10)
        restored.__setstate__(legacy_state)

        assert restored.is_warmed_up is True

    def test_window_smaller_than_min_samples_never_claims_warm_up(self):
        """A window that cannot hold min_samples values produces no real score.

        The counter-based version reported warmed_up=true here while
        _score_from returned 0.0 forever, because len(window) is capped by
        `window` and never reaches min_samples. Fail loud: an entity whose
        params can never produce a verdict must look uncalibrated, not calm.
        """
        det = RmadDetector(window=5, min_samples=20)

        for i in range(100):
            assert det.score_one(20.0 + i) == 0.0
            assert det.is_warmed_up is False


# ---------------------------------------------------------------------------
# score_batch purity
# ---------------------------------------------------------------------------


class TestScoreBatchIsPure:
    """registry.score_batch hands out the LIVE model reference (registry.py
    :359-369). A mutating batch score would let an offline replay poison the
    streaming baseline of a running entity."""

    def test_score_batch_does_not_mutate_the_live_model(self):
        det = RmadDetector()
        for i in range(200):
            det.score_one(20.0 + (i % 7) * 0.1)

        before_values = list(det._values)
        before_sorted = list(det._sorted)
        before_n_seen = det.n_seen

        result = det.score_batch([100.0] * 50)

        assert isinstance(result, list)  # registry normalises a list to (list, None)
        assert len(result) == 50
        assert list(det._values) == before_values
        assert det._sorted == before_sorted
        assert det.n_seen == before_n_seen

    def test_score_batch_does_not_spend_the_live_warning_budget(self, caplog):
        """The degenerate-scale warning is one-shot PER LIVE ENTITY, not per process.

        registry.score_batch hands out the LIVE model reference, so the warning
        latch was the one piece of state an "offline" replay could still move.
        Two consequences, both bad: "score_batch does not mutate the live model"
        stops being true, and the first degenerate window on the REAL stream is
        swallowed because a preview already spent the budget — a silent scoring
        anomaly, which Rule 12 forbids outright.

        The budget moves, it is not removed: the batch still warns once per call.
        """
        det = RmadDetector(min_samples=5)
        for _ in range(20):
            det.score_one(7.0)  # a window that is a single constant => rung 4

        assert det._warned_degenerate is False

        with caplog.at_level(logging.WARNING, logger="argus_detector.rmad_detector"):
            batch = det.score_batch([100.0] * 3)

        # Rung 4 on the first point; the copy stops being constant once the
        # batch inserts 100.0 into it, so only the first score is off-scale.
        assert batch[0] == 1.0
        # Loud once for the batch itself...
        assert sum("degenerate scale" in r.message for r in caplog.records) == 1
        # ...and charged to the batch, not to the entity.
        assert det._warned_degenerate is False

        caplog.clear()
        with caplog.at_level(logging.WARNING, logger="argus_detector.rmad_detector"):
            det.score_one(100.0)

        assert sum("degenerate scale" in r.message for r in caplog.records) == 1
        assert det._warned_degenerate is True


# ---------------------------------------------------------------------------
# apply_params
# ---------------------------------------------------------------------------


class TestApplyParams:
    """Before this, params reached a detector only at creation time — so an
    operator editing the window in the UI saw no effect at all on any entity
    restored from a checkpoint (registry.py:78-81 + model_store.py:398)."""

    def test_window_change_takes_effect_on_a_checkpoint_restored_instance(self):
        det = RmadDetector()
        for i in range(720):
            det.score_one(20.0 + (i % 11) * 0.1)

        restored = pickle.loads(pickle.dumps(det))
        assert len(restored._values) == 720

        assert restored.apply_params({"window": "250"}) is True
        assert len(restored._values) == 250
        assert len(restored._sorted) == 250
        assert restored.baseline_window == 250

        # Idempotent: re-applying the same params is a no-op, not a re-drain.
        snapshot = list(restored._values)
        assert restored.apply_params({"window": "250"}) is False
        assert list(restored._values) == snapshot

    def test_wire_only_keys_never_enter_the_fingerprint(self):
        """params carries the algorithm name on the wire (map<string,string>,
        no proto change). It must not look like a param change, or every point
        would drain the window."""
        det = RmadDetector()
        assert det.apply_params({"algorithm": "rmad", "detector": "rmad"}) is False


# ---------------------------------------------------------------------------
# Checkpoint compatibility
# ---------------------------------------------------------------------------


class TestCheckpointCompat:
    """model_store.load_checkpoint only compares river_version, and rmad does
    not use river at all — so the ONLY guard against a mis-shaped restore is
    __setstate__ (unresolved blocker #11)."""

    def test_setstate_rebuilds_sorted_from_values_on_mismatch(self):
        det = RmadDetector()
        for i in range(100):
            det.score_one(float(i))

        # A torn snapshot: score_one mutates _values and _sorted outside every
        # lock while checkpoint_dirty deepcopies under the entity lock.
        torn = copy.deepcopy(det.__dict__)
        torn["_sorted"] = torn["_sorted"][:-1]

        healed = RmadDetector.__new__(RmadDetector)
        healed.__setstate__(torn)

        assert healed._sorted == sorted(healed._values)
        assert len(healed._sorted) == 100
        # And it still scores, rather than raising mid-stream.
        assert 0.0 <= healed.score_one(50.0) <= 1.0

        # deepcopy runs __setstate__ too, so it must not raise on a clean state.
        assert copy.deepcopy(det)._sorted == det._sorted

    def test_setstate_rejects_a_newer_schema_and_fills_missing_fields(self):
        det = RmadDetector()
        det.score_one(1.0)

        newer = copy.deepcopy(det.__dict__)
        newer["_schema"] = det._schema + 1
        with pytest.raises(ValueError, match="newer"):
            RmadDetector.__new__(RmadDetector).__setstate__(newer)

        # A checkpoint written before a field existed must restore, not blow up
        # on the first live reading (model_store isolates failures per entity,
        # so a raise here costs that sensor its whole warm-up).
        older = {k: v for k, v in copy.deepcopy(det.__dict__).items() if k != "_scale_floor"}
        restored = RmadDetector.__new__(RmadDetector)
        restored.__setstate__(older)
        assert restored.scale_floor == 0.0
        assert math.isfinite(restored.score_one(1.0))
