using Argus.Detector.V1;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Logging;
using Argus.Orchestrator.Mqtt;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Note: google.protobuf.DoubleValue fields are generated as double? in C# (nullable double),
// not as DoubleValue wrapper classes. Timestamp fields remain as Timestamp.

namespace Argus.Orchestrator.Batch;

/// <summary>
/// BackgroundService that drives periodic batch scoring (BTCH-03) and nightly model retraining.
///
/// Per tick:
///   1. Queries InfluxDB for each entity (BTCH-01).
///   2. Calls ScoreBatchAsync per entity/detector (BTCH-02/BTCH-04).
///   3. Publishes last verdict via IStatePublisher.
///
/// Nightly fit:
///   Runs once per day when DateTime.Now.Hour == NightlyFitHour.
///   Python Fit RPC saves the model internally — no explicit SaveModel call (per plan).
///
/// Fault isolation (T-02-04-04):
///   Per-entity exceptions are caught and logged; worker never dies from a single entity failure.
///   OperationCanceledException always rethrown for clean shutdown.
/// </summary>
public sealed class BatchSchedulerWorker : BackgroundService
{
    private readonly ConnectionSettings _settings;
    private readonly IInfluxDataSource _influxReader;
    private readonly IBatchDetectorClient _detectorClient;
    private readonly IStatePublisher _statePublisher;
    private readonly ILiveEntitiesConfig _liveConfig;
    private readonly DetectionGateway? _gateway;
    private readonly ILogger<BatchSchedulerWorker> _logger;

    /// <summary>
    /// Test constructor — no DetectionGateway health gate (gate is skipped when gateway is null).
    /// </summary>
    public BatchSchedulerWorker(
        ConnectionSettings settings,
        IInfluxDataSource influxReader,
        IBatchDetectorClient detectorClient,
        IStatePublisher statePublisher,
        ILiveEntitiesConfig liveConfig,
        ILogger<BatchSchedulerWorker> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _influxReader = influxReader ?? throw new ArgumentNullException(nameof(influxReader));
        _detectorClient = detectorClient ?? throw new ArgumentNullException(nameof(detectorClient));
        _statePublisher = statePublisher ?? throw new ArgumentNullException(nameof(statePublisher));
        _liveConfig = liveConfig ?? throw new ArgumentNullException(nameof(liveConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Production constructor — includes DetectionGateway for INFRA-07 health gate.
    /// </summary>
    public BatchSchedulerWorker(
        ConnectionSettings settings,
        IInfluxDataSource influxReader,
        IBatchDetectorClient detectorClient,
        IStatePublisher statePublisher,
        ILiveEntitiesConfig liveConfig,
        DetectionGateway gateway,
        ILogger<BatchSchedulerWorker> logger)
        : this(settings, influxReader, detectorClient, statePublisher, liveConfig, logger)
    {
        _gateway = gateway;
    }

    // ─── BackgroundService ───────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // INFRA-07: gate on detector health before starting the batch loop
        if (_gateway is not null)
        {
            await _gateway.WaitForHealthyAsync(stoppingToken);
            if (stoppingToken.IsCancellationRequested) return;
        }

        _logger.LogInformation(LogEvents.BatchSchedulerStarted,
            "BatchSchedulerWorker starting — interval {Minutes}min", _settings.BatchIntervalMinutes);

        bool fitRunToday = false;
        int lastFitHour = -1;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_settings.BatchIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunBatchAsync(stoppingToken);

                int nowHour = DateTime.Now.Hour;

                // Reset daily flag when the hour changes (not just at NightlyFitHour)
                if (nowHour != lastFitHour)
                {
                    fitRunToday = false;
                    lastFitHour = nowHour;
                }

                if (nowHour == _settings.NightlyFitHour && !fitRunToday)
                {
                    await RunNightlyFitAsync(stoppingToken);
                    fitRunToday = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.BatchSchedulerError, ex, "Batch tick failed unexpectedly");
            }
        }
    }

    // ─── Core batch loop ─────────────────────────────────────────────────────

    internal async Task RunBatchAsync(CancellationToken ct)
    {
        // CFG-04: read live config per-cycle so a Swap before the next tick picks up new entities
        foreach (var entity in _liveConfig.Get().Entities)
        {
            foreach (var detectorCfg in entity.Detectors)
            {
                try
                {
                    await RunEntityBatchAsync(entity.EntityId, detectorCfg, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(LogEvents.BatchSchedulerError, ex,
                        "Batch failed for entity {EntityId} detector {Detector}",
                        entity.EntityId, detectorCfg.Name);
                }
            }
        }
    }

    private async Task RunEntityBatchAsync(string entityId, DetectorConfig detectorCfg, CancellationToken ct)
    {
        var points = await _influxReader.QueryAsync(entityId, ct);

        if (points.Count == 0)
        {
            _logger.LogWarning(LogEvents.BatchEntityNoData,
                "No readings for {EntityId} — skipping batch", entityId);
            return;
        }

        var request = BuildScoreBatchRequest(entityId, detectorCfg, points);
        var response = await _detectorClient.ScoreBatchAsync(request, ct);

        if (!response.Ok)
        {
            _logger.LogError(LogEvents.BatchSchedulerError,
                "ScoreBatch returned ok=false for {EntityId}/{Detector}: {Error}",
                entityId, detectorCfg.Name, response.Error);
            return;
        }

        // Publish only the last verdict (most recent point — window is sorted ascending)
        if (response.Verdicts.Count > 0)
        {
            var last = response.Verdicts[^1];
            // Score is double? (google.protobuf.DoubleValue -> C# double?)
            await _statePublisher.PublishScoreAsync(entityId, last.Score ?? 0.0, ct);
            await _statePublisher.PublishFlagAsync(entityId, last.IsAnomaly, ct);

            _logger.LogInformation(LogEvents.BatchScoredEntity,
                "Scored {EntityId}/{Detector}: score={Score} anomaly={Anomaly}",
                entityId, detectorCfg.Name, last.Score, last.IsAnomaly);
        }
    }

    private static ScoreBatchRequest BuildScoreBatchRequest(
        string entityId,
        DetectorConfig detectorCfg,
        IReadOnlyList<(DateTime Timestamp, double Value)> points)
    {
        var request = new ScoreBatchRequest
        {
            EntityId = entityId,
            Detector = detectorCfg.Name,
        };

        foreach (var (key, value) in detectorCfg.Params)
            request.Params[key] = value;

        foreach (var (ts, val) in points)
        {
            request.Window.Add(new Point
            {
                EntityId = entityId,
                Value = val,
                Timestamp = Timestamp.FromDateTime(ts.ToUniversalTime()),
            });
        }

        return request;
    }

    // ─── Nightly fit ─────────────────────────────────────────────────────────

    internal async Task RunNightlyFitAsync(CancellationToken ct)
    {
        _logger.LogInformation(LogEvents.NightlyFitStarted, "Nightly fit started");

        // CFG-04: read live config per-cycle so nightly fit uses the current entity set
        foreach (var entity in _liveConfig.Get().Entities)
        {
            foreach (var detectorCfg in entity.Detectors)
            {
                try
                {
                    var points = await _influxReader.QueryAsync(entity.EntityId, ct);

                    if (points.Count == 0)
                    {
                        _logger.LogWarning(LogEvents.BatchEntityNoData,
                            "No data for nightly fit: {EntityId}/{Detector}",
                            entity.EntityId, detectorCfg.Name);
                        continue;
                    }

                    var request = BuildFitRequest(entity.EntityId, detectorCfg, points);
                    var response = await _detectorClient.FitAsync(request, ct);

                    if (!response.Ok)
                    {
                        _logger.LogError(LogEvents.BatchSchedulerError,
                            "Nightly fit returned ok=false for {EntityId}/{Detector}: {Error}",
                            entity.EntityId, detectorCfg.Name, response.Error);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(LogEvents.BatchSchedulerError, ex,
                        "Nightly fit failed for {EntityId}/{Detector}",
                        entity.EntityId, detectorCfg.Name);
                }
            }
        }

        _logger.LogInformation(LogEvents.NightlyFitCompleted, "Nightly fit completed");
    }

    private static FitRequest BuildFitRequest(
        string entityId,
        DetectorConfig detectorCfg,
        IReadOnlyList<(DateTime Timestamp, double Value)> points)
    {
        var request = new FitRequest
        {
            EntityId = entityId,
            Detector = detectorCfg.Name,
        };

        foreach (var (key, value) in detectorCfg.Params)
            request.Params[key] = value;

        foreach (var (ts, val) in points)
        {
            request.Window.Add(new Point
            {
                EntityId = entityId,
                Value = val,
                Timestamp = Timestamp.FromDateTime(ts.ToUniversalTime()),
            });
        }

        return request;
    }

    // ─── Test helpers (internal — accessible via InternalsVisibleTo) ──────────

    /// <summary>Exposes RunBatchAsync for unit tests without the timer loop.</summary>
    internal Task RunBatchForTestAsync(CancellationToken ct) => RunBatchAsync(ct);

    /// <summary>Exposes RunNightlyFitAsync for unit tests without the timer loop.</summary>
    internal Task RunNightlyFitForTestAsync(CancellationToken ct) => RunNightlyFitAsync(ct);

    /// <summary>
    /// Simulates multiple timer ticks at specified hours to verify _fitRunToday flag behavior.
    /// Returns the number of times RunNightlyFitAsync was actually called.
    /// </summary>
    internal async Task<int> SimulateNightlyFitTicksAsync(
        int nightlyFitHour,
        int[] tickHours,
        CancellationToken ct)
    {
        bool fitRunToday = false;
        int lastFitHour = -1;
        int fitCount = 0;

        foreach (var nowHour in tickHours)
        {
            // Reset daily flag when the hour changes
            if (nowHour != lastFitHour)
            {
                fitRunToday = false;
                lastFitHour = nowHour;
            }

            if (nowHour == nightlyFitHour && !fitRunToday)
            {
                await RunNightlyFitAsync(ct);
                fitRunToday = true;
                fitCount++;
            }
        }

        return fitCount;
    }
}
