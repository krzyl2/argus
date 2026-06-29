using System.Globalization;
using Argus.Orchestrator.Logging;

namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// Topic helpers and state/availability publish surface for Plan 08 (RES-01).
/// Injected into MqttPublisherWorker; Plan 08 can call PublishFlagAsync/PublishScoreAsync
/// to push detection results over the same MqttConnection.
/// </summary>
public sealed class StatePublisher : IStatePublisher
{
    public const string BridgeAvailabilityTopic = MqttConnection.BridgeAvailabilityTopic;

    private MqttConnection? _mqtt;
    private readonly ILogger<StatePublisher> _logger;

    public StatePublisher(ILogger<StatePublisher> logger)
    {
        _logger = logger;
    }

    // Parameterless ctor for unit tests (no live connection needed for topic helper tests)
    public StatePublisher() : this(Microsoft.Extensions.Logging.Abstractions.NullLogger<StatePublisher>.Instance) { }

    /// <summary>Wires the shared MqttConnection. Called by MqttPublisherWorker after connect.</summary>
    public void SetConnection(MqttConnection mqtt) => _mqtt = mqtt;

    /// <summary>argus/{slug}/flag/state</summary>
    public string FlagTopic(string entityId) => $"argus/{UniqueId.Slug(entityId)}/flag/state";

    /// <summary>argus/{slug}/score/state</summary>
    public string ScoreTopic(string entityId) => $"argus/{UniqueId.Slug(entityId)}/score/state";

    /// <summary>Per-entity availability (not used for LWT — bridge-level handles that).</summary>
    public string EntityAvailabilityTopic(string entityId) => $"argus/{UniqueId.Slug(entityId)}/availability";

    /// <summary>Bridge-level availability topic constant (shared across all entities).</summary>
    string BridgeAvailabilityTopicProperty => BridgeAvailabilityTopic;

    /// <summary>Publishes binary_sensor flag state (ON/OFF).</summary>
    public async Task PublishFlagAsync(string entityId, bool on, CancellationToken ct)
    {
        EnsureConnected();
        var payload = on ? "ON" : "OFF";
        _logger.LogInformation(LogEvents.MqttDiscoveryPublished, "Flag {EntityId} → {Payload}", entityId, payload);
        await _mqtt!.PublishAsync(FlagTopic(entityId), payload, retain: false, ct);
    }

    /// <summary>Publishes anomaly score as invariant-culture float string.</summary>
    public async Task PublishScoreAsync(string entityId, double score, CancellationToken ct)
    {
        EnsureConnected();
        var payload = score.ToString("G", CultureInfo.InvariantCulture);
        await _mqtt!.PublishAsync(ScoreTopic(entityId), payload, retain: false, ct);
    }

    /// <summary>Publishes per-entity availability (online/offline).</summary>
    public async Task PublishAvailabilityAsync(string entityId, bool online, CancellationToken ct)
    {
        EnsureConnected();
        var payload = online ? "online" : "offline";
        await _mqtt!.PublishAsync(EntityAvailabilityTopic(entityId), payload, retain: true, ct);
    }

    /// <summary>Publishes bridge-level availability (online/offline).</summary>
    public async Task PublishBridgeAvailabilityAsync(bool online, CancellationToken ct)
    {
        EnsureConnected();
        var payload = online ? "online" : "offline";
        await _mqtt!.PublishAsync(BridgeAvailabilityTopic, payload, retain: true, ct);
    }

    private void EnsureConnected()
    {
        if (_mqtt is null)
            throw new InvalidOperationException("StatePublisher has no MqttConnection. Call SetConnection first.");
    }
}
