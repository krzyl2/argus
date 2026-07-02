using Argus.Detector.V1;
using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Integration tests for the group scoring loop wired into BatchSchedulerWorker (06-04):
/// staleness-cap branching (joint skip-group vs peer drop-member + min-3 floor),
/// mode-branched publish layout (per-member vs group-level), joint-only nightly fit,
/// and per-group fault isolation. Uses hand-written fakes — no live Influx/gRPC/MQTT.
/// </summary>
public class GroupBatchSchedulerTests
{
    // ─── Fakes ───────────────────────────────────────────────────────────────

    private sealed class FakeInfluxDbReader : IInfluxDataSource
    {
        public Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryAsync(
            string entityId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(DateTime, double)>>(Array.Empty<(DateTime, double)>());
    }

    private sealed class FakeGroupInfluxDataSource : IGroupInfluxDataSource
    {
        public GroupAlignedData Data { get; init; } = new(Array.Empty<GroupRow>(), new Dictionary<string, DateTime>());

        public Task<GroupAlignedData> QueryGroupAsync(
            IReadOnlyList<string> members, string every, string aggFn, TimeSpan stalenessCap, CancellationToken ct)
            => Task.FromResult(Data);
    }

    private sealed class FakeGroupDetectorClient : IBatchDetectorClient
    {
        public int ScoreGroupCallCount { get; private set; }
        public int FitGroupCallCount { get; private set; }
        public bool ThrowOnScoreGroup { get; init; }
        public GroupScoreRequest? LastScoreRequest { get; private set; }
        public GroupScoreResponse ScoreGroupResponse { get; init; } = new() { Ok = true };
        public FitGroupResponse FitGroupResponse { get; init; } = new() { Ok = true };

        public Task<ScoreBatchResponse> ScoreBatchAsync(ScoreBatchRequest request, CancellationToken ct)
            => Task.FromResult(new ScoreBatchResponse { Ok = true });

        public Task<FitResponse> FitAsync(FitRequest request, CancellationToken ct)
            => Task.FromResult(new FitResponse { Ok = true });

        public Task<GroupScoreResponse> ScoreGroupBatchAsync(GroupScoreRequest request, CancellationToken ct)
        {
            ScoreGroupCallCount++;
            LastScoreRequest = request;
            if (ThrowOnScoreGroup) throw new InvalidOperationException("simulated ScoreGroupBatch failure");
            return Task.FromResult(ScoreGroupResponse);
        }

        public Task<FitGroupResponse> FitGroupAsync(FitGroupRequest request, CancellationToken ct)
        {
            FitGroupCallCount++;
            return Task.FromResult(FitGroupResponse);
        }
    }

    private sealed class FakeStatePublisher : IStatePublisher
    {
        public List<(string GroupId, string? MemberId)> GroupFlagCalls { get; } = new();
        public List<(string GroupId, string? MemberId)> GroupScoreCalls { get; } = new();

        public Task PublishFlagAsync(string entityId, bool on, CancellationToken ct) => Task.CompletedTask;
        public Task PublishScoreAsync(string entityId, double score, CancellationToken ct) => Task.CompletedTask;
        public Task PublishAvailabilityAsync(string entityId, bool online, CancellationToken ct) => Task.CompletedTask;

        public Task PublishGroupFlagAsync(string groupId, string? memberId, bool on, CancellationToken ct)
        {
            GroupFlagCalls.Add((groupId, memberId));
            return Task.CompletedTask;
        }

        public Task PublishGroupScoreAsync(string groupId, string? memberId, double score, CancellationToken ct)
        {
            GroupScoreCalls.Add((groupId, memberId));
            return Task.CompletedTask;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static ILiveEntitiesConfig MakeLive(EntitiesConfig cfg) => new LiveEntitiesConfig(cfg);

    private static ConnectionSettings DefaultSettings() => new()
    {
        BatchIntervalMinutes = 1,
        NightlyFitHour = 2,
    };

    private static GroupConfig MakePeerGroup(IReadOnlyList<string> members) => new()
    {
        GroupId = "peer_group",
        FriendlyName = "Peer Group",
        Members = members.ToList(),
        Mode = "peer_divergence",
        Detector = "peer_divergence",
        Params = new Dictionary<string, string>
        {
            ["every"] = "5m",
            ["fn"] = "mean",
            ["staleness_cap"] = "00:30:00",
        },
    };

    private static GroupConfig MakeJointGroup(IReadOnlyList<string> members) => new()
    {
        GroupId = "joint_group",
        FriendlyName = "Joint Group",
        Members = members.ToList(),
        Mode = "joint",
        Detector = "ecod",
        Params = new Dictionary<string, string>
        {
            ["every"] = "5m",
            ["fn"] = "mean",
            ["staleness_cap"] = "00:30:00",
        },
    };

    /// <summary>Builds GroupAlignedData with N rows, all members fresh (LastSeenUtc = now) unless overridden.</summary>
    private static GroupAlignedData FreshData(IReadOnlyList<string> members, int rowCount, DateTime utcNow)
    {
        var rows = Enumerable.Range(0, rowCount)
            .Select(i => new GroupRow(
                utcNow.AddMinutes(-rowCount + i),
                members.ToDictionary(m => m, m => (double?)(20.0 + i))))
            .ToList();
        var lastSeen = members.ToDictionary(m => m, _ => utcNow);
        return new GroupAlignedData(rows, lastSeen);
    }

    private static BatchSchedulerWorker MakeWorker(
        EntitiesConfig cfg,
        FakeGroupInfluxDataSource groupInflux,
        FakeGroupDetectorClient detector,
        FakeStatePublisher publisher)
        => new(
            DefaultSettings(),
            new FakeInfluxDbReader(),
            detector,
            publisher,
            MakeLive(cfg),
            groupInflux,
            NullLogger<BatchSchedulerWorker>.Instance);

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task JointGroup_OneMemberStale_ScoreGroupNotCalled()
    {
        var members = new[] { "sensor.a", "sensor.b", "sensor.c" };
        var group = MakeJointGroup(members);
        var utcNow = DateTime.UtcNow;

        var data = FreshData(members, 3, utcNow);
        // sensor.b is stale: last seen 1 hour ago, beyond the 30-minute cap
        var lastSeen = new Dictionary<string, DateTime>(data.LastSeenUtc) { ["sensor.b"] = utcNow.AddHours(-1) };
        var staleData = data with { LastSeenUtc = lastSeen };

        var groupInflux = new FakeGroupInfluxDataSource { Data = staleData };
        var detector = new FakeGroupDetectorClient();
        var publisher = new FakeStatePublisher();
        var cfg = new EntitiesConfig { Groups = [group] };

        var worker = MakeWorker(cfg, groupInflux, detector, publisher);
        await worker.RunBatchForTestAsync(CancellationToken.None);

        Assert.Equal(0, detector.ScoreGroupCallCount);
        Assert.Empty(publisher.GroupScoreCalls);
        Assert.Empty(publisher.GroupFlagCalls);
    }

    [Fact]
    public async Task PeerGroup_OneMemberStale_ThreeFreshRemain_ScoresOnFreshSubset()
    {
        var members = new[] { "sensor.a", "sensor.b", "sensor.c", "sensor.d" };
        var group = MakePeerGroup(members);
        var utcNow = DateTime.UtcNow;

        var data = FreshData(members, 3, utcNow);
        var lastSeen = new Dictionary<string, DateTime>(data.LastSeenUtc) { ["sensor.d"] = utcNow.AddHours(-1) };
        var staleData = data with { LastSeenUtc = lastSeen };

        var groupInflux = new FakeGroupInfluxDataSource { Data = staleData };
        var detector = new FakeGroupDetectorClient();
        var publisher = new FakeStatePublisher();
        var cfg = new EntitiesConfig { Groups = [group] };

        var worker = MakeWorker(cfg, groupInflux, detector, publisher);
        await worker.RunBatchForTestAsync(CancellationToken.None);

        Assert.Equal(1, detector.ScoreGroupCallCount);
        var seriesMemberIds = detector.LastScoreRequest!.Series.Select(s => s.MemberId).ToList();
        Assert.Equal(3, seriesMemberIds.Count);
        Assert.DoesNotContain("sensor.d", seriesMemberIds);
        Assert.Contains("sensor.a", seriesMemberIds);
        Assert.Contains("sensor.b", seriesMemberIds);
        Assert.Contains("sensor.c", seriesMemberIds);
    }

    [Fact]
    public async Task PeerGroup_MembersStaleBelowFloor_Skipped()
    {
        var members = new[] { "sensor.a", "sensor.b", "sensor.c", "sensor.d" };
        var group = MakePeerGroup(members);
        var utcNow = DateTime.UtcNow;

        var data = FreshData(members, 3, utcNow);
        // Two of four members go stale — only 2 fresh remain, below the 3-member floor.
        var lastSeen = new Dictionary<string, DateTime>(data.LastSeenUtc)
        {
            ["sensor.c"] = utcNow.AddHours(-1),
            ["sensor.d"] = utcNow.AddHours(-1),
        };
        var staleData = data with { LastSeenUtc = lastSeen };

        var groupInflux = new FakeGroupInfluxDataSource { Data = staleData };
        var detector = new FakeGroupDetectorClient();
        var publisher = new FakeStatePublisher();
        var cfg = new EntitiesConfig { Groups = [group] };

        var worker = MakeWorker(cfg, groupInflux, detector, publisher);
        await worker.RunBatchForTestAsync(CancellationToken.None);

        Assert.Equal(0, detector.ScoreGroupCallCount);
    }

    [Fact]
    public async Task PeerGroup_PerMemberResponse_PublishesScoreAndFlagPerMember()
    {
        var members = new[] { "sensor.a", "sensor.b", "sensor.c" };
        var group = MakePeerGroup(members);
        var utcNow = DateTime.UtcNow;
        var data = FreshData(members, 3, utcNow);

        var response = new GroupScoreResponse { Ok = true };
        foreach (var m in members)
            response.PerMember.Add(new Verdict { EntityId = m, Score = 0.4, IsAnomaly = false });

        var groupInflux = new FakeGroupInfluxDataSource { Data = data };
        var detector = new FakeGroupDetectorClient { ScoreGroupResponse = response };
        var publisher = new FakeStatePublisher();
        var cfg = new EntitiesConfig { Groups = [group] };

        var worker = MakeWorker(cfg, groupInflux, detector, publisher);
        await worker.RunBatchForTestAsync(CancellationToken.None);

        Assert.Equal(3, publisher.GroupScoreCalls.Count);
        Assert.Equal(3, publisher.GroupFlagCalls.Count);
        foreach (var m in members)
        {
            Assert.Contains(publisher.GroupScoreCalls, c => c.GroupId == group.GroupId && c.MemberId == m);
            Assert.Contains(publisher.GroupFlagCalls, c => c.GroupId == group.GroupId && c.MemberId == m);
        }
    }

    [Fact]
    public async Task JointGroup_GroupVerdictResponse_PublishesOneScoreAndFlagWithNullMemberId()
    {
        var members = new[] { "sensor.a", "sensor.b", "sensor.c" };
        var group = MakeJointGroup(members);
        var utcNow = DateTime.UtcNow;
        var data = FreshData(members, 3, utcNow);

        var response = new GroupScoreResponse
        {
            Ok = true,
            GroupVerdict = new Verdict { Score = 0.9, IsAnomaly = true },
        };

        var groupInflux = new FakeGroupInfluxDataSource { Data = data };
        var detector = new FakeGroupDetectorClient { ScoreGroupResponse = response };
        var publisher = new FakeStatePublisher();
        var cfg = new EntitiesConfig { Groups = [group] };

        var worker = MakeWorker(cfg, groupInflux, detector, publisher);
        await worker.RunBatchForTestAsync(CancellationToken.None);

        var scoreCall = Assert.Single(publisher.GroupScoreCalls);
        Assert.Equal(group.GroupId, scoreCall.GroupId);
        Assert.Null(scoreCall.MemberId);

        var flagCall = Assert.Single(publisher.GroupFlagCalls);
        Assert.Equal(group.GroupId, flagCall.GroupId);
        Assert.Null(flagCall.MemberId);
    }

    [Fact]
    public async Task RunNightlyFit_JointGroupFitCalled_PeerGroupNeverFit()
    {
        var peerMembers = new[] { "sensor.a", "sensor.b", "sensor.c" };
        var jointMembers = new[] { "sensor.d", "sensor.e", "sensor.f" };
        var peerGroup = MakePeerGroup(peerMembers);
        var jointGroup = MakeJointGroup(jointMembers);
        var utcNow = DateTime.UtcNow;

        // FakeGroupInfluxDataSource returns the same data regardless of which group queries it;
        // build a fixture covering both member sets so both groups get scoreable data.
        var allMembers = peerMembers.Concat(jointMembers).ToList();
        var data = FreshData(allMembers, 3, utcNow);

        var groupInflux = new FakeGroupInfluxDataSource { Data = data };
        var detector = new FakeGroupDetectorClient();
        var publisher = new FakeStatePublisher();
        var cfg = new EntitiesConfig { Groups = [peerGroup, jointGroup] };

        var worker = MakeWorker(cfg, groupInflux, detector, publisher);
        await worker.RunNightlyFitForTestAsync(CancellationToken.None);

        Assert.Equal(1, detector.FitGroupCallCount);
    }

    [Fact]
    public async Task RunBatchAsync_OneGroupThrows_OtherGroupStillScores_NoRethrow()
    {
        var members1 = new[] { "sensor.a", "sensor.b", "sensor.c" };
        var members2 = new[] { "sensor.d", "sensor.e", "sensor.f" };
        var throwingGroup = new GroupConfig
        {
            GroupId = "throwing_group",
            FriendlyName = "Throwing Group",
            Members = members1.ToList(),
            Mode = "peer_divergence",
            Detector = "peer_divergence",
            Params = new Dictionary<string, string> { ["staleness_cap"] = "00:30:00" },
        };
        var okGroup = new GroupConfig
        {
            GroupId = "ok_group",
            FriendlyName = "OK Group",
            Members = members2.ToList(),
            Mode = "peer_divergence",
            Detector = "peer_divergence",
            Params = new Dictionary<string, string> { ["staleness_cap"] = "00:30:00" },
        };
        var utcNow = DateTime.UtcNow;
        var allMembers = members1.Concat(members2).ToList();
        var data = FreshData(allMembers, 3, utcNow);

        var groupInflux = new FakeGroupInfluxDataSource { Data = data };
        var detector = new FakeGroupDetectorClient { ThrowOnScoreGroup = true };
        var publisher = new FakeStatePublisher();
        var cfg = new EntitiesConfig { Groups = [throwingGroup, okGroup] };

        var worker = MakeWorker(cfg, groupInflux, detector, publisher);

        // Should not throw even though ScoreGroupBatch throws for every group
        await worker.RunBatchForTestAsync(CancellationToken.None);

        // Both groups attempted (throw is caught per-group, isolated)
        Assert.Equal(2, detector.ScoreGroupCallCount);
        Assert.Empty(publisher.GroupScoreCalls);
    }
}
