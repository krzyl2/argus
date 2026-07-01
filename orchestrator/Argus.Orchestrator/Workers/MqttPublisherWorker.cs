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
            _ = Task.Run(async () =>
            {
                try
                {
                    var entities = _liveConfig.Get().Entities;
                    await DiscoveryPublisher.PublishAllAsync(_mqtt, entities, _stoppingToken);

                    foreach (var entity in entities)
                    {
                        await _statePublisher.PublishAvailabilityAsync(
                            entity.EntityId, online: true, _stoppingToken);
                    }

                    _logger.LogInformation(LogEvents.MqttDiscoveryPublished,
                        "ConfigChanged: republished discovery + availability for {Count} entities", entities.Count);
                }
                catch (OperationCanceledException)
                {
                    // Host shutting down — normal, do not log as error
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ConfigChanged republish failed");
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

            _logger.LogInformation(LogEvents.MqttWorkerReady, "MqttPublisherWorker ready — discovery + availability published");

            // Keep alive until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => Task.CompletedTask);
        }
        finally
        {
            _liveConfig.ConfigChanged -= OnConfigChanged;
        }
    }
}
