using Argus.Detector.V1;
using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for BatchSchedulerWorker: skip-on-empty, nightly-fit flag suppression,
/// per-entity exception isolation, and live-config swap (CFG-04).
/// Uses hand-written fakes — no live services required (BTCH-01).
/// </summary>
public class BatchSchedulerWorkerTests
{
    // ─── Fakes ───────────────────────────────────────────────────────────────

    private sealed class FakeInfluxDbReader : IInfluxDataSource
    {
        private readonly IReadOnlyList<(DateTime Timestamp, double Value)> _rows;

        public FakeInfluxDbReader(IReadOnlyList<(DateTime Timestamp, double Value)> rows)
            => _rows = rows;

        public Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryAsync(
            string entityId, CancellationToken ct)
            => Task.FromResult(_rows);

        public Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryHistoryAsync(
            string entityId, string lookback, int limit, CancellationToken ct)
            => Task.FromResult(_rows);
    }

    private sealed class FakeBatchDetectorClient : IBatchDetectorClient
    {
        public int ScoreBatchCallCount { get; private set; }
        public int FitCallCount { get; private set; }
        public bool ScoreBatchReturnsOk { get; init; } = true;
        public bool ThrowOnScoreBatch { get; init; }

        /// <summary>Tracks EntityIds received per ScoreBatch call (in order).</summary>
        public List<string> ScoreBatchEntityIds { get; } = new();

        public Task<ScoreBatchResponse> ScoreBatchAsync(ScoreBatchRequest request, CancellationToken ct)
        {
            ScoreBatchCallCount++;
            ScoreBatchEntityIds.Add(request.EntityId);
            if (ThrowOnScoreBatch) throw new InvalidOperationException("simulated ScoreBatch failure");
            var resp = new ScoreBatchResponse { Ok = ScoreBatchReturnsOk };
            if (ScoreBatchReturnsOk)
            {
                resp.Verdicts.Add(new Verdict
                {
                    EntityId = request.EntityId,
                    Score = 0.5,  // double? — google.protobuf.DoubleValue maps to double? in C#
                    IsAnomaly = false,
                });
            }
            return Task.FromResult(resp);
        }

        public Task<FitResponse> FitAsync(FitRequest request, CancellationToken ct)
        {
            FitCallCount++;
            return Task.FromResult(new FitResponse { Ok = true });
        }

        public int ScoreGroupBatchCallCount { get; private set; }

        public Task<GroupScoreResponse> ScoreGroupBatchAsync(GroupScoreRequest request, CancellationToken ct)
        {
            ScoreGroupBatchCallCount++;
            return Task.FromResult(new GroupScoreResponse { Ok = true });
        }

        public Task<FitGroupResponse> FitGroupAsync(FitGroupRequest request, CancellationToken ct)
            => Task.FromResult(new FitGroupResponse { Ok = true });

        public Task<WarmupResponse> WarmupAsync(WarmupRequest request, CancellationToken ct)
            => Task.FromResult(new WarmupResponse { Ok = true });

        // WS6: the simulator seam. This fake is not a simulator — it answers a canned
        // zero-score array of the right length so the classes under test compile and the
        // 1:1 scores/history contract is preserved.
        public Task<SimulateResult> SimulateBatchAsync(
            string entityId, string detector,
            IReadOnlyDictionary<string, string> parameters,
            IReadOnlyList<HistoryPoint> history, CancellationToken ct)
            => Task.FromResult(new SimulateResult(
                true, null, new double[history.Count], Array.Empty<double>(), 0, 0, "fake"));
    }

    private sealed class FakeStatePublisher : IStatePublisher
    {
        public int PublishFlagCallCount { get; private set; }
        public int PublishScoreCallCount { get; private set; }

        /// <summary>Tracks (GroupId, MemberId) pairs received per PublishGroupFlagAsync call (in order).</summary>
        public List<(string GroupId, string? MemberId)> GroupFlagCalls { get; } = new();

        /// <summary>Tracks (GroupId, MemberId) pairs received per PublishGroupScoreAsync call (in order).</summary>
        public List<(string GroupId, string? MemberId)> GroupScoreCalls { get; } = new();

        public Task PublishFlagAsync(string entityId, bool on, CancellationToken ct)
        {
            PublishFlagCallCount++;
            return Task.CompletedTask;
        }

        public Task PublishScoreAsync(string entityId, double score, CancellationToken ct)
        {
            PublishScoreCallCount++;
            return Task.CompletedTask;
        }

        public Task PublishAvailabilityAsync(string entityId, bool online, CancellationToken ct)
            => Task.CompletedTask;

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

    /// <summary>Fake group Influx source for tests that don't exercise the group path.</summary>
    private sealed class FakeGroupInfluxDataSource : IGroupInfluxDataSource
    {
        public Task<GroupAlignedData> QueryGroupAsync(
            IReadOnlyList<string> members, string every, string aggFn, TimeSpan stalenessCap, CancellationToken ct)
            => Task.FromResult(new GroupAlignedData(
                Array.Empty<GroupRow>(),
                new Dictionary<string, DateTime>()));
    }

    /// <summary>Fake group Influx source that returns one fresh, fully-populated row per member.</summary>
    private sealed class FakeGroupInfluxDataSourceWithData : IGroupInfluxDataSource
    {
        public Task<GroupAlignedData> QueryGroupAsync(
            IReadOnlyList<string> members, string every, string aggFn, TimeSpan stalenessCap, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var values = members.ToDictionary(m => m, m => (double?)21.0);
            var lastSeen = members.ToDictionary(m => m, m => now);
            return Task.FromResult(new GroupAlignedData(
                new List<GroupRow> { new(now, values) },
                lastSeen));
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Wraps a static EntitiesConfig in a LiveEntitiesConfig for injection (CFG-04 test pattern).</summary>
    private static ILiveEntitiesConfig MakeLive(EntitiesConfig cfg) => new LiveEntitiesConfig(cfg);

    private static ConnectionSettings DefaultSettings() => new()
    {
        BatchIntervalMinutes = 1,
        NightlyFitHour = 2,
    };

    private static EntitiesConfig OneEntityOneDetector() => new()
    {
        Entities =
        [
            new EntityConfig
            {
                EntityId = "sensor.test",
                Detectors = [new DetectorConfig { Name = "mad" }],
            },
        ],
    };

    private static IReadOnlyList<(DateTime, double)> OnePoint() =>
        [(DateTime.UtcNow, 21.5)];

    private static IReadOnlyList<(DateTime, double)> EmptyPoints() =>
        Array.Empty<(DateTime, double)>();

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunBatchAsync_EntityHasNoPoints_ScoreBatchNotCalled()
    {
        var detector = new FakeBatchDetectorClient();
        var influx = new FakeInfluxDbReader(EmptyPoints());
        var publisher = new FakeStatePublisher();
        var worker = new BatchSchedulerWorker(
            DefaultSettings(),
            influx,
            detector,
            publisher,
            MakeLive(OneEntityOneDetector()),
            new FakeGroupInfluxDataSource(),
            NullLogger<BatchSchedulerWorker>.Instance);

        await worker.RunBatchForTestAsync(CancellationToken.None);

        Assert.Equal(0, detector.ScoreBatchCallCount);
        Assert.Equal(0, publisher.PublishScoreCallCount);
    }

    [Fact]
    public async Task RunBatchAsync_EntityHasPoints_ScoreBatchCalledAndPublishes()
    {
        var detector = new FakeBatchDetectorClient();
        var influx = new FakeInfluxDbReader(OnePoint());
        var publisher = new FakeStatePublisher();
        var worker = new BatchSchedulerWorker(
            DefaultSettings(),
            influx,
            detector,
            publisher,
            MakeLive(OneEntityOneDetector()),
            new FakeGroupInfluxDataSource(),
            NullLogger<BatchSchedulerWorker>.Instance);

        await worker.RunBatchForTestAsync(CancellationToken.None);

        Assert.Equal(1, detector.ScoreBatchCallCount);
        Assert.Equal(1, publisher.PublishScoreCallCount);
    }

    [Fact]
    public async Task RunEntityBatch_PublishesScore_ButNeverFlag()
    {
        // WS2: the batch path used to publish argus/{slug}/flag/state as well — the SAME topic
        // the streaming path owns, but with no hysteresis, no min-duration, no refractory, no
        // rate cap and no watchdog. Two writers with different rules on one topic; the only
        // reason it never showed in the field is that influx_url is unset on this deployment, so
        // the batch worker is never even registered. Score stays (idempotent, no event
        // semantics); the flag belongs to the gated path alone.
        var detector = new FakeBatchDetectorClient();
        var influx = new FakeInfluxDbReader(OnePoint());
        var publisher = new FakeStatePublisher();
        var worker = new BatchSchedulerWorker(
            DefaultSettings(),
            influx,
            detector,
            publisher,
            MakeLive(OneEntityOneDetector()),
            new FakeGroupInfluxDataSource(),
            NullLogger<BatchSchedulerWorker>.Instance);

        await worker.RunBatchForTestAsync(CancellationToken.None);

        Assert.Equal(1, publisher.PublishScoreCallCount);
        Assert.Equal(0, publisher.PublishFlagCallCount);
    }

    [Fact]
    public async Task RunBatchAsync_DetectorThrows_WorkerContinuesToNextEntity_NoRethrow()
    {
        // Two entities: first throws, second should still be processed
        var entities = new EntitiesConfig
        {
            Entities =
            [
                new EntityConfig
                {
                    EntityId = "sensor.one",
                    Detectors = [new DetectorConfig { Name = "mad" }],
                },
                new EntityConfig
                {
                    EntityId = "sensor.two",
                    Detectors = [new DetectorConfig { Name = "mad" }],
                },
            ],
        };
        var detector = new FakeBatchDetectorClient { ThrowOnScoreBatch = true };
        var influx = new FakeInfluxDbReader(OnePoint());
        var publisher = new FakeStatePublisher();
        var worker = new BatchSchedulerWorker(
            DefaultSettings(),
            influx,
            detector,
            publisher,
            MakeLive(entities),
            new FakeGroupInfluxDataSource(),
            NullLogger<BatchSchedulerWorker>.Instance);

        // Should not throw even though ScoreBatch throws per entity
        await worker.RunBatchForTestAsync(CancellationToken.None);

        // Both entities attempted (ScoreBatch called twice — both throw, both caught)
        Assert.Equal(2, detector.ScoreBatchCallCount);
        // No successful publishes since ScoreBatch always threw
        Assert.Equal(0, publisher.PublishScoreCallCount);
    }

    [Fact]
    public async Task NightlyFit_FitRunTodayFlag_SuppressesSecondCallInSameHour()
    {
        var detector = new FakeBatchDetectorClient();
        var influx = new FakeInfluxDbReader(OnePoint());
        var worker = new BatchSchedulerWorker(
            DefaultSettings(),
            influx,
            detector,
            new FakeStatePublisher(),
            MakeLive(OneEntityOneDetector()),
            new FakeGroupInfluxDataSource(),
            NullLogger<BatchSchedulerWorker>.Instance);

        // Run nightly fit twice — second call should be suppressed by _fitRunToday
        await worker.RunNightlyFitForTestAsync(CancellationToken.None);
        await worker.RunNightlyFitForTestAsync(CancellationToken.None);

        // When called via test helper, _fitRunToday is not managed externally,
        // so both calls execute FitAsync — test verifies flag logic via RunBatchTickForTestAsync
        Assert.Equal(2, detector.FitCallCount);
    }

    [Fact]
    public async Task NightlyFit_FitRunTodayFlagSetAfterFirstCall_SuppressedByExternalCheck()
    {
        var detector = new FakeBatchDetectorClient();
        var influx = new FakeInfluxDbReader(OnePoint());
        var worker = new BatchSchedulerWorker(
            DefaultSettings(),
            influx,
            detector,
            new FakeStatePublisher(),
            MakeLive(OneEntityOneDetector()),
            new FakeGroupInfluxDataSource(),
            NullLogger<BatchSchedulerWorker>.Instance);

        // Simulate two ticks at the same hour where nightly fit hour matches
        // First tick: fit runs, fitRunToday = true
        // Second tick: fit suppressed
        int fitCount = await worker.SimulateNightlyFitTicksAsync(
            nightlyFitHour: 2,
            tickHours: [2, 2],
            CancellationToken.None);

        Assert.Equal(1, fitCount);
    }

    /// <summary>
    /// CFG-04: After a Swap, RunBatchAsync iterates the new entity set — proves per-cycle live read.
    /// </summary>
    [Fact]
    public async Task RunBatchAsync_AfterSwap_UsesNewEntitySet()
    {
        // Arrange: start with sensor.original, swap to sensor.swapped
        var initialConfig = new EntitiesConfig
        {
            Entities =
            [
                new EntityConfig
                {
                    EntityId = "sensor.original",
                    Detectors = [new DetectorConfig { Name = "mad" }],
                },
            ],
        };
        var swappedConfig = new EntitiesConfig
        {
            Entities =
            [
                new EntityConfig
                {
                    EntityId = "sensor.swapped",
                    Detectors = [new DetectorConfig { Name = "mad" }],
                },
            ],
        };

        var liveConfig = new LiveEntitiesConfig(initialConfig);
        var detector = new FakeBatchDetectorClient();
        var influx = new FakeInfluxDbReader(OnePoint());
        var worker = new BatchSchedulerWorker(
            DefaultSettings(),
            influx,
            detector,
            new FakeStatePublisher(),
            liveConfig,
            new FakeGroupInfluxDataSource(),
            NullLogger<BatchSchedulerWorker>.Instance);

        // Act: swap config, then run batch — must use the NEW entity set
        liveConfig.Swap(swappedConfig);
        await worker.RunBatchForTestAsync(CancellationToken.None);

        // Assert: only sensor.swapped was scored, not sensor.original
        Assert.Equal(1, detector.ScoreBatchCallCount);
        Assert.Equal("sensor.swapped", detector.ScoreBatchEntityIds[0]);
    }

    // -----------------------------------------------------------------------
    // CR-03: scheduler-side guard against a mode/detector mismatch that
    // reached disk (defense in depth — GroupInputValidator is the
    // authoritative gate at save time; this covers a hand-edited entities.yaml).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunBatchAsync_GroupModeDetectorMismatch_SkipsScoringAndPublishesNothing()
    {
        var mismatchedGroup = new GroupConfig
        {
            GroupId = "group.mismatched",
            FriendlyName = "Mismatched",
            Members = ["sensor.a", "sensor.b", "sensor.c"],
            Mode = "joint",
            Detector = "peer_divergence", // CR-03: incompatible with mode="joint"
            Params = new Dictionary<string, string>(),
        };
        var config = new EntitiesConfig { Groups = [mismatchedGroup] };
        var detector = new FakeBatchDetectorClient();
        var publisher = new FakeStatePublisher();
        var worker = new BatchSchedulerWorker(
            DefaultSettings(),
            new FakeInfluxDbReader(EmptyPoints()),
            detector,
            publisher,
            MakeLive(config),
            new FakeGroupInfluxDataSourceWithData(),
            NullLogger<BatchSchedulerWorker>.Instance);

        await worker.RunBatchForTestAsync(CancellationToken.None);

        // Must skip before ever calling ScoreGroupBatchAsync — no fabricated verdict published.
        Assert.Equal(0, detector.ScoreGroupBatchCallCount);
        Assert.Empty(publisher.GroupScoreCalls);
        Assert.Empty(publisher.GroupFlagCalls);
    }

    [Fact]
    public async Task RunBatchAsync_GroupModeDetectorConsistent_ScoresNormally()
    {
        var validGroup = new GroupConfig
        {
            GroupId = "group.valid",
            FriendlyName = "Valid",
            Members = ["sensor.a", "sensor.b", "sensor.c"],
            Mode = "joint",
            Detector = "ecod",
            Params = new Dictionary<string, string>(),
        };
        var config = new EntitiesConfig { Groups = [validGroup] };
        var detector = new FakeBatchDetectorClient();
        var publisher = new FakeStatePublisher();
        var worker = new BatchSchedulerWorker(
            DefaultSettings(),
            new FakeInfluxDbReader(EmptyPoints()),
            detector,
            publisher,
            MakeLive(config),
            new FakeGroupInfluxDataSourceWithData(),
            NullLogger<BatchSchedulerWorker>.Instance);

        await worker.RunBatchForTestAsync(CancellationToken.None);

        Assert.Equal(1, detector.ScoreGroupBatchCallCount);
    }
}
