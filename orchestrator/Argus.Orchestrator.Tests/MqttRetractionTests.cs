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

    private static GroupConfig MakePeerGroup(string groupId, params string[] members) =>
        new()
        {
            GroupId = groupId,
            FriendlyName = groupId,
            Mode = "peer_divergence",
            Detector = "peer_divergence",
            Members = [.. members],
        };

    private static GroupConfig MakeJointGroup(string groupId, params string[] members) =>
        new()
        {
            GroupId = groupId,
            FriendlyName = groupId,
            Mode = "joint",
            Detector = "ecod",
            Members = [.. members],
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
        var anomalyId = UniqueId.AnomalyId(entity.EntityId);
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
        var scoreId = UniqueId.ScoreId(entity.EntityId);
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
        var notRemovedAnomalyId = UniqueId.AnomalyId(notRemoved.EntityId);
        var notRemovedScoreId   = UniqueId.ScoreId(notRemoved.EntityId);
        Assert.DoesNotContain(calls, c => c.Topic.Contains(notRemovedAnomalyId));
        Assert.DoesNotContain(calls, c => c.Topic.Contains(notRemovedScoreId));
    }

    // ─── D-G: an entity with no detectors retracts under the same detector-agnostic id ───

    [Fact]
    public async Task RetractAsync_EntityWithNoDetectors_UsesDetectorAgnosticId()
    {
        // Arrange
        var (calls, publish) = MakeRecorder();
        var entity = new EntityConfig
        {
            EntityId = "sensor.pressure_indoor",
            FriendlyName = "pressure",
            Detectors = [],  // empty — the id no longer depends on the detector at all
        };

        // Act
        await DiscoveryPublisher.RetractAsync(publish, [entity], CancellationToken.None);

        // Assert — topics carry no detector segment
        var anomalyId = UniqueId.AnomalyId("sensor.pressure_indoor");
        var scoreId   = UniqueId.ScoreId("sensor.pressure_indoor");
        Assert.Contains(calls, c => c.Topic == $"homeassistant/binary_sensor/{anomalyId}/config");
        Assert.Contains(calls, c => c.Topic == $"homeassistant/sensor/{scoreId}/config");
    }

    // ─── Group membership-change retraction (GRP-08) ─────────────────────────

    [Fact]
    public async Task RetractGroupAsync_PeerGroupShrink4To3_RetractsOnlyRemovedMemberTwoTopics()
    {
        // Arrange — peer group shrinking from 4 members to 3 (one removed)
        var (calls, publish) = MakeRecorder();
        var group = MakePeerGroup("garden_tires", "sensor.tire_fl", "sensor.tire_fr", "sensor.tire_rl", "sensor.tire_rr");
        var removedMember = "sensor.tire_rr";

        // Act — only the removed member is passed
        await DiscoveryPublisher.RetractGroupAsync(publish, group, [removedMember], CancellationToken.None);

        // Assert — exactly 2 messages, both for the removed member's topics
        Assert.Equal(2, calls.Count);
        var flagId = UniqueId.GroupFlagId(group.GroupId, removedMember);
        var scoreId = UniqueId.GroupScoreId(group.GroupId, removedMember);
        Assert.Contains(calls, c => c.Topic == $"homeassistant/binary_sensor/{flagId}/config");
        Assert.Contains(calls, c => c.Topic == $"homeassistant/sensor/{scoreId}/config");
    }

    [Fact]
    public async Task RetractGroupAsync_PeerGroupShrink4To3_DoesNotTouchSurvivingMembers()
    {
        // Arrange
        var (calls, publish) = MakeRecorder();
        var group = MakePeerGroup("garden_tires", "sensor.tire_fl", "sensor.tire_fr", "sensor.tire_rl", "sensor.tire_rr");
        var removedMember = "sensor.tire_rr";
        var survivors = new[] { "sensor.tire_fl", "sensor.tire_fr", "sensor.tire_rl" };

        // Act
        await DiscoveryPublisher.RetractGroupAsync(publish, group, [removedMember], CancellationToken.None);

        // Assert — no call topic mentions any surviving member's slug (mirrors RetractAsync_OnlyRetractsPassedEntities_NotOthers)
        foreach (var survivor in survivors)
        {
            var survivorFlagId = UniqueId.GroupFlagId(group.GroupId, survivor);
            var survivorScoreId = UniqueId.GroupScoreId(group.GroupId, survivor);
            Assert.DoesNotContain(calls, c => c.Topic.Contains(survivorFlagId));
            Assert.DoesNotContain(calls, c => c.Topic.Contains(survivorScoreId));
        }
    }

    [Fact]
    public async Task RetractGroupAsync_WholeJointGroupRemoved_RetractsSingleGroupPair()
    {
        // Arrange — joint group entirely removed (single group-level pair, memberId null)
        var (calls, publish) = MakeRecorder();
        var group = MakeJointGroup("living_room_climate", "sensor.living_room_temp", "sensor.living_room_humidity", "sensor.living_room_pressure");

        // Act — pass a single null entry to retract the group-level pair
        await DiscoveryPublisher.RetractGroupAsync(publish, group, [null], CancellationToken.None);

        // Assert — exactly 2 messages for the single group pair
        Assert.Equal(2, calls.Count);
        var flagId = UniqueId.GroupFlagId(group.GroupId);
        var scoreId = UniqueId.GroupScoreId(group.GroupId);
        Assert.Contains(calls, c => c.Topic == $"homeassistant/binary_sensor/{flagId}/config");
        Assert.Contains(calls, c => c.Topic == $"homeassistant/sensor/{scoreId}/config");
    }

    [Fact]
    public async Task RetractGroupAsync_AllPublishes_UseEmptyPayloadAndRetainTrue()
    {
        // Arrange
        var (calls, publish) = MakeRecorder();
        var group = MakePeerGroup("garden_tires", "sensor.tire_fl", "sensor.tire_fr", "sensor.tire_rl", "sensor.tire_rr");

        // Act
        await DiscoveryPublisher.RetractGroupAsync(publish, group, ["sensor.tire_rr"], CancellationToken.None);

        // Assert — empty payload + retain true across all group retraction calls
        Assert.All(calls, c => Assert.Equal(string.Empty, c.Payload));
        Assert.All(calls, c => Assert.True(c.Retain));
    }

    [Fact]
    public async Task RetractGroupAsync_EmptyRemovedList_PublishesNothing()
    {
        // Arrange
        var (calls, publish) = MakeRecorder();
        var group = MakePeerGroup("garden_tires", "sensor.tire_fl", "sensor.tire_fr", "sensor.tire_rl");

        // Act
        await DiscoveryPublisher.RetractGroupAsync(publish, group, [], CancellationToken.None);

        // Assert
        Assert.Empty(calls);
    }

    // ─── ComputeRetractionEntities: shape-transition decision logic (CR-02) ──
    // Pure logic — no MQTT I/O — covering the 2/3+-member boundary crossing
    // that the original member-list-only diff could not express.

    [Fact]
    public void ComputeRetractionEntities_PeerGroupShrinks3To2_RetractsAllOldMembers()
    {
        // Arrange — peer group crosses the 3+ -> 2 boundary (shape: per-member -> group-level)
        var oldGroup = MakePeerGroup("pipes", "sensor.a", "sensor.b", "sensor.c");
        var newGroup = MakePeerGroup("pipes", "sensor.a", "sensor.b");

        // Act
        var result = DiscoveryPublisher.ComputeRetractionEntities(oldGroup, newGroup);

        // Assert — entire OLD (per-member) shape retracted, not just the dropped member
        Assert.NotNull(result);
        Assert.Equal(["sensor.a", "sensor.b", "sensor.c"], result!.ToList());
    }

    [Fact]
    public void ComputeRetractionEntities_PeerGroupGrows2To3_RetractsOldGroupLevelEntity()
    {
        // Arrange — peer group crosses the 2 -> 3+ boundary (shape: group-level -> per-member)
        var oldGroup = MakePeerGroup("pipes", "sensor.a", "sensor.b");
        var newGroup = MakePeerGroup("pipes", "sensor.a", "sensor.b", "sensor.c");

        // Act
        var result = DiscoveryPublisher.ComputeRetractionEntities(oldGroup, newGroup);

        // Assert — the single OLD group-level entity (memberId=null) is retracted
        Assert.NotNull(result);
        Assert.Equal([null], result!.ToList());
    }

    [Fact]
    public void ComputeRetractionEntities_PeerGroupSameShape_RetractsOnlyRemovedMember()
    {
        // Arrange — 4 -> 3 members, shape unchanged (still per-member, both >= 3)
        var oldGroup = MakePeerGroup("garden_tires", "sensor.tire_fl", "sensor.tire_fr", "sensor.tire_rl", "sensor.tire_rr");
        var newGroup = MakePeerGroup("garden_tires", "sensor.tire_fl", "sensor.tire_fr", "sensor.tire_rl");

        // Act
        var result = DiscoveryPublisher.ComputeRetractionEntities(oldGroup, newGroup);

        // Assert — only the dropped member, not the whole shape
        Assert.NotNull(result);
        Assert.Equal(["sensor.tire_rr"], result!.ToList());
    }

    [Fact]
    public void ComputeRetractionEntities_PeerGroupSameShapeNoChange_ReturnsNull()
    {
        // Arrange — same 3+ members, nothing removed
        var oldGroup = MakePeerGroup("pipes", "sensor.a", "sensor.b", "sensor.c");
        var newGroup = MakePeerGroup("pipes", "sensor.a", "sensor.b", "sensor.c");

        // Act
        var result = DiscoveryPublisher.ComputeRetractionEntities(oldGroup, newGroup);

        // Assert — nothing to retract
        Assert.Null(result);
    }

    [Fact]
    public void ComputeRetractionEntities_JointGroupMemberChange_ReturnsNull()
    {
        // Arrange — joint groups have no per-member entities to diff
        var oldGroup = MakeJointGroup("climate", "sensor.temp", "sensor.humidity");
        var newGroup = MakeJointGroup("climate", "sensor.temp", "sensor.humidity", "sensor.pressure");

        // Act
        var result = DiscoveryPublisher.ComputeRetractionEntities(oldGroup, newGroup);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ComputeRetractionEntities_WholeGroupRemoved_PeerShape_RetractsAllMembers()
    {
        // Arrange — group_id removed entirely (newGroup null), peer shape with 3+ members
        var oldGroup = MakePeerGroup("pipes", "sensor.a", "sensor.b", "sensor.c");

        // Act
        var result = DiscoveryPublisher.ComputeRetractionEntities(oldGroup, null);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(["sensor.a", "sensor.b", "sensor.c"], result!.ToList());
    }

    [Fact]
    public void ComputeRetractionEntities_WholeGroupRemoved_JointShape_RetractsGroupLevelEntity()
    {
        // Arrange — group_id removed entirely (newGroup null), joint (group-level) shape
        var oldGroup = MakeJointGroup("climate", "sensor.temp", "sensor.humidity");

        // Act
        var result = DiscoveryPublisher.ComputeRetractionEntities(oldGroup, null);

        // Assert
        Assert.NotNull(result);
        Assert.Equal([null], result!.ToList());
    }

    [Fact]
    public void ComputeRetractionEntities_WholeGroupRemoved_2MemberPeerShape_RetractsGroupLevelEntity()
    {
        // Arrange — 2-member peer_divergence group removed entirely: uses group-level
        // shape (UsesPerMemberEntities is false for exactly 2 members), same as joint.
        var oldGroup = MakePeerGroup("pipes", "sensor.a", "sensor.b");

        // Act
        var result = DiscoveryPublisher.ComputeRetractionEntities(oldGroup, null);

        // Assert
        Assert.NotNull(result);
        Assert.Equal([null], result!.ToList());
    }
}
