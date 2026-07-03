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
    private readonly IGroupInfluxDataSource _groupInfluxReader;
    private readonly DetectionGateway? _gateway;
    private readonly ILogger<BatchSchedulerWorker> _logger;
    private readonly IGroupStatusCache? _groupStatusCache;

    // Defaults applied when a group's Params dictionary omits these keys.
    private static readonly TimeSpan DefaultStalenessCap = TimeSpan.FromMinutes(30);
    private const string DefaultEvery = "5m";
    private const string DefaultAggFn = "mean";
    private const int PeerMinFreshMembers = 3;

    /// <summary>
    /// Test constructor — no DetectionGateway health gate (gate is skipped when gateway is null).
    /// </summary>
    public BatchSchedulerWorker(
        ConnectionSettings settings,
        IInfluxDataSource influxReader,
        IBatchDetectorClient detectorClient,
        IStatePublisher statePublisher,
        ILiveEntitiesConfig liveConfig,
        IGroupInfluxDataSource groupInfluxReader,
        ILogger<BatchSchedulerWorker> logger,
        IGroupStatusCache? groupStatusCache = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _influxReader = influxReader ?? throw new ArgumentNullException(nameof(influxReader));
        _detectorClient = detectorClient ?? throw new ArgumentNullException(nameof(detectorClient));
        _statePublisher = statePublisher ?? throw new ArgumentNullException(nameof(statePublisher));
        _liveConfig = liveConfig ?? throw new ArgumentNullException(nameof(liveConfig));
        _groupInfluxReader = groupInfluxReader ?? throw new ArgumentNullException(nameof(groupInfluxReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _groupStatusCache = groupStatusCache;
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
        IGroupInfluxDataSource groupInfluxReader,
        DetectionGateway gateway,
        ILogger<BatchSchedulerWorker> logger,
        IGroupStatusCache? groupStatusCache = null)
        : this(settings, influxReader, detectorClient, statePublisher, liveConfig, groupInfluxReader, logger, groupStatusCache)
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

        // CFG-04: read live config per-cycle so a Swap before the next tick picks up new groups
        foreach (var group in _liveConfig.Get().Groups)
        {
            try
            {
                await RunGroupBatchAsync(group, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.GroupSchedulerError, ex,
                    "Group batch failed for {GroupId}", group.GroupId);
            }
        }
    }

    // ─── Group batch loop (GRP-02/GRP-08) ───────────────────────────────────

    private async Task RunGroupBatchAsync(GroupConfig group, CancellationToken ct)
    {
        // CR-03 (defense in depth): GroupInputValidator is the authoritative
        // mode/detector consistency gate at save time, but a hand-edited
        // entities.yaml could still reach this point with a mismatch (e.g.
        // mode="joint" + detector="peer_divergence"). Scoring such a group would
        // dispatch on Mode alone and publish a fabricated verdict (proto default
        // Score=0.0/IsAnomaly=false) instead of erroring — skip the cycle instead.
        if (!Web.GroupInputValidator.IsModeDetectorConsistent(group.Mode, group.Detector))
        {
            _logger.LogError(LogEvents.GroupModeDetectorMismatch,
                "Group {GroupId} skipped — mode '{Mode}' is incompatible with detector '{Detector}'",
                group.GroupId, group.Mode, group.Detector);
            return;
        }

        var every = group.Params.TryGetValue("every", out var everyVal) && !string.IsNullOrWhiteSpace(everyVal)
            ? everyVal
            : DefaultEvery;
        var aggFn = group.Params.TryGetValue("fn", out var fnVal) && !string.IsNullOrWhiteSpace(fnVal)
            ? fnVal
            : DefaultAggFn;
        // WR-02: reject non-positive staleness_cap (e.g. "0", "-1.00:00:00", or a typo'd
        // config value) — a zero/negative cap makes (utcNow - lastSeen) > stalenessCap always
        // true, so every member is treated as stale forever, silently deadlocking group
        // scoring (JOINT never scores, PEER never reaches the fresh-member floor).
        var stalenessCap = group.Params.TryGetValue("staleness_cap", out var capVal) &&
                            TimeSpan.TryParse(capVal, out var parsedCap) && parsedCap > TimeSpan.Zero
            ? parsedCap
            : DefaultStalenessCap;

        var data = await _groupInfluxReader.QueryGroupAsync(group.Members, every, aggFn, stalenessCap, ct);

        var isPeer = string.Equals(group.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase);
        var memberSeries = BuildGroupMatrix(
            group, data, DateTime.UtcNow, stalenessCap, isPeer, out var skipWholeGroup);

        if (skipWholeGroup)
        {
            _logger.LogWarning(LogEvents.GroupSkippedStale,
                "Group {GroupId} skipped this cycle — staleness cap breach", group.GroupId);
            return;
        }

        if (memberSeries.Count == 0)
        {
            _logger.LogWarning(LogEvents.GroupNoData,
                "Group {GroupId} has no scoreable data this cycle", group.GroupId);
            return;
        }

        var request = BuildGroupScoreRequest(group, memberSeries);
        var response = await _detectorClient.ScoreGroupBatchAsync(request, ct);

        if (!response.Ok)
        {
            _logger.LogError(LogEvents.GroupSchedulerError,
                "ScoreGroupBatch returned ok=false for {GroupId}: {Error}", group.GroupId, response.Error);
            return;
        }

        if (response.PerMember.Count > 0)
        {
            foreach (var v in response.PerMember)
            {
                await _statePublisher.PublishGroupScoreAsync(group.GroupId, v.EntityId, v.Score ?? 0.0, ct);
                await _statePublisher.PublishGroupFlagAsync(group.GroupId, v.EntityId, v.IsAnomaly, ct);
            }

            _logger.LogInformation(LogEvents.GroupScored,
                "Scored group {GroupId} ({Mode}): {Count} member verdicts",
                group.GroupId, group.Mode, response.PerMember.Count);
        }
        else if (response.GroupVerdict != null)
        {
            var v = response.GroupVerdict;
            await _statePublisher.PublishGroupScoreAsync(group.GroupId, null, v.Score ?? 0.0, ct);
            await _statePublisher.PublishGroupFlagAsync(group.GroupId, null, v.IsAnomaly, ct);

            // RESEARCH Pitfall 4: response.Contributions is emitted in request.series member
            // order, NOT ranked by magnitude — sort descending before using/caching it so
            // "top contributor" and GET /api/groups/{id}/status are both honestly ranked.
            var sorted = response.Contributions.OrderByDescending(c => c.Contribution).ToList();

            _groupStatusCache?.Set(new GroupStatusEntry(
                group.GroupId,
                v.Score,
                v.IsAnomaly,
                group.Detector,
                DateTimeOffset.UtcNow,
                sorted.Select(c => new FeatureContributionDto(c.MemberId, c.Contribution)).ToList()));

            // Contributions are carried through the RPC response for HA surfacing (GRP-09) —
            // logged here at info level only, no MQTT publish this phase.
            if (sorted.Count > 0)
            {
                var top = sorted[0];
                _logger.LogInformation(LogEvents.GroupScored,
                    "Scored group {GroupId} ({Mode}): score={Score} anomaly={Anomaly} topContributor={Member}",
                    group.GroupId, group.Mode, v.Score, v.IsAnomaly, top.MemberId);
            }
            else
            {
                _logger.LogInformation(LogEvents.GroupScored,
                    "Scored group {GroupId} ({Mode}): score={Score} anomaly={Anomaly}",
                    group.GroupId, group.Mode, v.Score, v.IsAnomaly);
            }
        }
        else if (!string.IsNullOrEmpty(response.Error))
        {
            // WR-01: GRP-04 below-floor classic peer_divergence responses carry neither
            // PerMember entries nor a GroupVerdict (Ok=true, Error set) — without this
            // branch the cycle produced no log output at all, contradicting the project's
            // "fail loud" convention and removing the only per-cycle "why isn't my group
            // scoring" signal an operator had.
            _logger.LogInformation(LogEvents.GroupScored,
                "Group {GroupId} ({Mode}) produced no verdict this cycle: {Error}",
                group.GroupId, group.Mode, response.Error);
        }
    }

    /// <summary>
    /// Applies the wall-clock staleness_cap exclusion policy and the null-cell (rectangular-matrix)
    /// guard to a group's aligned data, producing a per-member value list ready for the group RPC.
    ///
    /// JOINT: any member breaching the staleness cap skips the whole group this cycle (fixed feature
    /// vector — a joint model's fitted dimensionality can't tolerate a dropped column).
    /// PEER: stale members are dropped from the active set; if fewer than the minimum floor of fresh
    /// members remain, the group is skipped this cycle instead of being scored short-handed.
    /// Null-valued cells (missing pivot data — a genuine gap, never forward-filled) are excluded
    /// row-by-row so the matrix passed to the detector stays rectangular.
    /// </summary>
    private static Dictionary<string, List<double>> BuildGroupMatrix(
        GroupConfig group,
        GroupAlignedData data,
        DateTime utcNow,
        TimeSpan stalenessCap,
        bool isPeer,
        out bool skipWholeGroup)
    {
        var staleMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in group.Members)
        {
            var isStale = !data.LastSeenUtc.TryGetValue(member, out var lastSeen) ||
                          (utcNow - lastSeen) > stalenessCap;
            if (isStale) staleMembers.Add(member);
        }

        if ((!isPeer || group.Members.Count < 3) && staleMembers.Count > 0)
        {
            skipWholeGroup = true;
            return new Dictionary<string, List<double>>();
        }

        var activeMembers = group.Members.Where(m => !staleMembers.Contains(m)).ToList();

        // Gated on group.Members.Count >= 3: for a 2-member peer group, activeMembers.Count can
        // never reach PeerMinFreshMembers (3) even with zero staleness, so this floor check must
        // stay scoped to N>=3 peer groups — the same "unreachable for N==2" contract
        // PeerDivergenceDetector's own internal floor has (Rule 1 fix vs. plan text: the first
        // guard above only fires when staleMembers.Count > 0, so a fully-fresh 2-member group
        // must not also be caught here).
        if (isPeer && group.Members.Count >= 3 && activeMembers.Count < PeerMinFreshMembers)
        {
            skipWholeGroup = true;
            return new Dictionary<string, List<double>>();
        }

        var memberSeries = activeMembers.ToDictionary(m => m, _ => new List<double>(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in data.Rows)
        {
            // Rectangular guard: exclude the row if any active member's cell is a genuine gap (null).
            var hasGap = activeMembers.Any(m =>
                !row.MemberValues.TryGetValue(m, out var v) || v is null);
            if (hasGap) continue;

            foreach (var m in activeMembers)
                memberSeries[m].Add(row.MemberValues[m]!.Value);
        }

        skipWholeGroup = false;
        return memberSeries;
    }

    private static GroupScoreRequest BuildGroupScoreRequest(
        GroupConfig group,
        IReadOnlyDictionary<string, List<double>> memberSeries)
    {
        var request = new GroupScoreRequest
        {
            GroupId = group.GroupId,
            Detector = group.Detector,
        };

        foreach (var (key, value) in group.Params)
            request.Params[key] = value;

        foreach (var (memberId, values) in memberSeries)
        {
            var series = new Series { MemberId = memberId };
            series.Values.AddRange(values);
            request.Series.Add(series);
        }

        return request;
    }

    private static FitGroupRequest BuildFitGroupRequest(
        GroupConfig group,
        IReadOnlyDictionary<string, List<double>> memberSeries)
    {
        var request = new FitGroupRequest
        {
            GroupId = group.GroupId,
            Detector = group.Detector,
        };

        foreach (var (key, value) in group.Params)
            request.Params[key] = value;

        foreach (var (memberId, values) in memberSeries)
        {
            var series = new Series { MemberId = memberId };
            series.Values.AddRange(values);
            request.Series.Add(series);
        }

        return request;
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

        // CFG-04: read live config per-cycle so nightly fit uses the current group set.
        // RunGroupFitAsync is called for every group; Python's FitGroup decides fit semantics
        // by member count (no-op for N>=3 peer_divergence, actual fit for 2-member pairwise-delta).
        foreach (var group in _liveConfig.Get().Groups)
        {
            try
            {
                await RunGroupFitAsync(group, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.GroupSchedulerError, ex,
                    "Group nightly fit failed for {GroupId}", group.GroupId);
            }
        }

        _logger.LogInformation(LogEvents.NightlyFitCompleted, "Nightly fit completed");
    }

    private async Task RunGroupFitAsync(GroupConfig group, CancellationToken ct)
    {
        var every = group.Params.TryGetValue("every", out var everyVal) && !string.IsNullOrWhiteSpace(everyVal)
            ? everyVal
            : DefaultEvery;
        var aggFn = group.Params.TryGetValue("fn", out var fnVal) && !string.IsNullOrWhiteSpace(fnVal)
            ? fnVal
            : DefaultAggFn;
        // WR-02: reject non-positive staleness_cap — see RunGroupBatchAsync for rationale.
        var stalenessCap = group.Params.TryGetValue("staleness_cap", out var capVal) &&
                            TimeSpan.TryParse(capVal, out var parsedCap) && parsedCap > TimeSpan.Zero
            ? parsedCap
            : DefaultStalenessCap;

        var data = await _groupInfluxReader.QueryGroupAsync(group.Members, every, aggFn, stalenessCap, ct);

        // Joint fit: any stale member excludes the whole group's rows this cycle (fixed feature vector).
        var memberSeries = BuildGroupMatrix(
            group, data, DateTime.UtcNow, stalenessCap, isPeer: false, out var skipWholeGroup);

        if (skipWholeGroup || memberSeries.Count == 0 || memberSeries.Values.All(v => v.Count == 0))
        {
            _logger.LogWarning(LogEvents.GroupNoData,
                "Group {GroupId} nightly fit skipped — no scoreable data", group.GroupId);
            return;
        }

        var request = BuildFitGroupRequest(group, memberSeries);
        var response = await _detectorClient.FitGroupAsync(request, ct);

        if (!response.Ok)
        {
            _logger.LogError(LogEvents.GroupSchedulerError,
                "FitGroup returned ok=false for {GroupId}: {Error}", group.GroupId, response.Error);
        }
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
