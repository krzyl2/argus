using Argus.Detector.V1;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Logging;
using Argus.Orchestrator.Mqtt;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

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
/// Score is always published (allows dashboards to show raw scores during warm-up).
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

    /// <summary>
    /// Production constructor — includes DetectionGateway for opening live streams.
    /// </summary>
    public ScoreStreamPipeline(
        IStatePublisher publisher,
        ILogger<ScoreStreamPipeline> logger,
        ILiveEntitiesConfig liveConfig,
        DetectionGateway gateway)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _liveConfig = liveConfig ?? throw new ArgumentNullException(nameof(liveConfig));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    /// <summary>
    /// Test constructor — no DetectionGateway (tests inject IScoreStreamCall directly via RunAsync overload).
    /// </summary>
    public ScoreStreamPipeline(
        IStatePublisher publisher,
        ILogger<ScoreStreamPipeline> logger,
        ILiveEntitiesConfig liveConfig)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _liveConfig = liveConfig ?? throw new ArgumentNullException(nameof(liveConfig));
        _gateway = null;
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
            entityState.RecordReading();
            entityState.SuppressBinarySensor = reading.SuppressBinarySensor;

            if (entityState.FrozenDetector.IsFrozen)
            {
                _logger.LogWarning(
                    "Entity {EntityId} is frozen (variance < threshold) — publishing frozen flag",
                    reading.EntityId);
                await PublishFrozenAsync(reading.EntityId, entityState, ct);
                // Still forward to detector for model continuity (HST keeps learning)
            }

            var point = ToPoint(reading);
            await call.WriteAsync(point, ct);
        }

        // PITFALL 3: CompleteAsync BEFORE await readTask (never reverse — deadlock)
        await call.CompleteAsync();
        await readTask;
    }

    /// <summary>
    /// Processes a single verdict: publishes score always; publishes binary_sensor flag
    /// only when not suppressed and warmed up (PITFALL 8/D-07).
    /// Applies hysteresis gate (STRM-05) before flag publish.
    /// Logs per-verdict latency (OBS-01/STRM-04).
    /// </summary>
    public async Task ProcessVerdictAsync(
        Ha.HaReading reading,
        Verdict verdict,
        EntityRuntimeState entityState,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        double score = verdict.Score ?? 0.0;

        // Always publish score (raw metric visible even during warm-up)
        await _publisher.PublishScoreAsync(reading.EntityId, score, ct);

        // Apply hysteresis gate
        bool isAnomalous = entityState.Hysteresis.Apply(score);

        // Publish binary_sensor flag only when not suppressed and HST has warmed up (PITFALL 8/D-07)
        bool canPublishFlag = !reading.SuppressBinarySensor && entityState.WarmedUp;
        if (canPublishFlag)
        {
            await _publisher.PublishFlagAsync(reading.EntityId, isAnomalous, ct);
            entityState.LastPublishedFlag = isAnomalous;
        }

        // OBS-01/STRM-04: structured per-verdict latency log
        var latencyMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        _logger.LogDebug(
            "Verdict: entity={EntityId} score={Score:F4} anomalous={IsAnomalous} flagPublished={FlagPublished} latency_ms={LatencyMs:F1}",
            reading.EntityId, score, isAnomalous, canPublishFlag, latencyMs);
    }

    /// <summary>
    /// Publishes a frozen sensor detection result: binary_sensor ON + availability online (FAULT-02).
    /// Called when FrozenSensorDetector.IsFrozen for an entity's reading.
    /// </summary>
    public async Task PublishFrozenAsync(string entityId, EntityRuntimeState entityState, CancellationToken ct)
    {
        // Frozen is an anomaly — force binary_sensor ON regardless of warm-up/suppression
        await _publisher.PublishFlagAsync(entityId, on: true, ct);
        entityState.LastPublishedFlag = true;

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

    private async Task RunEntityStreamAsync(
        string entityId,
        EntityRuntimeState entityState,
        IAsyncEnumerable<HaReading> readings,
        CancellationToken ct)
    {
        try
        {
            var call = new LiveScoreStreamCall(_gateway!.DetectorClient.ScoreStream(cancellationToken: ct));
            await RunAsync(call, entityId, readings, entityState, ct);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            await HandleDetectorFailureAsync(entityId, ct);
            _logger.LogError(ex,
                "ScoreStream RpcException for {EntityId}: {Status} — entity marked offline",
                entityId, ex.Status);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — do not log as error
        }
    }

    private Dictionary<string, EntityRuntimeState> BuildEntityStates()
    {
        // CFG-04: read live config at RunAsync entry — captures the post-swap entity set
        // (not a ctor-captured stale reference — Pitfall 2 / RESEARCH Q1)
        var states = new Dictionary<string, EntityRuntimeState>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in _liveConfig.Get().Entities)
        {
            var hstDetector = entity.Detectors.FirstOrDefault(d =>
                string.Equals(d.Name, "hst", StringComparison.OrdinalIgnoreCase));
            var hstParams = hstDetector is not null
                ? HstParams.From(hstDetector.Params)
                : new HstParams();
            states[entity.EntityId] = new EntityRuntimeState(hstParams);
        }
        return states;
    }

    private static Point ToPoint(Ha.HaReading reading)
        => new Point
        {
            EntityId = reading.EntityId,
            Value = reading.Value,
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(reading.LastChanged),
        };
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
