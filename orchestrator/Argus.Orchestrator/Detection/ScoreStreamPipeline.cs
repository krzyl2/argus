using Argus.Detector.V1;
using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Logging;
using Argus.Orchestrator.Mqtt;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using System.Globalization;

namespace Argus.Orchestrator.Detection;

/// <summary>
/// Bidi ScoreStream pipeline (STRM-03/STRM-04/STRM-05, FAULT-02, RES-01).
///
/// Design: one stream per entity (isolation — D-04 note). Each entity gets its own
/// AsyncDuplexStreamingCall so an RpcException on one entity only marks that entity offline.
///
/// Completion ordering (PITFALL 3): CompleteAsync() MUST be called before awaiting readTask.
/// The server only closes the response stream once the request stream is complete; reversing
/// the order causes the read loop to block forever (deadlock).
///
/// Warm-up suppression (PITFALL 8/D-07): binary_sensor flag is suppressed until
/// the entity has received at least HstParams.Window readings (HST calibration period).
/// SuppressBinarySensor=true (post-reconnect cooldown) also suppresses the flag.
/// Score is always published on the VERDICT path (dashboards see raw scores during
/// warm-up); on the frozen path it rides the flag's own gate, so the two topics are
/// never observed disagreeing.
///
/// Graceful degradation (RES-01): RpcException publishes availability "offline" for
/// the affected entity; the worker layer is responsible for re-establishing via
/// WaitForHealthyAsync (RES-03).
///
/// Latency logging (OBS-01/STRM-04): each verdict logs entity_id, score, latency_ms.
/// </summary>
public sealed class ScoreStreamPipeline
{
    private readonly IStatePublisher _publisher;
    private readonly ILogger<ScoreStreamPipeline> _logger;
    private readonly ILiveEntitiesConfig _liveConfig;
    private readonly DetectionGateway? _gateway;
    private readonly IEntityStatusCache? _statusCache;
    private readonly IRecentAnomaliesCache? _recentAnomalies;
    private readonly IInfluxDataSource? _historySource;
    private readonly IBatchDetectorClient? _detectorClient;
    private readonly ConnectionSettings? _connectionSettings;
    private readonly AlertStateStore? _alertStore;

    /// <summary>
    /// Production constructor — includes DetectionGateway for opening live streams.
    /// The three trailing backfill dependencies (Phase 15-03/D-15) are optional: when
    /// InfluxDB is unconfigured, Program.cs's DI registration leaves <paramref name="historySource"/>
    /// and <paramref name="detectorClient"/> null, which disables backfill priming with no
    /// separate feature check — the no-Influx streaming-only deployment keeps working exactly
    /// as before this phase.
    /// </summary>
    public ScoreStreamPipeline(
        IStatePublisher publisher,
        ILogger<ScoreStreamPipeline> logger,
        ILiveEntitiesConfig liveConfig,
        DetectionGateway gateway,
        IEntityStatusCache? statusCache = null,
        IRecentAnomaliesCache? recentAnomalies = null,
        IInfluxDataSource? historySource = null,
        IBatchDetectorClient? detectorClient = null,
        ConnectionSettings? connectionSettings = null,
        AlertStateStore? alertStore = null)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _liveConfig = liveConfig ?? throw new ArgumentNullException(nameof(liveConfig));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _statusCache = statusCache;
        _recentAnomalies = recentAnomalies;
        _historySource = historySource;
        _detectorClient = detectorClient;
        _connectionSettings = connectionSettings;
        _alertStore = alertStore;
    }

    /// <summary>
    /// Test constructor — no DetectionGateway (tests inject IScoreStreamCall directly via RunAsync overload).
    /// </summary>
    public ScoreStreamPipeline(
        IStatePublisher publisher,
        ILogger<ScoreStreamPipeline> logger,
        ILiveEntitiesConfig liveConfig,
        IEntityStatusCache? statusCache = null,
        IRecentAnomaliesCache? recentAnomalies = null,
        AlertStateStore? alertStore = null)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _liveConfig = liveConfig ?? throw new ArgumentNullException(nameof(liveConfig));
        _gateway = null;
        _statusCache = statusCache;
        _recentAnomalies = recentAnomalies;
        _alertStore = alertStore;
    }

    /// <summary>
    /// Runs the full pipeline for all configured entities.
    /// Opens one bidi stream per entity, handles frozen detection and hysteresis,
    /// and publishes results via MQTT. On RpcException marks entities unavailable.
    /// </summary>
    public async Task RunAsync(IAsyncEnumerable<HaReading> readings, CancellationToken ct)
    {
        if (_gateway is null)
            throw new InvalidOperationException("RunAsync(readings, ct) requires a DetectionGateway. Use the production constructor.");

        // Build per-entity state keyed by entity_id
        var entityStates = BuildEntityStates();

        // WR-03: fan-out — create one bounded channel per entity so each entity stream
        // has its own enumerator. A single shared IAsyncEnumerable cannot be iterated
        // concurrently (MoveNextAsync is not thread-safe).
        var entityChannels = entityStates.Keys.ToDictionary(
            id => id,
            _ => Channel.CreateBounded<HaReading>(500));

        // Fan-out task: read once, route to matching per-entity channel.
        // try/finally ensures channel writers are always completed — even on cancellation
        // or an unexpected exception — so per-entity stream tasks are never left blocked
        // on ReadAllAsync waiting for a writer signal that will never arrive.
        // TryComplete (not Complete) is safe to call more than once in edge cases.
        var fanOutTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var r in readings.WithCancellation(ct))
                    if (entityChannels.TryGetValue(r.EntityId, out var ch))
                        await ch.Writer.WriteAsync(r, ct);
            }
            finally
            {
                foreach (var ch in entityChannels.Values)
                    ch.Writer.TryComplete();
            }
        }, ct);

        var tasks = entityStates.Select(kvp =>
            RunEntityStreamAsync(kvp.Key, kvp.Value, entityChannels[kvp.Key].Reader.ReadAllAsync(ct), ct));

        await Task.WhenAll(tasks.Append(fanOutTask));
    }

    /// <summary>
    /// Runs the bidi loop for a single entity using the provided IScoreStreamCall abstraction.
    /// This overload is the primary testable surface — tests inject an OrderTrackingDuplexCall.
    /// Completion ordering (PITFALL 3): CompleteAsync before await readTask.
    /// </summary>
    public async Task RunAsync(
        IScoreStreamCall call,
        string entityId,
        IAsyncEnumerable<HaReading> readings,
        EntityRuntimeState entityState,
        CancellationToken ct)
    {
        // Read loop runs concurrently with the write loop
        var readTask = Task.Run(async () =>
        {
            await foreach (var verdict in call.ReadAllVerdictsAsync(ct))
            {
                // SuppressBinarySensor is tracked per-entity in entityState (updated in write loop).
                // Use entityState value so post-reconnect cooldown (D-07) and warm-up (PITFALL 8)
                // suppression is correctly forwarded to ProcessVerdictAsync.
                await ProcessVerdictAsync(
                    new Ha.HaReading(entityId, 0.0, DateTimeOffset.UtcNow, entityState.SuppressBinarySensor),
                    verdict, entityState, ct);
            }
        }, ct);

        // Write loop: feed readings to the stream
        await foreach (var reading in readings.WithCancellation(ct))
        {
            if (reading.EntityId != entityId)
                continue;

            entityState.FrozenDetector.AddReading(reading.Value);
            entityState.SuppressBinarySensor = reading.SuppressBinarySensor;

            // F6-3: measure the sensor's own cadence here, from the reading's HA timestamp.
            // window is configured in SAMPLES, so without this the editor cannot tell the
            // operator whether 720 samples is 3 h of baseline or 78 h.
            entityState.Cadence.Observe(reading.LastChanged);

            // WS2: the raw-evidence channel is fed HERE, from the real reading value. The
            // verdict read loop only ever sees the synthetic HaReading below (value 0.0), so
            // moving ObserveValue there would compute every z-score against a constant zero.
            entityState.LastValue = reading.Value;
            entityState.Alert.ObserveValue(reading.Value);
            entityState.FrozenNow = entityState.FrozenDetector.IsFrozen;

            if (entityState.FrozenNow)
            {
                _logger.LogWarning(
                    "Entity {EntityId} is frozen (variance < threshold) — publishing frozen flag",
                    reading.EntityId);
                await PublishFrozenAsync(reading.EntityId, entityState, ct);
                // Still forward to detector for model continuity (HST keeps learning)
            }
            else
            {
                // The run of frozen readings is over: a sensor that thaws and re-freezes must
                // earn min_consecutive again, exactly as the read loop's counter is reset by a
                // non-firing verdict.
                entityState.Alert.ClearFrozenRun();
            }

            // D-01/WARM-01: warm-up no longer counted here — RecordReading() is gone.
            // The status cache is now written from the verdict read loop
            // (ProcessVerdictAsync), since warm-up data arrives on the Verdict.
            var point = ToPoint(reading, entityState);
            await call.WriteAsync(point, ct);
        }

        // PITFALL 3: CompleteAsync BEFORE await readTask (never reverse — deadlock)
        await call.CompleteAsync();
        await readTask;
    }

    /// <summary>
    /// Processes a single verdict: publishes score always; publishes the binary_sensor flag
    /// only on a CHANGE of value and only when not suppressed (PITFALL 8/D-07, F8).
    ///
    /// Two gates live here, selected by alert_mode:
    ///  - "adaptive" (default, WS2): AlertPolicy — per-entity rank + robust-z evidence,
    ///    min-duration/refractory/rate-cap/watchdog, change-only publishing.
    ///  - "legacy": the original HysteresisGate against absolute thresholds, kept verbatim as a
    ///    no-redeploy rollback path (A13).
    ///
    /// Warm-up/calibration suppress the flag's VALUE (an explicit OFF clears a retained ON);
    /// the post-reconnect cooldown suppresses the PUBLISH itself.
    /// Logs per-verdict latency at Debug (OBS-01/STRM-04).
    /// </summary>
    public async Task ProcessVerdictAsync(
        Ha.HaReading reading,
        Verdict verdict,
        EntityRuntimeState entityState,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        double score = verdict.Score ?? 0.0;

        // D-01/WARM-01: apply the detector's own warm-up numbers first — this is the
        // single point where WarmedUp/ReadingCount/WarmUpWindow change value.
        entityState.ApplyVerdictWarmup(verdict.WarmedUp, verdict.NSeen, verdict.Window);

        // Always publish score (raw metric visible even during warm-up)
        await _publisher.PublishScoreAsync(reading.EntityId, score, ct);

        if (string.Equals(entityState.AlertParams.Mode, "legacy", StringComparison.OrdinalIgnoreCase))
        {
            await ProcessVerdictLegacyAsync(reading, score, entityState, startedAt, ct);
            return;
        }

        var decision = entityState.Alert.OnVerdict(
            score,
            entityState.WarmedUp,
            reading.SuppressBinarySensor,
            entityState.FrozenNow,
            DateTimeOffset.UtcNow);

        // D-10: status cache write relocated here from the write loop — the data now
        // arrives with the verdict, not the raw reading. Alert calibration rides along so
        // "calibrating"/"storm" is visible in GET /api/sensors (A14).
        _statusCache?.Set(new EntityStatusEntry(
            reading.EntityId, entityState.WarmedUp, entityState.ReadingCount, entityState.WarmUpWindow,
            entityState.Alert.Calibrated, entityState.Alert.SampleCount,
            entityState.AlertParams.AlertMinSamples, entityState.Alert.State,
            // WS3/D-E: the detector already puts its band on the wire (Verdict.expected/lower/
            // upper). Carrying it through unchanged — nulls included — is what lets the editor
            // render the threshold in the sensor's own units instead of a bare 0.5, and what
            // keeps it honest before the first band exists.
            verdict.Expected, verdict.Lower, verdict.Upper,
            entityState.Cadence.MedianIntervalSec));

        // F8: publish ONLY on a transition. The cooldown (D-07) still blocks the publish itself.
        bool published = false;
        if (!reading.SuppressBinarySensor)
            published = await PublishFlagIfChangedAsync(reading.EntityId, entityState, decision.FlagOn, ct);

        if (decision.EventStarted)
        {
            // One entry per EVENT, not per verdict — the Dashboard's "Recent anomalies" list
            // counts episodes, and a firing entity produces a verdict every tick.
            //
            // The detector name comes from the entity's own state, never from a literal: since
            // D-A the default detector is rmad, so a hardcoded "hst" labels every card on the
            // Dashboard with the name of an algorithm the entity is not running — the same
            // class of lie as F0, and it points the operator at the wrong params to edit.
            _recentAnomalies?.Record(new RecentAnomaly(
                reading.EntityId, null, score, entityState.DetectorName, DateTimeOffset.UtcNow));
            _logger.LogInformation(LogEvents.AlertEventStarted,
                "Alert started for {EntityId}: rank={Rank:F4} z={Z:F2} channel={Channel}",
                reading.EntityId, decision.Rank, decision.RawZ, decision.Channel);
        }

        if (decision.EventEnded)
        {
            _logger.LogInformation(LogEvents.AlertEventEnded,
                "Alert ended for {EntityId}: rank={Rank:F4} z={Z:F2}",
                reading.EntityId, decision.Rank, decision.RawZ);
        }

        if (decision.Storm)
        {
            // Rule 12: an alarm suppressed by the rate cap or force-closed by the watchdog
            // must never be silent — this WARN is the only trace it leaves.
            _logger.LogWarning(LogEvents.AlertStormRaised,
                "Alert storm for {EntityId} — rate cap or max event duration hit; suppressing onsets for {HoldSec}s",
                reading.EntityId, entityState.AlertParams.StormHoldSec);
        }

        var latencyMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        _logger.LogDebug(
            "Verdict: entity={EntityId} score={Score:F4} anomalous={IsAnomalous} flagPublished={FlagPublished} " +
            "rank={Rank:F4} z={Z:F2} state={AlertState} published={Published} latency_ms={LatencyMs:F1}",
            reading.EntityId, score, decision.FlagOn, published,
            decision.Rank, decision.RawZ, entityState.Alert.State, published, latencyMs);
    }

    /// <summary>
    /// The pre-WS2 absolute-threshold path, reachable with alert_mode: legacy in an entity's
    /// detector params. Kept byte-for-byte in behaviour (HysteresisGate + publish every tick)
    /// so it is a real rollback and not a second, subtly different gate.
    /// </summary>
    private async Task ProcessVerdictLegacyAsync(
        Ha.HaReading reading,
        double score,
        EntityRuntimeState entityState,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        _statusCache?.Set(new EntityStatusEntry(
            reading.EntityId, entityState.WarmedUp, entityState.ReadingCount, entityState.WarmUpWindow,
            Calibrated: entityState.WarmedUp, CalibrationCount: entityState.ReadingCount,
            CalibrationTarget: entityState.WarmUpWindow, AlertState: "legacy",
            MedianIntervalSec: entityState.Cadence.MedianIntervalSec));

        bool isAnomalous = entityState.Hysteresis.Apply(score);

        bool canPublishFlag = !reading.SuppressBinarySensor && entityState.WarmedUp;
        if (canPublishFlag)
        {
            await _publisher.PublishFlagAsync(reading.EntityId, isAnomalous, ct);
            entityState.Alert.LastPublishedFlag = isAnomalous;

            if (isAnomalous)
                _recentAnomalies?.Record(new RecentAnomaly(
                    reading.EntityId, null, score, entityState.DetectorName, DateTimeOffset.UtcNow));
        }

        var latencyMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        _logger.LogDebug(
            "Verdict: entity={EntityId} score={Score:F4} anomalous={IsAnomalous} flagPublished={FlagPublished} latency_ms={LatencyMs:F1}",
            reading.EntityId, score, isAnomalous, canPublishFlag, latencyMs);
    }

    /// <summary>
    /// The ONE way this pipeline puts a flag on the wire: claim and publish under the entity's
    /// own <see cref="EntityRuntimeState.FlagPublishGate"/>, so the order the broker sees is the
    /// order the claims were taken in.
    ///
    /// Both callers run concurrently for the same entity — <see cref="ProcessVerdictAsync"/> on
    /// the verdict read loop, <see cref="PublishFrozenAsync"/> on the write loop. Claiming under
    /// the policy's lock but awaiting the broker outside it makes the two publishes atomic
    /// individually and unordered together: HA can end up retaining ON while the policy records
    /// OFF (and then drops the next real OFF as a duplicate — the flag never goes out again), or
    /// the mirror image, HA OFF against a policy that believes ON, which loses a real alarm
    /// silently. The flag topic is retained, so neither self-heals.
    ///
    /// The claim is also rolled back when the broker call throws. Without that, a failed publish
    /// leaves the policy believing the transition was delivered, and it is never retried.
    /// </summary>
    /// <returns>True when a publish was actually issued (i.e. the value changed).</returns>
    private async Task<bool> PublishFlagIfChangedAsync(
        string entityId, EntityRuntimeState entityState, bool on, CancellationToken ct)
    {
        await entityState.FlagPublishGate.WaitAsync(ct);
        try
        {
            if (!entityState.Alert.TryClaimFlagPublish(on, out var rollbackTo))
                return false;

            try
            {
                await _publisher.PublishFlagAsync(entityId, on, ct);
            }
            catch
            {
                entityState.Alert.RollbackFlagClaim(rollbackTo);
                throw;
            }

            return true;
        }
        finally
        {
            entityState.FlagPublishGate.Release();
        }
    }

    /// <summary>
    /// Score published on the frozen branch. It goes out with — and only with — the forced-ON
    /// flag, so the pair HA sees is coherent in both directions: a 0.0 next to an ON flag would
    /// read as a false positive, and a 1.0 next to an OFF flag is the same lie with the halves
    /// swapped. A fixed sentinel is used because no last-known score is retained per entity and
    /// an entity frozen from the start never produced a verdict-based score.
    /// </summary>
    private const double FrozenScore = 1.0;

    /// <summary>
    /// Publishes a frozen sensor detection result: score (max-anomaly) + binary_sensor ON +
    /// availability online (FAULT-02). Called when FrozenSensorDetector.IsFrozen for a reading.
    ///
    /// Everything here is gated on the SAME premises the verdict read loop applies to frozen
    /// (D-H) — the post-reconnect cooldown, min_consecutive, and the raw channel's readiness.
    /// Two loops deciding the same frozen state must not answer it differently: the flag topic
    /// is RETAINED, so a disagreement is not a transient, it is what HA keeps.
    ///
    /// Availability is the exception, and deliberately so: it says the sensor is present and
    /// reporting, which is true throughout, cooldown included.
    /// </summary>
    public async Task PublishFrozenAsync(string entityId, EntityRuntimeState entityState, CancellationToken ct)
    {
        // D-07 comes first, because the cooldown's own trigger is what lights this branch: a
        // reconnect replays a get_states burst of IDENTICAL retained values, a zero-variance
        // window, i.e. IsFrozen. The read loop honours the cooldown twice over (OnVerdict will
        // not START an event on a suppressed reading, and ProcessVerdictAsync publishes nothing
        // while suppressed); the write loop asked nobody, so the one loop that ignored the
        // cooldown was the one its trigger drives straight into a forced ON. That burst of false
        // flags out of a snapshot is exactly what D-07 exists to prevent.
        //
        // The run is CLEARED rather than merely held, mirroring OnVerdict's
        // `_consecAbove = fire && !suppressed ? _consecAbove + 1 : 0`: without it the snapshot
        // burst banks min_consecutive and the first live reading after the cooldown fires
        // instantly — the same false flag, arriving 60 s late instead of not at all.
        if (entityState.SuppressBinarySensor)
        {
            entityState.Alert.ClearFrozenRun();
        }

        // Frozen raises binary_sensor ON, change-only (F8): repeating ON on every frozen reading
        // was ~4 publishes per 15 s per entity. The invariant this path exists for — a frozen
        // entity whose detector emits no verdict still gets a flag — is preserved; only the
        // repetition is gone.
        //
        // ...and only once the raw evidence channel is ready, which is the same premise
        // OnVerdict applies to frozen after D-H. The two loops must not answer the same question
        // differently: with frozen_window under RollingRobustZ.MinSamples the frozen detector
        // latches before the gate can speak, and the write loop's ON then alternates on the wire
        // with the read loop's OFF for the same readings. The wait is bounded and self-clearing
        // — the write loop feeds ObserveValue on every reading, and backfill priming fills the
        // window before the stream even opens — so a frozen entity still gets its flag.
        //
        // ...and only once frozen has held for min_consecutive readings, which is the OTHER
        // premise OnVerdict applies to it (D-H: frozen "podlega min_consecutive"). Without that
        // the read loop, counting the same frozen state over verdicts, publishes OFF for
        // min_consecutive-1 of them while this loop is publishing ON — ON/OFF/ON on a RETAINED
        // topic. OnFrozenReading is called on EVERY frozen reading (one tick per reading), so
        // the debounce advances even for an entity whose detector returns no verdict at all:
        // the guaranteed publish path of D-H survives, delayed by min_consecutive readings.
        else if (entityState.Alert.OnFrozenReading() && entityState.Alert.RawChannelReady)
        {
            // The score rides the SAME gate rather than standing above it. Its unconditional
            // position predates D-H, when this branch forced the flag ON and the score was here
            // to keep that pair coherent; with the forcing gone, an ungated score published 1.0
            // — the maximum possible anomaly — against a binary_sensor reading OFF for every
            // reading of the min_consecutive debounce, and for the whole D-07 cooldown. Score
            // before flag, so the two topics are never observed disagreeing.
            await _publisher.PublishScoreAsync(entityId, FrozenScore, ct);
            await PublishFlagIfChangedAsync(entityId, entityState, on: true, ct);
        }

        // Sensor is present and reporting (just frozen), so availability stays online
        await _publisher.PublishAvailabilityAsync(entityId, online: true, ct);
    }

    /// <summary>
    /// Publishes availability "offline" for the entity after an RpcException (RES-01).
    /// Does not crash the worker — caller handles re-establishment via WaitForHealthyAsync.
    /// </summary>
    public async Task HandleDetectorFailureAsync(string entityId, CancellationToken ct)
    {
        _logger.LogWarning(
            "Detector RpcException for {EntityId} — publishing availability offline (RES-01)",
            entityId);
        await _publisher.PublishAvailabilityAsync(entityId, online: false, ct);
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private Task RunEntityStreamAsync(
        string entityId,
        EntityRuntimeState entityState,
        IAsyncEnumerable<HaReading> readings,
        CancellationToken ct)
        => RunEntityStreamAsync(
            entityId,
            entityState,
            readings,
            token => new LiveScoreStreamCall(_gateway!.DetectorClient.ScoreStream(cancellationToken: token)),
            ct);

    /// <summary>
    /// Runs one entity stream with an injectable call factory. The factory seam lets tests
    /// exercise the failure handling below without a live gRPC channel — the handling is the
    /// only thing standing between a detector-side stream failure and a host shutdown.
    /// </summary>
    internal async Task RunEntityStreamAsync(
        string entityId,
        EntityRuntimeState entityState,
        IAsyncEnumerable<HaReading> readings,
        Func<CancellationToken, IScoreStreamCall> callFactory,
        CancellationToken ct)
    {
        try
        {
            // BACKFILL-01..04/D-15: one bounded, ascending, idempotent prime attempt before
            // every stream open. Bails out early (no-op) when backfill is unavailable/disabled,
            // and never lets a failure here prevent the stream from opening below.
            await PrimeFromHistoryAsync(entityId, entityState, ct);

            var call = callFactory(ct);
            await RunAsync(call, entityId, readings, entityState, ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
            // Client-side cancellation (host shutdown or CFG-04 reload) surfaces from the
            // gRPC stream as RpcException/Cancelled, not OperationCanceledException. Letting
            // it escape fails HaListenerWorker and stops the host on every clean shutdown.
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            await HandleDetectorFailureAsync(entityId, ct);
            _logger.LogError(ex,
                "ScoreStream RpcException for {EntityId}: {Status} — entity marked offline",
                entityId, ex.Status);
        }
        catch (RpcException ex)
        {
            // Cancelled without our own cancellation — the detector dropped the call.
            // Treat it like any other stream failure: mark the entity offline, stay alive.
            await HandleDetectorFailureAsync(entityId, CancellationToken.None);
            _logger.LogWarning(ex,
                "ScoreStream cancelled by detector for {EntityId} — entity marked offline",
                entityId);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — do not log as error
        }
    }

    /// <summary>
    /// Baseline window rmad needs in hand before its median/MAD estimate is trustworthy (D-A/D-B).
    /// Used as a FLOOR on the backfill request, not a replacement for the configured window: an
    /// entity still carrying the legacy hst window (250) must be primed for the detector it is
    /// about to run, and an entity configured wider than 720 keeps its own number.
    /// </summary>
    internal const int RmadBaselineWindow = 720;

    /// <summary>
    /// Per-entity deadline on one priming attempt (§7 #6). Six entities at startup is therefore a
    /// bounded ~3 min worst case on the shared fan-out task, not an open-ended stall.
    /// </summary>
    private static readonly TimeSpan PrimeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Primes a cold detector and the frozen-sensor window from history before the
    /// entity's ScoreStream opens (BACKFILL-01..04). Internal (rather than private) so
    /// ScoreStreamPipelineTests can exercise it directly via InternalsVisibleTo — the ten
    /// existing RunAsync(IScoreStreamCall,...) test call sites are the tested surface this
    /// plan must not disturb, so backfill priming gets its own directly-testable seam instead
    /// of being folded into RunAsync's control flow.
    ///
    /// Wrapped in a single try/catch around everything (D-15): a null history source, a null
    /// detector client, BackfillEnabled=false, a query returning zero rows, a query throwing,
    /// or WarmupAsync throwing RpcException all degrade to a silent no-op — none of them can
    /// prevent the caller (RunEntityStreamAsync) from opening the stream normally afterwards.
    /// </summary>
    internal async Task PrimeFromHistoryAsync(
        string entityId, EntityRuntimeState entityState, CancellationToken ct)
    {
        if (_historySource is null || _detectorClient is null || _connectionSettings is null)
            return;
        if (!_connectionSettings.BackfillEnabled)
            return;

        // §7 #6: the fan-out task is a single shared writer on a Wait-mode bounded channel, so a
        // slow HA does not drop readings — it stalls delivery for EVERY entity. Priming therefore
        // gets a hard per-entity deadline; the cost of a stalled Recorder is a missing prime, not
        // a pipeline that never starts.
        using var primeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        primeCts.CancelAfter(PrimeTimeout);
        var primeCt = primeCts.Token;

        try
        {
            // WS5: ask for the full rmad baseline window, never the legacy 250. min_samples (60)
            // is the readiness threshold, not the request size — priming only 60 points would
            // leave MAD estimated from 60 samples and the scale wrong for hours afterwards.
            var requestedRows = Math.Max(entityState.HstParams.Window, RmadBaselineWindow);

            var history = await _historySource.QueryHistoryAsync(
                entityId, _connectionSettings.BackfillLookback, requestedRows, primeCt);

            if (history.Count == 0)
            {
                // F11 says the startup log must carry one priming line per watched entity with
                // n > 0. Zero rows is the one outcome that produces no such line, so it has to
                // announce itself: a query that succeeded and returned nothing is an HA-side
                // visibility/permission problem (§5.3 case (e)), not "no backfill configured",
                // and the two must not look the same in the log (Rule 12).
                _logger.LogWarning(LogEvents.HistoryEmpty,
                    "No history returned for {EntityId} from {HistorySource} (lookback={Lookback}) — "
                    + "nothing to prime; the entity warms up on live readings only",
                    entityId, _historySource.SourceName, _connectionSettings.BackfillLookback);
                return;
            }

            if (history.Count < requestedRows)
            {
                // Rule 12: an entity the Recorder cannot fill the baseline window for is a real,
                // silent limit (a sensor slower than ~90 readings/day never fills 720 in 8 days) —
                // it must be visible by name, not inferred from a flag that never fires.
                _logger.LogWarning(LogEvents.HistoryShort,
                    "Only {PointCount} of {RequestedRows} history points available for {EntityId} — "
                    + "baseline window is not full; flag stays OFF until live readings top it up",
                    history.Count, requestedRows, entityId);
            }

            // The priming request must name the SAME detector the live stream will use, or the
            // registry keys the primed model under (entity, "hst") and the rmad key opens cold
            // (registry.warmup_one skips only keys with n_seen > 0).
            var request = new WarmupRequest { EntityId = entityId, Detector = entityState.DetectorName };
            foreach (var kv in BuildDetectorParamsMap(entityState))
                request.Params[kv.Key] = kv.Value;

            // WS2: seed the raw-evidence window from the same rows, but only when it is still
            // empty — the decision is hoisted out of the loop on purpose, since re-evaluating it
            // per row would seed exactly one point and leave the channel permanently abstaining.
            bool seedRaw = entityState.Alert.RawSampleCount == 0;

            // D-14: history rows are already in hand — feed the frozen detector in the same
            // loop that builds the WarmupRequest's points, in the ascending order the query
            // already returns them (BACKFILL-01/D-13).
            foreach (var row in history)
            {
                request.History.Add(new Point
                {
                    EntityId = entityId,
                    Value = row.Value,
                    Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
                        DateTime.SpecifyKind(row.Timestamp, DateTimeKind.Utc)),
                });
                entityState.FrozenDetector.AddReading(row.Value);
                if (seedRaw)
                    entityState.Alert.SeedValue(row.Value);
            }

            var response = await _detectorClient.WarmupAsync(request, primeCt);

            if (response.Skipped)
            {
                _logger.LogInformation(LogEvents.WarmupSkipped,
                    "Entity {EntityId} already primed (n_seen={NSeen}) — skipping backfill",
                    entityId, response.NSeen);
            }
            else
            {
                // F11 acceptance: this line is the receipt an operator greps for, and it has to
                // name the SOURCE. With influx_url empty, "Primed X with 720 points" reads the
                // same whether the HA Recorder answered or whether no history source was ever
                // registered — the exact ambiguity WS5 exists to remove.
                _logger.LogInformation(LogEvents.WarmupPrimed,
                    "Primed {EntityId} with {PointCount} history points from {HistorySource} "
                    + "-> n_seen={NSeen} warmedUp={WarmedUp}",
                    entityId, request.History.Count, _historySource.SourceName,
                    response.NSeen, response.WarmedUp);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Must precede the catch-all: a config Save or host shutdown cancels ct on the routine
            // path, and reporting that as WarmupFailed (5019) would drown the one signature that
            // line is supposed to carry — "HA is unreachable".
        }
        catch (Exception ex)
        {
            // D-15: backfill can never fail startup — log and let the caller open the stream
            // exactly as if no history source had been configured.
            _logger.LogWarning(LogEvents.WarmupFailed, ex,
                "Backfill priming failed for {EntityId} — proceeding with normal live warm-up", entityId);
        }
    }

    /// <summary>
    /// Shared params-map builder (Window/NTrees) so ToPoint's live-scoring params and
    /// PrimeFromHistoryAsync's backfill params can never drift apart — a mismatch would
    /// silently create two differently-configured detectors for the same entity.
    /// </summary>
    private static Dictionary<string, string> BuildHstParamsMap(HstParams hstParams)
        => new()
        {
            ["window"] = hstParams.Window.ToString(),
            ["n_trees"] = hstParams.NTrees.ToString(),
        };

    /// <summary>
    /// Wire params for whichever detector the entity is configured with.
    ///
    /// The "detector" key is what servicer.py dispatches on (params["algorithm"] then
    /// params["detector"], falling back to "hst"), so omitting it would silently score a
    /// migrated rmad entity with the old rarity detector — the exact F0 state the migration
    /// exists to leave. The numeric keys are the ones RmadDetector._read_params reads;
    /// z_scale is deliberately NOT among them (D-B: z_scale and high_threshold are the same
    /// degree of freedom, and the gate owns the threshold).
    /// </summary>
    internal static Dictionary<string, string> BuildDetectorParamsMap(EntityRuntimeState entityState)
    {
        if (entityState.RmadParams is not { } rmad)
            return BuildHstParamsMap(entityState.HstParams);

        return new Dictionary<string, string>
        {
            ["detector"] = "rmad",
            ["window"] = rmad.Window.ToString(CultureInfo.InvariantCulture),
            ["min_samples"] = rmad.MinSamples.ToString(CultureInfo.InvariantCulture),
            ["scale_floor"] = rmad.ScaleFloor.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// True when this pipeline resolves the detector from config instead of hardcoding "hst".
    ///
    /// EntitiesSchemaMigrator reads this as a sequence gate and REFUSES to write when it is
    /// false: a config migrated to rmad while the pipeline still sends window/n_trees and
    /// Detector="hst" would run the legacy detector against thresholds that mean something
    /// entirely different (0.5 on an HST rarity mass is "above the 50th percentile"), which is
    /// a worse state than not migrating at all. Flip this to false only alongside reverting
    /// BuildEntityStates.
    /// </summary>
    internal static bool SupportsRmad => true;

    private Dictionary<string, EntityRuntimeState> BuildEntityStates()
    {
        // CFG-04: read live config at RunAsync entry — captures the post-swap entity set
        // (not a ctor-captured stale reference — Pitfall 2 / RESEARCH Q1)
        var states = new Dictionary<string, EntityRuntimeState>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in _liveConfig.Get().Entities)
        {
            // WS2: alert params come from the SAME params map as the detector params (no new
            // YAML block).
            var alertParams = AlertParams.From(
                ResolveStreamingDetector(entity)?.Params ?? new Dictionary<string, string>());
            var alertPolicy = _alertStore?.GetOrCreate(entity.EntityId, alertParams);

            states[entity.EntityId] = BuildEntityState(entity, alertParams, alertPolicy);
        }

        // Config reloads rebuild this map on every Save; the store keeps each entity's rank/raw
        // calibration across those rebuilds, so untracked entities must be dropped explicitly.
        _alertStore?.PruneTo(states.Keys);
        return states;
    }

    /// <summary>
    /// WS3/D-A: the streaming detector is resolved by NAME, first match wins. The literal "hst"
    /// lookup this replaced meant a config migrated to rmad fell through to `new HstParams()`
    /// (250/0.7/0.3) — F0 restored in silence.
    ///
    /// Null when the entity names neither: a `[mad]`, `[stl]` or `[mad, stl]` entity has no
    /// streaming block at all, which is the case <see cref="BuildEntityState"/> has to answer for.
    /// </summary>
    internal static DetectorConfig? ResolveStreamingDetector(EntityConfig entity)
        => entity.Detectors.FirstOrDefault(d =>
            string.Equals(d.Name, "rmad", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.Name, "hst", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds one entity's runtime state from its config.
    ///
    /// D-H, third branch. Frozen detection is disabled through the params block
    /// (frozen_variance_threshold: 0.0) by EntitiesSchemaMigrator, which can only reach an
    /// hst or rmad block — the only kind the UI lets an operator edit. An entity with NO such
    /// block ([mad], [stl], [mad, stl]) used to land on `new HstParams()`, whose D-12 defaults
    /// are frozen_window 10 / frozen_variance_threshold 0.001: frozen live, on an entity where
    /// no key exists through which anyone could switch it off, and frozen forces the flag ON.
    /// On a fridge that is ON for the whole compressor rest. So the fallback carries frozen
    /// DEAD: it is enabled only by a params block an operator can actually see and edit.
    /// </summary>
    internal static EntityRuntimeState BuildEntityState(
        EntityConfig entity, AlertParams alertParams, AlertPolicy? alertPolicy)
    {
        var streaming = ResolveStreamingDetector(entity);

        if (streaming is null)
        {
            return new EntityRuntimeState(
                new HstParams { FrozenVarianceThreshold = 0.0 }, alertParams, alertPolicy);
        }

        return string.Equals(streaming.Name, "rmad", StringComparison.OrdinalIgnoreCase)
            ? new EntityRuntimeState(RmadParams.From(streaming.Params), alertParams, alertPolicy)
            : new EntityRuntimeState(HstParams.From(streaming.Params), alertParams, alertPolicy);
    }

    private static Point ToPoint(Ha.HaReading reading, EntityRuntimeState entityState)
    {
        var point = new Point
        {
            EntityId = reading.EntityId,
            Value = reading.Value,
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(reading.LastChanged),
        };
        foreach (var kv in BuildDetectorParamsMap(entityState))
            point.Params[kv.Key] = kv.Value;
        return point;
    }
}

/// <summary>
/// Production IScoreStreamCall adapter wrapping the real AsyncDuplexStreamingCall.
/// </summary>
internal sealed class LiveScoreStreamCall : IScoreStreamCall
{
    private readonly Grpc.Core.AsyncDuplexStreamingCall<Point, Verdict> _call;

    public LiveScoreStreamCall(Grpc.Core.AsyncDuplexStreamingCall<Point, Verdict> call)
    {
        _call = call ?? throw new ArgumentNullException(nameof(call));
    }

    public Task WriteAsync(Point point, CancellationToken ct)
        => _call.RequestStream.WriteAsync(point, ct);

    public Task CompleteAsync()
        => _call.RequestStream.CompleteAsync();

    public IAsyncEnumerable<Verdict> ReadAllVerdictsAsync(CancellationToken ct)
        => _call.ResponseStream.ReadAllAsync(ct);
}
