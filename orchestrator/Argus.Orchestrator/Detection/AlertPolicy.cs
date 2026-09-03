using Argus.Orchestrator.Config;

namespace Argus.Orchestrator.Detection;

/// <summary>
/// Outcome of a single verdict passed through the alert layer.
/// </summary>
/// <param name="FlagOn">The value the binary_sensor should carry after this verdict.</param>
/// <param name="EventStarted">A NEW event began (not a re-raise inside the refractory window).</param>
/// <param name="EventEnded">The running event closed (clear, or watchdog force-close).</param>
/// <param name="Storm">The rate cap or the watchdog fired — fail-loud signal, never silence.</param>
/// <param name="Rank">Mid-rank of this score in the entity's own recent score window.</param>
/// <param name="RawZ">Robust z of the last observed RAW value in the entity's own value window.</param>
/// <param name="Channel">Which evidence carried the fire decision: frozen/both/score/raw/none.</param>
public sealed record AlertDecision(
    bool FlagOn,
    bool EventStarted,
    bool EventEnded,
    bool Storm,
    double Rank,
    double RawZ,
    string Channel);

/// <summary>
/// Per-entity event layer replacing the absolute-threshold gate on the adaptive path (WS2).
///
/// The defects this class exists to make structurally impossible:
///  - F2 (a release threshold that is arithmetically unreachable): nothing here compares a score
///    with a numeric literal. Both channels are relative to the entity's OWN window, so a sensor
///    whose score never drops below 0.48 still clears within one window.
///  - F1 (a flag stuck ON for days): <see cref="AlertParams.MaxEventDurationSec"/> force-closes any
///    event, whatever the evidence says, and says so with a storm signal.
///  - F6 (one global threshold cannot fit five differently-scaled score distributions): rank and
///    robust-z are both dimensionless, so one default table is correct on every sensor.
///
/// Thread-safety: <see cref="ObserveValue"/> runs on the pipeline's write loop and
/// <see cref="OnVerdict"/> on its read loop, so every body takes one lock. The lock is never held
/// across an await — this class does no I/O and takes <c>now</c> as a parameter (the
/// ReconnectCooldown idiom) so it stays deterministic under test.
/// </summary>
public sealed class AlertPolicy
{
    private readonly AlertParams _params;
    private readonly RollingRank _rank;
    private readonly RollingRobustZ _raw;
    private readonly object _gate = new();

    private bool _firing;
    private int _consecAbove;
    private int _consecBelow;
    private int _samples;
    private double _lastRawZ;

    private DateTimeOffset _eventStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _holdUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEventEndedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _stormUntil = DateTimeOffset.MinValue;
    private readonly List<DateTimeOffset> _eventStarts = new();

    public AlertPolicy(AlertParams alertParams)
    {
        _params = alertParams ?? throw new ArgumentNullException(nameof(alertParams));
        _rank = new RollingRank(Math.Max(1, alertParams.RankWindow));
        _raw = new RollingRobustZ(Math.Max(1, alertParams.RawWindow), alertParams.ScaleFloor);
    }

    private bool _flagPublished;
    private bool _lastPublishedFlag;

    /// <summary>
    /// Last flag value actually published to MQTT for this entity, or null when nothing has been
    /// published yet. Read (not just written) by the pipeline so an unchanged flag is never
    /// republished — this is the whole of F8.
    ///
    /// Guarded by <see cref="_gate"/> like the rest of the class: a <c>bool?</c> is TWO fields
    /// (has-value + value), so an unsynchronised write from the write loop
    /// (<c>PublishFrozenAsync</c>) can be read torn by the verdict read loop. A torn
    /// <c>(hasValue: true, value: false)</c> makes the read loop believe OFF was already
    /// published and skip it — and the flag topic is retained, so HA keeps a retained ON that
    /// nothing puts out. That is F1 with a new mechanism.
    /// </summary>
    public bool? LastPublishedFlag
    {
        get { lock (_gate) return _flagPublished ? _lastPublishedFlag : null; }
        set
        {
            lock (_gate)
            {
                _flagPublished = value.HasValue;
                _lastPublishedFlag = value ?? false;
            }
        }
    }

    /// <summary>
    /// Atomically claims the right to publish <paramref name="value"/>: returns true (and records
    /// it) only when it differs from the last published flag.
    ///
    /// WHY a method and not a read followed by a write: the read loop and the write loop
    /// (<c>PublishFrozenAsync</c>) both do compare-then-set on this field. Interleaved, the write
    /// loop can publish ON and record it AFTER the read loop has already compared against the old
    /// value and concluded that its OFF needs no publish — leaving a retained ON in HA against a
    /// gate that says OFF. Making the compare and the set one critical section removes that
    /// window; the claim is taken BEFORE the publish, so a lost claim is a skipped duplicate,
    /// never a skipped transition.
    /// </summary>
    public bool TryClaimFlagPublish(bool value)
    {
        lock (_gate)
        {
            if (_flagPublished && _lastPublishedFlag == value)
                return false;
            _flagPublished = true;
            _lastPublishedFlag = value;
            return true;
        }
    }

    /// <summary>Verdicts observed since this policy was created.</summary>
    public int SampleCount { get { lock (_gate) return _samples; } }

    /// <summary>Raw values observed or seeded since this policy was created.</summary>
    public int RawSampleCount { get { lock (_gate) return _raw.Count; } }

    /// <summary>True once the rank channel has enough history to be trusted.</summary>
    public bool Calibrated { get { lock (_gate) return IsCalibrated(); } }

    /// <summary>Robust z of the most recently observed raw value.</summary>
    public double LastRawZ { get { lock (_gate) return _lastRawZ; } }

    /// <summary>storm | calibrating | firing | clear — surfaced by GET /api/sensors (A14).</summary>
    public string State
    {
        get
        {
            lock (_gate)
            {
                // Storm outranks everything: it is the one state that means "Argus is
                // deliberately not telling you about alarms right now".
                if (DateTimeOffset.UtcNow < _stormUntil) return "storm";
                if (!IsCalibrated()) return "calibrating";
                return _firing ? "firing" : "clear";
            }
        }
    }

    /// <summary>
    /// Feeds a live raw reading: computes its robust z against the window BEFORE adding it
    /// (so a value is never scored against itself), then retains it.
    /// </summary>
    public void ObserveValue(double value)
    {
        lock (_gate)
        {
            _lastRawZ = _raw.ZOf(value);
            _raw.Push(value);
        }
    }

    /// <summary>
    /// Adds a historical raw value WITHOUT scoring it. Used by backfill priming — history must
    /// build the baseline, never produce a z-score against a half-empty window.
    /// </summary>
    public void SeedValue(double value)
    {
        lock (_gate)
            _raw.Push(value);
    }

    /// <summary>Seeds a whole history series in ascending order (see <see cref="SeedValue"/>).</summary>
    public void SeedHistory(IReadOnlyList<double> values)
    {
        if (values is null) return;
        lock (_gate)
        {
            foreach (var v in values)
                _raw.Push(v);
        }
    }

    /// <summary>
    /// Passes one verdict through the event layer and returns what should happen.
    /// </summary>
    /// <param name="score">Detector score for this verdict.</param>
    /// <param name="warmedUp">Detector-reported warm-up state.</param>
    /// <param name="suppressed">Post-reconnect cooldown (D-07): blocks NEW events, not closes.</param>
    /// <param name="frozen">FrozenSensorDetector verdict for the latest reading.</param>
    /// <param name="now">Wall clock, injected so durations are testable.</param>
    public AlertDecision OnVerdict(double score, bool warmedUp, bool suppressed, bool frozen, DateTimeOffset now)
    {
        lock (_gate)
        {
            bool started = false, ended = false, storm = false;

            double rank = _rank.RankOf(score);
            _rank.Push(score);
            _samples++;

            bool calibrated = IsCalibrated();

            // The two channels warm up INDEPENDENTLY, and gating them together silences the one
            // sensor that works. The rank channel needs alert_min_samples VERDICTS (240) plus a
            // 50-deep rank window; the raw channel needs 10 raw VALUES, and backfill priming
            // (SeedHistory) hands it a full 720-sample window before the first verdict ever
            // arrives. A single warm-up gate over both meant lodowkababcia_power (~225
            // verdicts/day) stayed silent for ~26 h after every restart and after every
            // parameter change — with _lastRawZ around 17 the whole time. That breaks D-J
            // ("≥2 episodes on the fridge") on the only sensor with real precision.
            bool scoreReady = warmedUp && calibrated;
            bool rawReady = _raw.Count >= RollingRobustZ.MinSamples;

            // Neither channel has enough history to judge: hold the flag OFF, but close a running
            // event rather than stranding it. Close ONLY when firing — stamping _lastEventEndedAt
            // on every calibration tick would drop the first real event into the refractory
            // branch and it would never be counted.
            //
            // frozen does NOT exempt an entity from this gate. D-H names "frozen forces ON,
            // bypassing warm-up, suppression and hysteresis" as today's defect and puts the
            // frozen state into the gate as a PREMISE — subject to min_consecutive, to the
            // watchdog, and able to go out again. Suppression was already respected here;
            // warm-up was not, so a brand-new entity with no history at all could be pinned ON
            // by a variance reading taken over its first ten events. The guaranteed publish path
            // for an entity whose detector returns no verdict is unaffected: it is
            // PublishFrozenAsync on the write loop, not this gate.
            if (!scoreReady && !rawReady)
            {
                if (_firing)
                    ended = Close(now);
                return new AlertDecision(false, false, ended, false, rank, _lastRawZ, "none");
            }

            bool scoreHigh = scoreReady && rank >= _params.QFire;
            bool scoreLow = !scoreReady || rank < _params.QClear;
            bool rawHigh = rawReady && _lastRawZ >= _params.ZFire;
            bool rawLow = !rawReady || _lastRawZ < _params.ZClear;

            switch (_params.EvidenceMode)
            {
                case "score_only":
                    rawHigh = false; rawLow = true;
                    break;
                case "raw_only":
                    scoreHigh = false; scoreLow = true;
                    break;
            }

            bool both = _params.EvidenceMode == "both";
            bool fire = (both ? scoreHigh && rawHigh : scoreHigh || rawHigh) || frozen;
            bool clear = (both ? scoreLow || rawLow : scoreLow && rawLow) && !frozen;

            string channel = !fire ? "none"
                : frozen ? "frozen"
                : scoreHigh && rawHigh ? "both"
                : scoreHigh ? "score"
                : rawHigh ? "raw"
                : "none";

            if (!_firing)
            {
                // D-07 asymmetry: a cooldown reading may never START an event. It may still
                // close one (handled in the firing branch below).
                _consecAbove = fire && !suppressed ? _consecAbove + 1 : 0;

                if (_consecAbove >= _params.MinConsecutive)
                {
                    _consecAbove = 0;

                    if (now < _stormUntil)
                    {
                        // Storm hold: evidence is ignored on purpose, and stays reported as "storm".
                    }
                    else if (EventsInLastHour(now) >= _params.MaxEventsPerHour)
                    {
                        _stormUntil = now + TimeSpan.FromSeconds(_params.StormHoldSec);
                        storm = true;
                    }
                    else
                    {
                        _firing = true;
                        _holdUntil = now + TimeSpan.FromSeconds(_params.MinDurationSec);

                        // The ==MinValue arm is what protects the very first event ever: without
                        // it, _lastEventEndedAt (also MinValue) makes the refractory test true,
                        // _eventStartedAt stays at MinValue, and the watchdog force-closes the
                        // first alarm after every process start.
                        if (_eventStartedAt == DateTimeOffset.MinValue ||
                            (now - _lastEventEndedAt) >= TimeSpan.FromSeconds(_params.RefractorySec))
                        {
                            _eventStartedAt = now;
                            _eventStarts.Add(now);
                            started = true;
                        }
                    }
                }
            }
            else
            {
                _consecBelow = clear ? _consecBelow + 1 : 0;

                if ((now - _eventStartedAt) > TimeSpan.FromSeconds(_params.MaxEventDurationSec))
                {
                    // Watchdog. No evidence can hold a flag ON past this — F1's only backstop
                    // that does not depend on the scorer being correct.
                    ended = Close(now);
                    storm = true;
                    _stormUntil = now + TimeSpan.FromSeconds(_params.StormHoldSec);
                }
                else if (_consecBelow >= _params.MinConsecutive && now >= _holdUntil)
                {
                    ended = Close(now);
                }
            }

            return new AlertDecision(_firing, started, ended, storm, rank, _lastRawZ, channel);
        }
    }

    private bool IsCalibrated()
        => _samples >= _params.AlertMinSamples && _rank.Count >= MinRankSamples;

    /// <summary>
    /// Floor on the rank window's fill before the quantile gate may fire. With mid-ranks the
    /// largest attainable rank is 1 − 0.5/Count, so q_fire = 0.99 is arithmetically unreachable
    /// below 50 samples — without this floor the score channel would look permanently silent
    /// rather than uncalibrated.
    /// </summary>
    private const int MinRankSamples = 50;

    private bool Close(DateTimeOffset now)
    {
        _firing = false;
        _lastEventEndedAt = now;
        _consecAbove = 0;
        _consecBelow = 0;
        return true;
    }

    private int EventsInLastHour(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromHours(1);
        _eventStarts.RemoveAll(t => t < cutoff);
        return _eventStarts.Count;
    }
}
