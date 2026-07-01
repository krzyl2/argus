using Argus.Orchestrator.Config;
using Argus.Orchestrator.Mqtt;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for DiscoveryPublisher.RetractAsync.
/// Verifies that retraction publishes empty retained payloads to the correct
/// binary_sensor + sensor config topics for removed entities only.
/// Uses the testable delegate overload to avoid requiring a live MQTT broker.
/// </summary>
public class MqttRetractionTests
{
    // ─── Recording seam ──────────────────────────────────────────────────────

    private sealed record PublishCall(string Topic, string Payload, bool Retain);

    private static (List<PublishCall> calls, Func<string, string, bool, CancellationToken, Task> publish) MakeRecorder()
    {
        var calls = new List<PublishCall>();
        Task Publish(string topic, string payload, bool retain, CancellationToken _)
        {
            calls.Add(new PublishCall(topic, payload, retain));
            return Task.CompletedTask;
        }
        return (calls, Publish);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static EntityConfig MakeEntity(string entityId, string detectorName = "hst") =>
        new()
        {
            EntityId = entityId,
            FriendlyName = entityId,
            Detectors = [new DetectorConfig { Name = detectorName, Params = [] }],
        };

    // ─── Two publishes per removed entity ────────────────────────────────────

    [Fact]
    public async Task RetractAsync_OneEntity_PublishesTwoMessages()
    {
        // Arrange
        var (calls, publish) = MakeRecorder();
        var entity = MakeEntity("sensor.temperature_indoor");

        // Act
        await DiscoveryPublisher.RetractAsync(publish, [entity], CancellationToken.None);

        // Assert
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task RetractAsync_TwoEntities_PublishesFourMessages()
    {
        // Arrange
        var (calls, publish) = MakeRecorder();
        var entities = new[]
        {
            MakeEntity("sensor.temperature_indoor"),
            MakeEntity("sensor.humidity_outdoor"),
        };

        // Act
        await DiscoveryPublisher.RetractAsync(publish, entities, CancellationToken.None);

        // Assert
        Assert.Equal(4, calls.Count);
    }

    // ─── Correct topics ──────────────────────────────────────────────────────

    [Fact]
    public async Task RetractAsync_PublishesToBinarySensorConfigTopic()
    {
        // Arrange
        var (calls, publish) = MakeRecorder();
        var entity = MakeEntity("sensor.temperature_indoor", "hst");

        // Act
        await DiscoveryPublisher.RetractAsync(publish, [entity], CancellationToken.None);

        // Assert — one of the two topics is binary_sensor
        var anomalyId = UniqueId.AnomalyId(entity.EntityId, "hst");
        var expectedTopic = $"homeassistant/binary_sensor/{anomalyId}/config";
        Assert.Contains(calls, c => c.Topic == expectedTopic);
    }

    [Fact]
    public async Task RetractAsync_PublishesToSensorConfigTopic()
    {
        // Arrange
        var (calls, publish) = MakeRecorder();
        var entity = MakeEntity("sensor.temperature_indoor", "hst");

        // Act
        await DiscoveryPublisher.RetractAsync(publish, [entity], CancellationToken.None);

        // Assert — one of the two topics is sensor (score)
        var scoreId = UniqueId.ScoreId(entity.EntityId, "hst");
        var expectedTopic = $"homeassistant/sensor/{scoreId}/config";
        Assert.Contains(calls, c => c.Topic == expectedTopic);
    }

    // ─── Empty payload + retain true ─────────────────────────────────────────

    [Fact]
    public async Task RetractAsync_AllPublishes_UseEmptyPayload()
    {
        // Arrange
        var (calls, publish) = MakeRecorder();
        var entity = MakeEntity("sensor.temperature_indoor");

        // Act
        await DiscoveryPublisher.RetractAsync(publish, [entity], CancellationToken.None);

        // Assert — both publishes carry an empty payload
        Assert.All(calls, c => Assert.Equal(string.Empty, c.Payload));
    }

    [Fact]
    public async Task RetractAsync_AllPublishes_UseRetainTrue()
    {
        // Arrange
        var (calls, publish) = MakeRecorder();
        var entity = MakeEntity("sensor.temperature_indoor");

        // Act
        await DiscoveryPublisher.RetractAsync(publish, [entity], CancellationToken.None);

        // Assert — both publishes use retain=true (MQTT retained-message deletion)
        Assert.All(calls, c => Assert.True(c.Retain));
    }

    // ─── Non-removed entities receive no publishes ────────────────────────────

    [Fact]
    public async Task RetractAsync_EmptyList_PublishesNothing()
    {
        // Arrange
        var (calls, publish) = MakeRecorder();

        // Act
        await DiscoveryPublisher.RetractAsync(publish, [], CancellationToken.None);

        // Assert
        Assert.Empty(calls);
    }

    [Fact]
    public async Task RetractAsync_OnlyRetractsPassedEntities_NotOthers()
    {
        // Arrange
        var (calls, publish) = MakeRecorder();
        var removed = MakeEntity("sensor.temperature_indoor");
        var notRemoved = MakeEntity("sensor.humidity_outdoor");

        // Act — only removed is passed
        await DiscoveryPublisher.RetractAsync(publish, [removed], CancellationToken.None);

        // Assert — no publishes mention the non-removed entity's IDs
        var notRemovedAnomalyId = UniqueId.AnomalyId(notRemoved.EntityId, "hst");
        var notRemovedScoreId   = UniqueId.ScoreId(notRemoved.EntityId, "hst");
        Assert.DoesNotContain(calls, c => c.Topic.Contains(notRemovedAnomalyId));
        Assert.DoesNotContain(calls, c => c.Topic.Contains(notRemovedScoreId));
    }

    // ─── GetDetectorName fallback ("hst" when no detectors configured) ────────

    [Fact]
    public async Task RetractAsync_EntityWithNoDetectors_UsesFallbackDetectorName()
    {
        // Arrange
        var (calls, publish) = MakeRecorder();
        var entity = new EntityConfig
        {
            EntityId = "sensor.pressure_indoor",
            FriendlyName = "pressure",
            Detectors = [],  // empty — should fall back to "hst"
        };

        // Act
        await DiscoveryPublisher.RetractAsync(publish, [entity], CancellationToken.None);

        // Assert — topics use "hst" fallback
        var anomalyId = UniqueId.AnomalyId("sensor.pressure_indoor", "hst");
        var scoreId   = UniqueId.ScoreId("sensor.pressure_indoor", "hst");
        Assert.Contains(calls, c => c.Topic == $"homeassistant/binary_sensor/{anomalyId}/config");
        Assert.Contains(calls, c => c.Topic == $"homeassistant/sensor/{scoreId}/config");
    }
}
