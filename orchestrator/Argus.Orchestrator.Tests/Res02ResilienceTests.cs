using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Mqtt;
using MQTTnet;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// RES-02: Verifies orchestrator restart is idempotent for MQTT discovery.
/// Calling PublishAllAsync twice with the same entities list must produce
/// identical unique_id sets with no accumulation (MQTT-04 / retain=true).
/// Uses a capture delegate — no live broker required.
/// </summary>
public class Res02ResilienceTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static EntityConfig MakeEntity(string entityId, string friendlyName, string detector = "hst")
        => new()
        {
            EntityId = entityId,
            FriendlyName = friendlyName,
            Detectors = [new DetectorConfig { Name = detector, Params = [] }],
        };

    private static List<EntityConfig> TwoEntities() =>
    [
        MakeEntity("sensor.test_a", "Test A"),
        MakeEntity("sensor.test_b", "Test B"),
    ];

    /// <summary>
    /// Extract all unique_id values from a list of captured (topic, payload, retain) tuples.
    /// Only discovery config topics have unique_id payloads; we parse all payloads.
    /// </summary>
    private static HashSet<string> ExtractUniqueIds(
        IReadOnlyList<(string Topic, string Payload, bool Retain)> messages)
    {
        var ids = new HashSet<string>();
        foreach (var (_, payload, _) in messages)
        {
            try
            {
                var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("unique_id", out var uid))
                {
                    var val = uid.GetString();
                    if (val is not null) ids.Add(val);
                }
            }
            catch (JsonException)
            {
                // non-JSON payloads (e.g. availability) — skip
            }
        }
        return ids;
    }

    // ─── Test 1: DiscoveryIdempotency ─────────────────────────────────────────

    [Fact]
    public async Task DiscoveryIdempotency_UniqueIdsIdenticalAcrossTwoPublishes()
    {
        // Arrange
        var firstBatch = new List<(string Topic, string Payload, bool Retain)>();
        var secondBatch = new List<(string Topic, string Payload, bool Retain)>();
        var entities = TwoEntities();

        // Act — first publish
        await DiscoveryPublisher.PublishAllAsync(
            (topic, payload, retain, _) =>
            {
                firstBatch.Add((topic, payload, retain));
                return Task.CompletedTask;
            },
            entities,
            CancellationToken.None);

        // Act — second publish (simulates orchestrator restart)
        await DiscoveryPublisher.PublishAllAsync(
            (topic, payload, retain, _) =>
            {
                secondBatch.Add((topic, payload, retain));
                return Task.CompletedTask;
            },
            entities,
            CancellationToken.None);

        // Assert: same number of messages
        Assert.Equal(firstBatch.Count, secondBatch.Count);

        // Assert: unique_id sets are identical (no orphaned or duplicated entities)
        var firstIds = ExtractUniqueIds(firstBatch);
        var secondIds = ExtractUniqueIds(secondBatch);

        Assert.NotEmpty(firstIds);
        Assert.Equal(firstIds, secondIds);
    }

    [Fact]
    public async Task DiscoveryIdempotency_TwoEntitiesProduceFourPayloads_PerPublish()
    {
        // Each entity produces binary_sensor + sensor config = 2 payloads × 2 entities = 4
        var captured = new List<(string Topic, string Payload, bool Retain)>();
        var entities = TwoEntities();

        await DiscoveryPublisher.PublishAllAsync(
            (topic, payload, retain, _) =>
            {
                captured.Add((topic, payload, retain));
                return Task.CompletedTask;
            },
            entities,
            CancellationToken.None);

        Assert.Equal(4, captured.Count);
    }

    // ─── Test 2: RetainFlag ──────────────────────────────────────────────────

    [Fact]
    public async Task RetainFlag_AllDiscoveryPayloadsHaveRetainTrue()
    {
        // Arrange
        var captured = new List<(string Topic, string Payload, bool Retain)>();
        var entities = TwoEntities();

        // Act
        await DiscoveryPublisher.PublishAllAsync(
            (topic, payload, retain, _) =>
            {
                captured.Add((topic, payload, retain));
                return Task.CompletedTask;
            },
            entities,
            CancellationToken.None);

        // Assert: every message has Retain=true
        Assert.NotEmpty(captured);
        Assert.All(captured, msg => Assert.True(msg.Retain, $"Topic {msg.Topic} must have Retain=true"));
    }
}
