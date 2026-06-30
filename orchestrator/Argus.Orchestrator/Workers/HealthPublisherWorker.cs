using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Health;
using Argus.Orchestrator.Logging;
using Argus.Orchestrator.Mqtt;
using GrpcHealth = Grpc.Health.V1;

namespace Argus.Orchestrator.Workers;

/// <summary>
/// BackgroundService that publishes the Argus composite health entity (HEALTH-01).
///
/// Lifecycle:
///   1. Waits for MQTT to be connected (polling, short delay).
///   2. Publishes the health binary_sensor discovery config once (retained).
///   3. Loops every ~15 seconds: evaluates composite health and publishes state.
///
/// Composite health: OFF (healthy) only when:
///   - detector gRPC is SERVING (CheckAsync with 5s deadline)
///   - HA WebSocket is connected (ArgusHealthSignals.HaConnected)
///   - MQTT broker is connected (MqttConnection.IsConnected)
/// ON = problem; OFF = healthy (device_class "problem" semantics).
///
/// T-03-08 mitigation: fixed 15s interval with short Check deadline; failures
/// are caught and treated as not-SERVING, no tight retry.
/// </summary>
public sealed class HealthPublisherWorker : BackgroundService
{
    private readonly MqttConnection _mqtt;
    private readonly DetectionGateway _gateway;
    private readonly ArgusHealthSignals _signals;
    private readonly ILogger<HealthPublisherWorker> _logger;

    public HealthPublisherWorker(
        MqttConnection mqtt,
        DetectionGateway gateway,
        ArgusHealthSignals signals,
        ILogger<HealthPublisherWorker> logger)
    {
        _mqtt = mqtt ?? throw new ArgumentNullException(nameof(mqtt));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for MQTT to establish its initial connection
        while (!_mqtt.IsConnected && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
        stoppingToken.ThrowIfCancellationRequested();

        // Publish health discovery config once (retained) — HA creates the entity
        await _mqtt.PublishAsync(
            DiscoveryPublisher.HealthDiscoveryTopic,
            DiscoveryPublisher.BuildHealthBinarySensorConfig(),
            retain: true,
            stoppingToken);

        _logger.LogInformation(LogEvents.HealthEntityPublished,
            "Health discovery published to {Topic}", DiscoveryPublisher.HealthDiscoveryTopic);

        // Periodic health state loop (~15s interval, T-03-08 mitigation)
        while (!stoppingToken.IsCancellationRequested)
        {
            bool serving = false;
            bool ha = _signals.HaConnected;
            bool mqtt = _mqtt.IsConnected;

            try
            {
                serving = await CheckDetectorServingAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Any gRPC error (deadline, transport failure) → treat as not serving
                serving = false;
            }

            // Cache detector-serving result for zero-latency UI reads (PlaceholderPage / Phase 2+ UI)
            _signals.DetectorConnected = serving;

            string payload = HealthEvaluator.Evaluate(serving, ha, mqtt);
            await _mqtt.PublishAsync(DiscoveryPublisher.HealthStateTopic, payload, retain: true, stoppingToken);

            _logger.LogInformation(LogEvents.HealthEntityPublished,
                "Health state: {Payload} (detector={Serving} ha={Ha} mqtt={Mqtt})",
                payload, serving, ha, mqtt);

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task<bool> CheckDetectorServingAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var resp = await _gateway.HealthClient.CheckAsync(
            new GrpcHealth.HealthCheckRequest { Service = "argus.v1.DetectorService" },
            deadline: deadline,
            cancellationToken: ct);
        return resp.Status == GrpcHealth.HealthCheckResponse.Types.ServingStatus.Serving;
    }

    /// <summary>
    /// Internal testable health cycle: evaluates composite health and invokes the publish delegate.
    /// Used by unit tests to verify the evaluator is called with correct signals and the
    /// result is published to the correct topic — without a live MQTT broker or gRPC server.
    /// Returns (detectorServing, publishedPayload).
    /// </summary>
    internal static async Task<(bool Serving, string Payload)> ExecuteHealthCycleAsync(
        Func<CancellationToken, Task<bool>> detectServing,
        Func<bool> getHaConnected,
        Func<bool> getMqttConnected,
        Func<string, string, bool, CancellationToken, Task> publish,
        CancellationToken ct)
    {
        bool serving = false;
        try
        {
            serving = await detectServing(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            serving = false;
        }

        bool ha = getHaConnected();
        bool mqtt = getMqttConnected();
        string payload = HealthEvaluator.Evaluate(serving, ha, mqtt);
        await publish(DiscoveryPublisher.HealthStateTopic, payload, true, ct);
        return (serving, payload);
    }
}
