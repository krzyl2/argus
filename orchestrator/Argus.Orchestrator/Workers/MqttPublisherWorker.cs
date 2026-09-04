using Argus.Orchestrator.Config;
using Argus.Orchestrator.Logging;
using Argus.Orchestrator.Mqtt;

namespace Argus.Orchestrator.Workers;

/// <summary>
/// BackgroundService that manages the MQTT lifecycle:
/// 1. Connects MqttConnection (LWT already configured in ctor — PITFALL 6).
/// 2. Publishes bridge "online" (done by ConnectAsync).
/// 3. Publishes retained discovery configs for all configured entities (MQTT-01/03).
/// 4. Publishes initial per-entity availability "online".
/// 5. On ConfigChanged: republishes discovery AND availability for current entities so
///    newly-added entities get HA discovery immediately (not shown "unavailable" until
///    pipeline warm-up — RESEARCH Q8 / Pitfall 4).
///
/// StatePublisher is wired after connect so Plan 08 can publish state/availability
/// through the same connection.
/// </summary>
public sealed class MqttPublisherWorker : BackgroundService
{
    private readonly MqttConnection _mqtt;
    private readonly StatePublisher _statePublisher;
    private readonly ILiveEntitiesConfig _liveConfig;
    private readonly ILogger<MqttPublisherWorker> _logger;
    private readonly LegacyDiscoveryRetraction _legacyRetraction;

    // Stored stoppingToken used in the ConfigChanged fire-and-forget handler.
    // Set at ExecuteAsync entry before the ConfigChanged subscription.
    private CancellationToken _stoppingToken;

    // Snapshot of the last-published group set, used to diff removed members on ConfigChanged
    // (GRP-08 — retract removed members BEFORE republishing the new set). Read-modify-write of
    // this field plus the retract/publish pass is serialized by _configChangeGate (CR-01) so
    // that two rapid ConfigChanged events cannot race on a stale snapshot.
    private IReadOnlyList<GroupConfig> _lastGroups = System.Array.Empty<GroupConfig>();

    // Serializes OnConfigChanged task bodies (CR-01): without this, two fire-and-forget
    // Task.Run calls triggered by rapid successive config saves could both read the same
    // stale _lastGroups, compute the diff against it, and race on the final write — causing
    // a missed or incorrect group member retraction (orphaned HA entity). Mirrors the
    // _connectGate idiom in MqttConnection.
    private readonly SemaphoreSlim _configChangeGate = new(1, 1);

    public MqttPublisherWorker(
        MqttConnection mqtt,
        StatePublisher statePublisher,
        ILiveEntitiesConfig liveConfig,
        ILogger<MqttPublisherWorker> logger,
        LegacyDiscoveryRetraction? legacyRetraction = null)
    {
        _mqtt = mqtt ?? throw new ArgumentNullException(nameof(mqtt));
        _statePublisher = statePublisher ?? throw new ArgumentNullException(nameof(statePublisher));
        _liveConfig = liveConfig ?? throw new ArgumentNullException(nameof(liveConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Optional so the existing construction sites keep compiling; "none pending" is the
        // correct default on every boot except the first one after a schema-2 migration.
        _legacyRetraction = legacyRetraction ?? LegacyDiscoveryRetraction.None;
    }

    /// <summary>
    /// Runs the D-G retraction as a step that can FAIL WITHOUT TAKING THE ADD-ON WITH IT.
    ///
    /// The obligation is durable now (LegacyDiscoveryRetraction resolves it from disk), which
    /// turns what used to be a one-boot annoyance into a permanent one: an exception escaping
    /// here stops the host (BackgroundServiceExceptionBehavior.StopHost), and because no marker
    /// was written the next boot reaches the very same throw — start, crash, start, with
    /// PublishAllAsync below never running and NO discovery in HA at all. A missing retraction
    /// costs one duplicate entity; a boot loop costs every entity.
    ///
    /// So a failure is logged and the boot continues. The marker is still unwritten, so the
    /// deletions stay owed and are retried on the next start — the retry channel is the boot,
    /// not the crash.
    /// </summary>
    internal static async Task<bool> RunLegacyRetractionAsync(
        LegacyDiscoveryRetraction retraction,
        Func<IReadOnlyList<EntityConfig>, CancellationToken, Task<bool>> retract,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            return await retraction.RunAsync(retract, logger, ct);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down — a clean stop, not a failure. Still no marker: next boot retries.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(LogEvents.MqttPublishDropped, ex,
                "Legacy detector-scoped discovery retraction FAILED — discovery publishing "
                + "continues, and the retraction is retried on the next start (no marker was "
                + "written). Until then HA may show a duplicate, orphaned entity per sensor");
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        _logger.LogInformation(LogEvents.MqttWorkerStarted, "MqttPublisherWorker starting");

        // Connect (LWT already configured; online published inside ConnectAsync)
        await _mqtt.ConnectAsync(stoppingToken);

        // Wire StatePublisher to the live connection
        _statePublisher.SetConnection(_mqtt);

        // Subscribe to ConfigChanged before publishing so we don't miss a rapid reload
        // immediately after the first publish. Unsubscribe in finally.
        void OnConfigChanged(object? sender, EventArgs e)
        {
            // Fire-and-forget: republish discovery + availability for the current entity set
            // so newly-added entities get HA discovery immediately (idempotent — retain=true).
            // Uses stored _stoppingToken (host lifetime) for broker call cancellation.
            // Ordering (GRP-08): retract removed group members FIRST, then republish entities
            // (existing), then republish the current group set, then update the snapshot.
            // The whole body is serialized by _configChangeGate (CR-01) so that two rapid
            // config changes cannot both read the same stale _lastGroups and race on the
            // diff-then-write, which could miss or duplicate a group member retraction.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _configChangeGate.WaitAsync(_stoppingToken);
                    try
                    {
                        // Retract removed group members before anything else is republished.
                        var newGroups = _liveConfig.Get().Groups;
                        var newGroupsById = newGroups.ToDictionary(g => g.GroupId, StringComparer.OrdinalIgnoreCase);

                        foreach (var oldGroup in _lastGroups)
                        {
                            // CR-02: decision logic (what to retract) lives in the pure,
                            // unit-testable DiscoveryPublisher.ComputeRetractionEntities —
                            // handles both the member-list diff and the shape-transition
                            // (2/3+-member boundary) case; this loop only performs the I/O.
                            newGroupsById.TryGetValue(oldGroup.GroupId, out var newGroup);
                            var toRetract = DiscoveryPublisher.ComputeRetractionEntities(oldGroup, newGroup);
                            if (toRetract is not null)
                                await DiscoveryPublisher.RetractGroupAsync(_mqtt, oldGroup, toRetract, _stoppingToken);
                        }

                        var entities = _liveConfig.Get().Entities;
                        await DiscoveryPublisher.PublishAllAsync(_mqtt, entities, _stoppingToken);

                        foreach (var entity in entities)
                        {
                            await _statePublisher.PublishAvailabilityAsync(
                                entity.EntityId, online: true, _stoppingToken);
                        }

                        foreach (var group in newGroups)
                            await DiscoveryPublisher.PublishGroupAsync(_mqtt, group, _stoppingToken);

                        _lastGroups = newGroups;

                        _logger.LogInformation(LogEvents.MqttDiscoveryPublished,
                            "ConfigChanged: republished discovery + availability for {Count} entities, {GroupCount} groups",
                            entities.Count, newGroups.Count);
                    }
                    finally
                    {
                        _configChangeGate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Host shutting down — normal, do not log as error
                }
                catch (Exception ex)
                {
                    _logger.LogError(LogEvents.GroupSchedulerError, ex, "ConfigChanged republish failed");
                }
            });
        }

        _liveConfig.ConfigChanged += OnConfigChanged;

        try
        {
            // D-G: retract the PRE-migration, detector-scoped retained discovery configs FIRST.
            // Order matters: the new configs published below carry detector-agnostic ids, so
            // doing this afterwards would be racing the broker to delete a config HA may already
            // have turned into a duplicate entity on the same state topic.
            //
            // RunAsync owns the durable marker: it is written only after the broker accepted the
            // deletions, so a broker that is down here means we try again next boot instead of
            // leaving the old retained configs — and the orphaned HA entities they create — behind
            // forever.
            await RunLegacyRetractionAsync(
                _legacyRetraction,
                (entities, ct) => DiscoveryPublisher.RetractLegacyDetectorScopedAsync(_mqtt, entities, ct),
                _logger,
                stoppingToken);

            // Publish retained discovery configs for all entities (MQTT-01/03/04)
            await DiscoveryPublisher.PublishAllAsync(_mqtt, _liveConfig.Get().Entities, stoppingToken);
            _logger.LogInformation(LogEvents.MqttDiscoveryPublished,
                "Discovery published for {Count} entities", _liveConfig.Get().Entities.Count);

            // Publish initial per-entity availability "online"
            foreach (var entity in _liveConfig.Get().Entities)
            {
                await _statePublisher.PublishAvailabilityAsync(entity.EntityId, online: true, stoppingToken);
            }

            // Publish retained discovery configs for the current group set (GRP-08)
            var initialGroups = _liveConfig.Get().Groups;
            foreach (var group in initialGroups)
                await DiscoveryPublisher.PublishGroupAsync(_mqtt, group, stoppingToken);
            _lastGroups = initialGroups;

            _logger.LogInformation(LogEvents.MqttWorkerReady, "MqttPublisherWorker ready — discovery + availability published");

            // Keep alive until cancellation — TaskCanceledException on stop; finally still runs
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { /* normal host shutdown — do not rethrow */ }
        }
        finally
        {
            _liveConfig.ConfigChanged -= OnConfigChanged;
        }
    }
}
