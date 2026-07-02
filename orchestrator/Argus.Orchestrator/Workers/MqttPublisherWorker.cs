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

    // Stored stoppingToken used in the ConfigChanged fire-and-forget handler.
    // Set at ExecuteAsync entry before the ConfigChanged subscription.
    private CancellationToken _stoppingToken;

    // Snapshot of the last-published group set, used to diff removed members on ConfigChanged
    // (GRP-08 — retract removed members BEFORE republishing the new set). Updated only at the
    // end of each publish pass; the worker is single-threaded enough (ExecuteAsync +
    // sequential fire-and-forget handler body) for this to be a safe diff basis.
    private IReadOnlyList<GroupConfig> _lastGroups = System.Array.Empty<GroupConfig>();

    public MqttPublisherWorker(
        MqttConnection mqtt,
        StatePublisher statePublisher,
        ILiveEntitiesConfig liveConfig,
        ILogger<MqttPublisherWorker> logger)
    {
        _mqtt = mqtt ?? throw new ArgumentNullException(nameof(mqtt));
        _statePublisher = statePublisher ?? throw new ArgumentNullException(nameof(statePublisher));
        _liveConfig = liveConfig ?? throw new ArgumentNullException(nameof(liveConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            _ = Task.Run(async () =>
            {
                try
                {
                    // Retract removed group members before anything else is republished.
                    var newGroups = _liveConfig.Get().Groups;
                    var newGroupsById = newGroups.ToDictionary(g => g.GroupId, StringComparer.OrdinalIgnoreCase);

                    foreach (var oldGroup in _lastGroups)
                    {
                        if (newGroupsById.TryGetValue(oldGroup.GroupId, out var newGroup))
                        {
                            var isPeer = string.Equals(oldGroup.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase);
                            if (!isPeer) continue; // joint groups have no per-member diff

                            var removed = oldGroup.Members
                                .Except(newGroup.Members, StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            if (removed.Count > 0)
                                await DiscoveryPublisher.RetractGroupAsync(_mqtt, oldGroup, removed, _stoppingToken);
                        }
                        else
                        {
                            // Whole group_id removed — retract all of it.
                            var isPeer = string.Equals(oldGroup.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase);
                            IEnumerable<string?> removedAll = isPeer
                                ? oldGroup.Members.Cast<string?>()
                                : [null];
                            await DiscoveryPublisher.RetractGroupAsync(_mqtt, oldGroup, removedAll, _stoppingToken);
                        }
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
