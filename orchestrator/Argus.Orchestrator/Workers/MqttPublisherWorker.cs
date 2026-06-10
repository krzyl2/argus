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
///
/// StatePublisher is wired after connect so Plan 08 can publish state/availability
/// through the same connection.
/// </summary>
public sealed class MqttPublisherWorker : BackgroundService
{
    private readonly MqttConnection _mqtt;
    private readonly StatePublisher _statePublisher;
    private readonly EntitiesConfig _entities;
    private readonly ILogger<MqttPublisherWorker> _logger;

    public MqttPublisherWorker(
        MqttConnection mqtt,
        StatePublisher statePublisher,
        EntitiesConfig entities,
        ILogger<MqttPublisherWorker> logger)
    {
        _mqtt = mqtt;
        _statePublisher = statePublisher;
        _entities = entities;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(LogEvents.MqttWorkerStarted, "MqttPublisherWorker starting");

        // Connect (LWT already configured; online published inside ConnectAsync)
        await _mqtt.ConnectAsync(stoppingToken);

        // Wire StatePublisher to the live connection
        _statePublisher.SetConnection(_mqtt);

        // Publish retained discovery configs for all entities (MQTT-01/03/04)
        await DiscoveryPublisher.PublishAllAsync(_mqtt, _entities.Entities, stoppingToken);
        _logger.LogInformation(LogEvents.MqttDiscoveryPublished,
            "Discovery published for {Count} entities", _entities.Entities.Count);

        // Publish initial per-entity availability "online"
        foreach (var entity in _entities.Entities)
        {
            await _statePublisher.PublishAvailabilityAsync(entity.EntityId, online: true, stoppingToken);
        }

        _logger.LogInformation(LogEvents.MqttWorkerStarted, "MqttPublisherWorker ready — discovery + availability published");

        // Keep alive until cancellation
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => Task.CompletedTask);
    }
}
