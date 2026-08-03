using Argus.Detector.V1;
using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Mqtt;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for ScoreStreamPipeline: bidi loop, warm-up/cooldown suppression,
/// frozen branch, RpcException degradation, CompleteAsync ordering (PITFALL 3).
/// Uses fakes — no live detector or broker.
/// </summary>
public class ScoreStreamPipelineTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Wraps a static EntitiesConfig in a LiveEntitiesConfig for injection (CFG-04 test pattern).</summary>
    private static ILiveEntitiesConfig MakeLive(EntitiesConfig cfg) => new LiveEntitiesConfig(cfg);

    /// <summary>
    /// A DetectionGateway backed by a lazily-connecting channel to a nonexistent address.
    /// GrpcChannel.ForAddress never actually dials until an RPC is issued, so this is safe
    /// to construct in unit tests that only exercise PrimeFromHistoryAsync (which never
    /// touches _gateway) via the production constructor.
    /// </summary>
    private static DetectionGateway MakeGateway()
        => new(GrpcChannel.ForAddress("http://localhost:1"), NullLogger<DetectionGateway>.Instance);

    private static IReadOnlyList<(DateTime Timestamp, double Value)> MakeHistory(int count, double value = 20.0)
    {
        var baseTime = DateTime.UtcNow.AddDays(-30);
        var rows = new List<(DateTime, double)>();
        for (int i = 0; i < count; i++)
            rows.Add((baseTime.AddMinutes(i), value));
        return rows;
    }

    private static HaReading MakeReading(string entityId = "sensor.test", double value = 21.0, bool suppress = false)
        => new HaReading(entityId, value, DateTimeOffset.UtcNow, suppress);

    /// <summary>
    /// D-01/WARM-01: warmedUp/nSeen/window default to false/0/0 (matching the pre-15-02
    /// "not warmed up yet" state) — tests that need a warmed-up entity pass warmedUp: true
    /// explicitly instead of calling the now-removed EntityRuntimeState.RecordReading().
    /// </summary>
    private static Verdict MakeVerdict(
        string entityId = "sensor.test", double score = 0.8,
        bool warmedUp = false, int nSeen = 0, int window = 0)
        => new Verdict
        {
            EntityId = entityId,
            Score = score,
            WarmedUp = warmedUp,
            NSeen = nSeen,
            Window = window,
        };

    private static EntitiesConfig MakeEntitiesConfig(string entityId = "sensor.test")
    {
        var cfg = new EntitiesConfig();
        cfg.Entities.Add(new EntityConfig
        {
            EntityId = entityId,
            FriendlyName = "Test Sensor",
            Detectors = new List<DetectorConfig>
            {
                new DetectorConfig { Name = "hst", Params = new Dictionary<string, string>() }
            }
        });
        return cfg;
    }

    // ─── Test 1: OnVerdict publishes flag when not suppressed ────────────────

    [Fact]
    public async Task OnVerdict_NotSuppressed_PublishesFlag()
    {
        // Arrange: 3 high-score verdicts (min_consecutive=3) with no suppression
        var publisher = new FakeStatePublisher();

        // Feed 3 consecutive high verdicts to flip hysteresis ON
        // Warm-up: default window=250 readings; we set window=1 via override params
        var cfg = new EntitiesConfig();
        cfg.Entities.Add(new EntityConfig
        {
            EntityId = "sensor.test",
            FriendlyName = "Test",
            Detectors = new List<DetectorConfig>
            {
                new DetectorConfig
                {
                    Name = "hst",
                    Params = new Dictionary<string, string>
                    {
                        // window=1 so WarmedUp after 1 reading
                        ["window"] = "1",
                        ["min_consecutive"] = "1",
                    }
                }
            }
        });

        var entityState = new EntityRuntimeState(HstParams.From(
            cfg.Entities[0].Detectors[0].Params));

        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));
        var reading = MakeReading(suppress: false);
        // D-01/WARM-01: warmed-up now comes from the verdict, not a local counter.
        var verdict = MakeVerdict(score: 0.9, warmedUp: true, nSeen: 1, window: 1);

        // Act
        await pipeline.ProcessVerdictAsync(reading, verdict, entityState, CancellationToken.None);

        // Assert
        Assert.True(publisher.FlagPublished, "Flag should be published when not suppressed and warmed up");
    }

    [Fact]
    public async Task OnVerdict_SuppressBinarySensor_DoesNotPublishFlag()
    {
        // Arrange: reading has SuppressBinarySensor=true
        var publisher = new FakeStatePublisher();
        var cfg = new EntitiesConfig();
        cfg.Entities.Add(new EntityConfig
        {
            EntityId = "sensor.test",
            FriendlyName = "Test",
            Detectors = new List<DetectorConfig>
            {
                new DetectorConfig
                {
                    Name = "hst",
                    Params = new Dictionary<string, string>
                    {
                        ["window"] = "1",
                        ["min_consecutive"] = "1",
                    }
                }
            }
        });

        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));

        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));
        var reading = MakeReading(suppress: true); // SUPPRESSED
        var verdict = MakeVerdict(score: 0.9, warmedUp: true, nSeen: 1, window: 1);

        // Act
        await pipeline.ProcessVerdictAsync(reading, verdict, entityState, CancellationToken.None);

        // Assert: score IS published, flag is NOT
        Assert.True(publisher.ScorePublished, "Score should always be published");
        Assert.False(publisher.FlagPublished, "Flag must be suppressed during cooldown (SuppressBinarySensor=true)");
    }

    [Fact]
    public async Task OnVerdict_NotWarmedUp_DoesNotPublishFlag()
    {
        // Arrange: entity not warmed up (window=250, 0 readings)
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig(); // default window=250

        var entityState = new EntityRuntimeState(
            HstParams.From(cfg.Entities[0].Detectors[0].Params));
        // Do NOT call RecordReading — not warmed up

        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));
        var reading = MakeReading(suppress: false);
        var verdict = MakeVerdict(score: 0.9);

        // Act
        await pipeline.ProcessVerdictAsync(reading, verdict, entityState, CancellationToken.None);

        // Assert: score published, flag NOT (not warmed up — PITFALL 8)
        Assert.True(publisher.ScorePublished);
        Assert.False(publisher.FlagPublished, "Flag must be suppressed during warm-up");
    }

    // ─── Recording-gate tests (QUICK-dashboard-real-data) ────────────────────

    [Fact]
    public async Task OnVerdict_PublishedAndAnomalous_RecordsRecentAnomaly()
    {
        var publisher = new FakeStatePublisher();
        var cfg = new EntitiesConfig();
        cfg.Entities.Add(new EntityConfig
        {
            EntityId = "sensor.test",
            FriendlyName = "Test",
            Detectors = new List<DetectorConfig>
            {
                new DetectorConfig
                {
                    Name = "hst",
                    Params = new Dictionary<string, string>
                    {
                        ["window"] = "1",
                        ["min_consecutive"] = "1",
                    }
                }
            }
        });

        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));

        var recentAnomalies = new RecentAnomaliesCache();
        var pipeline = new ScoreStreamPipeline(
            publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), recentAnomalies: recentAnomalies);
        var reading = MakeReading(suppress: false);
        var verdict = MakeVerdict(score: 0.9, warmedUp: true, nSeen: 1, window: 1);

        await pipeline.ProcessVerdictAsync(reading, verdict, entityState, CancellationToken.None);

        var recorded = recentAnomalies.GetRecent();
        var entry = Assert.Single(recorded);
        Assert.Equal("sensor.test", entry.EntityId);
        Assert.Null(entry.GroupId);
    }

    [Fact]
    public async Task OnVerdict_Suppressed_DoesNotRecordRecentAnomaly()
    {
        var publisher = new FakeStatePublisher();
        var cfg = new EntitiesConfig();
        cfg.Entities.Add(new EntityConfig
        {
            EntityId = "sensor.test",
            FriendlyName = "Test",
            Detectors = new List<DetectorConfig>
            {
                new DetectorConfig
                {
                    Name = "hst",
                    Params = new Dictionary<string, string>
                    {
                        ["window"] = "1",
                        ["min_consecutive"] = "1",
                    }
                }
            }
        });

        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));

        var recentAnomalies = new RecentAnomaliesCache();
        var pipeline = new ScoreStreamPipeline(
            publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), recentAnomalies: recentAnomalies);
        var reading = MakeReading(suppress: true); // SUPPRESSED
        var verdict = MakeVerdict(score: 0.9, warmedUp: true, nSeen: 1, window: 1);

        await pipeline.ProcessVerdictAsync(reading, verdict, entityState, CancellationToken.None);

        Assert.Empty(recentAnomalies.GetRecent());
    }

    // ─── Test 2: Frozen branch publishes flag ON, score, and availability ────

    [Fact]
    public async Task FrozenReading_PublishesFrozenFlag_ScoreAndAvailability()
    {
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));

        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));

        // Act
        await pipeline.PublishFrozenAsync("sensor.test", entityState, CancellationToken.None);

        // Assert: frozen publishes binary_sensor ON
        Assert.True(publisher.FlagPublished);
        Assert.True(publisher.LastFlagValue, "Frozen flag should be ON");
        Assert.True(publisher.AvailabilityPublished, "Availability should be published (online) for frozen");

        // Assert: frozen ALSO publishes a score (invariant "Score is always published"),
        // otherwise the score entity stays `unknown` in HA while the flag reads ON.
        // WHY this matters: the frozen branch is the only guaranteed publish path for a
        // frozen entity — regressing this leaves the flag/score pair incoherent (the
        // original bug: frozen-flag-no-score on sensor.load_5m).
        Assert.True(publisher.ScorePublished, "Frozen branch must publish a score (flag/score coherence)");
        Assert.Equal(1.0, publisher.LastScoreValue);
    }

    // ─── Test 2b: flag-implies-score coherence invariant (generic) ───────────

    [Fact]
    public async Task PublishedFlag_AlwaysAccompaniedByScore_AcrossFrozenAndVerdictPaths()
    {
        // Encodes the class-level invariant generically: whenever a flag is published for
        // an entity, a score must also have been published for that entity. Exercises BOTH
        // the frozen branch and the verdict branch so any future path that publishes a flag
        // without a score is caught here.
        var publisher = new CoherenceTrackingPublisher();

        var cfg = new EntitiesConfig();
        cfg.Entities.Add(new EntityConfig
        {
            EntityId = "sensor.verdict",
            FriendlyName = "Verdict",
            Detectors = new List<DetectorConfig>
            {
                new DetectorConfig
                {
                    Name = "hst",
                    Params = new Dictionary<string, string>
                    {
                        ["window"] = "1",
                        ["min_consecutive"] = "1",
                    }
                }
            }
        });

        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));

        // Frozen path — forces flag ON for a distinct entity
        var frozenState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        await pipeline.PublishFrozenAsync("sensor.frozen", frozenState, CancellationToken.None);

        // Verdict path — warmed up (window=1), not suppressed, high score → flag ON
        var verdictState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        await pipeline.ProcessVerdictAsync(
            MakeReading("sensor.verdict", suppress: false),
            MakeVerdict("sensor.verdict", score: 0.9, warmedUp: true, nSeen: 1, window: 1),
            verdictState,
            CancellationToken.None);

        // Both entities had a flag published — assert both also had a score published.
        Assert.Contains("sensor.frozen", publisher.FlaggedEntities);
        Assert.Contains("sensor.verdict", publisher.FlaggedEntities);
        Assert.Empty(publisher.FlaggedEntitiesWithoutScore());
    }

    // ─── Test 3: RpcException → availability offline (RES-01) ────────────────

    [Fact]
    public async Task RpcException_PublishesAvailabilityOffline()
    {
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));

        // Act
        await pipeline.HandleDetectorFailureAsync("sensor.test", CancellationToken.None);

        // Assert: availability offline published (RES-01)
        Assert.True(publisher.AvailabilityPublished);
        Assert.False(publisher.LastAvailabilityOnline, "Should publish offline on detector failure");
    }

    // ─── Test 4: CompleteAsync ordering ──────────────────────────────────────

    [Fact]
    public async Task RunAsync_CompleteAsyncCalledBeforeReadTaskAwaited()
    {
        // This test verifies PITFALL 3: CompleteAsync must precede readTask await.
        // We use an instrumented fake call that records call order.
        var callOrder = new List<string>();
        var fakeCall = new OrderTrackingDuplexCall(callOrder);
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));

        using var cts = new CancellationTokenSource();

        // Feed one reading then cancel (empty duplex call completes immediately)
        var readings = AsyncEnumerableHelper.FromItems(
            MakeReading("sensor.test"),
            cancellationToken: cts.Token);

        await pipeline.RunAsync(fakeCall, "sensor.test", readings, new EntityRuntimeState(HstParams.From(new Dictionary<string, string> { ["window"] = "1" })), cts.Token);

        // Assert: CompleteAsync recorded before readTask completion
        var completeIdx = callOrder.IndexOf("CompleteAsync");
        var readTaskIdx = callOrder.IndexOf("ReadTaskDone");

        Assert.True(completeIdx >= 0, "CompleteAsync must be called");
        Assert.True(readTaskIdx >= 0, "ReadTask must complete");
        Assert.True(completeIdx < readTaskIdx,
            $"CompleteAsync (idx={completeIdx}) must precede readTask done (idx={readTaskIdx}) — PITFALL 3");
    }

    // ─── Test 5: D2 fix — write loop no longer counts readings (Phase 15-02) ─

    [Fact]
    public async Task RunAsync_FeedingReadings_DoesNotChangeReadingCount()
    {
        // Executable proof that defect D2 is closed: RecordReading() is gone, so pushing
        // readings through the write loop must never move ReadingCount — only a verdict can.
        var callOrder = new List<string>();
        var fakeCall = new OrderTrackingDuplexCall(callOrder); // yields zero verdicts
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));

        using var cts = new CancellationTokenSource();
        var readings = AsyncEnumerableHelper.FromItems(
            Enumerable.Range(0, 5).Select(i => MakeReading("sensor.test", 20.0 + i)),
            cancellationToken: cts.Token);

        await pipeline.RunAsync(fakeCall, "sensor.test", readings, entityState, cts.Token);

        Assert.Equal(0, entityState.ReadingCount);
    }

    // ─── Test 6: D-10 — status cache written from the verdict read loop ─────

    [Fact]
    public async Task ProcessVerdictAsync_WritesStatusCacheFromVerdictNumbers()
    {
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var cache = new EntityStatusCache();
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), statusCache: cache);
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));

        var verdict = MakeVerdict("sensor.test", score: 0.5, warmedUp: false, nSeen: 137, window: 250);
        await pipeline.ProcessVerdictAsync(MakeReading("sensor.test"), verdict, entityState, CancellationToken.None);

        var entry = cache.Get("sensor.test");
        Assert.NotNull(entry);
        Assert.Equal(137, entry!.ReadingCount);
        Assert.Equal(250, entry.WarmUpWindow);
        Assert.False(entry.WarmedUp);
    }

    // ─── Test 7: D-01 — restored-warm entity is not re-suppressed after restart ─

    [Fact]
    public async Task ProcessVerdictAsync_VerdictWarmedUpTrue_PublishesFlagEvenWithZeroLocalReadingCount()
    {
        // A freshly-constructed EntityRuntimeState has ReadingCount=0 (post-restart), but if
        // the detector's Verdict already reports warmed_up=true (restored checkpoint), the
        // flag must publish on the very first verdict — no re-suppression wait.
        var publisher = new FakeStatePublisher();
        var cfg = new EntitiesConfig();
        cfg.Entities.Add(new EntityConfig
        {
            EntityId = "sensor.restored",
            FriendlyName = "Restored",
            Detectors = new List<DetectorConfig>
            {
                new DetectorConfig
                {
                    Name = "hst",
                    Params = new Dictionary<string, string> { ["window"] = "250", ["min_consecutive"] = "1" }
                }
            }
        });
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        Assert.Equal(0, entityState.ReadingCount); // fresh, post-restart

        var verdict = MakeVerdict("sensor.restored", score: 0.9, warmedUp: true, nSeen: 300, window: 250);
        await pipeline.ProcessVerdictAsync(
            MakeReading("sensor.restored", suppress: false), verdict, entityState, CancellationToken.None);

        Assert.True(publisher.FlagPublished, "Restored-warm entity must publish its flag on the first verdict");
    }

    // ─── Test 8: WARM-02 — ToPoint emits resolved HstParams as wire params ────

    [Fact]
    public async Task RunAsync_WriteLoop_EmitsPointWithWindowAndNTreesParams()
    {
        var callOrder = new List<string>();
        var fakeCall = new OrderTrackingDuplexCall(callOrder);
        var publisher = new FakeStatePublisher();
        var cfg = new EntitiesConfig();
        cfg.Entities.Add(new EntityConfig
        {
            EntityId = "sensor.params",
            FriendlyName = "Params",
            Detectors = new List<DetectorConfig>
            {
                new DetectorConfig
                {
                    Name = "hst",
                    Params = new Dictionary<string, string> { ["window"] = "77", ["n_trees"] = "9" }
                }
            }
        });
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));

        using var cts = new CancellationTokenSource();
        var readings = AsyncEnumerableHelper.FromItems(
            new[] { MakeReading("sensor.params") }, cancellationToken: cts.Token);

        await pipeline.RunAsync(fakeCall, "sensor.params", readings, entityState, cts.Token);

        var point = Assert.Single(fakeCall.WrittenPoints);
        Assert.Equal("77", point.Params["window"]);
        Assert.Equal("9", point.Params["n_trees"]);
    }

    // ─── Task 3: PrimeFromHistoryAsync (Phase 15-03, BACKFILL-01..04) ─────────

    [Fact]
    public async Task PrimeFromHistoryAsync_250Rows_SendsOneWarmupRequestWithMatchingFields()
    {
        var cfg = new EntitiesConfig();
        cfg.Entities.Add(new EntityConfig
        {
            EntityId = "sensor.prime",
            FriendlyName = "Prime",
            Detectors = new List<DetectorConfig>
            {
                new DetectorConfig { Name = "hst", Params = new Dictionary<string, string> { ["window"] = "250" } }
            }
        });
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        var history = MakeHistory(250);
        var historySource = new FakeInfluxHistorySource(history);
        var detectorClient = new FakeWarmupDetectorClient();
        var settings = new ConnectionSettings { BackfillEnabled = true, BackfillLookback = "30d" };

        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: historySource, detectorClient: detectorClient, connectionSettings: settings);

        await pipeline.PrimeFromHistoryAsync("sensor.prime", entityState, CancellationToken.None);

        Assert.Equal(1, detectorClient.WarmupCallCount);
        var request = detectorClient.LastWarmupRequest!;
        Assert.Equal("sensor.prime", request.EntityId);
        Assert.Equal("hst", request.Detector);
        Assert.Equal(250, request.History.Count);
        Assert.Equal("250", request.Params["window"]);
    }

    [Fact]
    public async Task PrimeFromHistoryAsync_HistoryPointTimestamps_AreAscending()
    {
        var cfg = MakeEntitiesConfig("sensor.asc");
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        var history = MakeHistory(10);
        var detectorClient = new FakeWarmupDetectorClient();
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: new FakeInfluxHistorySource(history), detectorClient: detectorClient,
            connectionSettings: new ConnectionSettings());

        await pipeline.PrimeFromHistoryAsync("sensor.asc", entityState, CancellationToken.None);

        var timestamps = detectorClient.LastWarmupRequest!.History
            .Select(p => p.Timestamp.ToDateTime())
            .ToList();
        var sorted = timestamps.OrderBy(t => t).ToList();
        Assert.Equal(sorted, timestamps);
    }

    [Fact]
    public async Task PrimeFromHistoryAsync_LongerThanFrozenWindow_MarksFrozenDetectorFrozen()
    {
        var cfg = MakeEntitiesConfig("sensor.frozen_prime");
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        Assert.False(entityState.FrozenDetector.IsFrozen);

        // Constant-value history longer than the default frozen window (10) — proves the
        // rows reached FrozenDetector.AddReading (D-14).
        var history = MakeHistory(20, value: 21.0);
        var detectorClient = new FakeWarmupDetectorClient();
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: new FakeInfluxHistorySource(history), detectorClient: detectorClient,
            connectionSettings: new ConnectionSettings());

        await pipeline.PrimeFromHistoryAsync("sensor.frozen_prime", entityState, CancellationToken.None);

        Assert.True(entityState.FrozenDetector.IsFrozen);
    }

    [Fact]
    public async Task PrimeFromHistoryAsync_NullHistorySource_NoExceptionAndNoWarmupCall()
    {
        var cfg = MakeEntitiesConfig("sensor.degrade1");
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        var detectorClient = new FakeWarmupDetectorClient();
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: null, detectorClient: detectorClient, connectionSettings: new ConnectionSettings());

        await pipeline.PrimeFromHistoryAsync("sensor.degrade1", entityState, CancellationToken.None);

        Assert.Equal(0, detectorClient.WarmupCallCount);
    }

    [Fact]
    public async Task PrimeFromHistoryAsync_NullDetectorClient_NoExceptionAndNoQuery()
    {
        var cfg = MakeEntitiesConfig("sensor.degrade2");
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        var historySource = new FakeInfluxHistorySource(MakeHistory(250));
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: historySource, detectorClient: null, connectionSettings: new ConnectionSettings());

        await pipeline.PrimeFromHistoryAsync("sensor.degrade2", entityState, CancellationToken.None);

        Assert.Equal(0, historySource.QueryHistoryCallCount);
    }

    [Fact]
    public async Task PrimeFromHistoryAsync_BackfillDisabled_NoExceptionAndNoQuery()
    {
        var cfg = MakeEntitiesConfig("sensor.degrade3");
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        var historySource = new FakeInfluxHistorySource(MakeHistory(250));
        var detectorClient = new FakeWarmupDetectorClient();
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: historySource, detectorClient: detectorClient,
            connectionSettings: new ConnectionSettings { BackfillEnabled = false });

        await pipeline.PrimeFromHistoryAsync("sensor.degrade3", entityState, CancellationToken.None);

        Assert.Equal(0, historySource.QueryHistoryCallCount);
        Assert.Equal(0, detectorClient.WarmupCallCount);
    }

    [Fact]
    public async Task PrimeFromHistoryAsync_ZeroRowQuery_NoExceptionAndNoWarmupCall()
    {
        var cfg = MakeEntitiesConfig("sensor.degrade4");
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        var historySource = new FakeInfluxHistorySource(Array.Empty<(DateTime, double)>());
        var detectorClient = new FakeWarmupDetectorClient();
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: historySource, detectorClient: detectorClient, connectionSettings: new ConnectionSettings());

        await pipeline.PrimeFromHistoryAsync("sensor.degrade4", entityState, CancellationToken.None);

        Assert.Equal(0, detectorClient.WarmupCallCount);
    }

    [Fact]
    public async Task PrimeFromHistoryAsync_QueryThrows_NoExceptionEscapes()
    {
        var cfg = MakeEntitiesConfig("sensor.degrade5");
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        var historySource = new FakeInfluxHistorySource(
            Array.Empty<(DateTime, double)>(), throwOnHistory: new InvalidOperationException("influx down"));
        var detectorClient = new FakeWarmupDetectorClient();
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: historySource, detectorClient: detectorClient, connectionSettings: new ConnectionSettings());

        // Must not throw.
        await pipeline.PrimeFromHistoryAsync("sensor.degrade5", entityState, CancellationToken.None);

        Assert.Equal(0, detectorClient.WarmupCallCount);
    }

    [Fact]
    public async Task PrimeFromHistoryAsync_WarmupAsyncThrowsRpcException_NoExceptionEscapes()
    {
        var cfg = MakeEntitiesConfig("sensor.degrade6");
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        var historySource = new FakeInfluxHistorySource(MakeHistory(250));
        var detectorClient = new FakeWarmupDetectorClient
        {
            ThrowOnWarmup = new RpcException(new Status(StatusCode.Unavailable, "detector down")),
        };
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: historySource, detectorClient: detectorClient, connectionSettings: new ConnectionSettings());

        // Must not throw — the stream open path (caller) proceeds normally.
        await pipeline.PrimeFromHistoryAsync("sensor.degrade6", entityState, CancellationToken.None);
    }

    [Fact]
    public async Task PrimeFromHistoryAsync_SkippedResponse_ResultsInNoFurtherWarmupCalls()
    {
        var cfg = MakeEntitiesConfig("sensor.skipped");
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        var historySource = new FakeInfluxHistorySource(MakeHistory(250));
        var detectorClient = new FakeWarmupDetectorClient
        {
            WarmupResponse = new WarmupResponse { Ok = true, Skipped = true, NSeen = 300, WarmedUp = true },
        };
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: historySource, detectorClient: detectorClient, connectionSettings: new ConnectionSettings());

        await pipeline.PrimeFromHistoryAsync("sensor.skipped", entityState, CancellationToken.None);
        // Calling again (e.g. a second stream-open attempt) must not pile up calls beyond
        // what each individual PrimeFromHistoryAsync invocation issues (one call each) —
        // this test's focus is that a skipped response does not trigger a retry loop.
        Assert.Equal(1, detectorClient.WarmupCallCount);
    }

    [Fact]
    public async Task PrimeFromHistoryAsync_PartialPrime_40Of250_AllRowsSent()
    {
        var cfg = new EntitiesConfig();
        cfg.Entities.Add(new EntityConfig
        {
            EntityId = "sensor.partial",
            FriendlyName = "Partial",
            Detectors = new List<DetectorConfig>
            {
                new DetectorConfig { Name = "hst", Params = new Dictionary<string, string> { ["window"] = "250" } }
            }
        });
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        var historySource = new FakeInfluxHistorySource(MakeHistory(40));
        var detectorClient = new FakeWarmupDetectorClient();
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: historySource, detectorClient: detectorClient, connectionSettings: new ConnectionSettings());

        await pipeline.PrimeFromHistoryAsync("sensor.partial", entityState, CancellationToken.None);

        Assert.Equal(40, detectorClient.LastWarmupRequest!.History.Count);
        Assert.Equal(1, detectorClient.WarmupCallCount);
    }

    [Fact]
    public async Task PrimeFromHistoryAsync_ThenMultipleReadings_ExactlyOneWarmupCall()
    {
        // BACKFILL: backfill runs once per stream open, not per reading. PrimeFromHistoryAsync
        // is the only call site that invokes WarmupAsync; RunAsync's write loop never does.
        var cfg = MakeEntitiesConfig("sensor.once");
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        var historySource = new FakeInfluxHistorySource(MakeHistory(250));
        var detectorClient = new FakeWarmupDetectorClient();
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: historySource, detectorClient: detectorClient, connectionSettings: new ConnectionSettings());

        await pipeline.PrimeFromHistoryAsync("sensor.once", entityState, CancellationToken.None);

        var callOrder = new List<string>();
        var fakeCall = new OrderTrackingDuplexCall(callOrder);
        using var cts = new CancellationTokenSource();
        var readings = AsyncEnumerableHelper.FromItems(
            Enumerable.Range(0, 5).Select(i => MakeReading("sensor.once", 20.0 + i)),
            cancellationToken: cts.Token);
        await pipeline.RunAsync(fakeCall, "sensor.once", readings, entityState, cts.Token);

        Assert.Equal(1, detectorClient.WarmupCallCount);
    }

    [Fact]
    public void ScoreStreamPipeline_ResolvesFromDI_WithNoInfluxConfigured()
    {
        // Proves the no-Influx streaming-only deployment (a real supported configuration,
        // per 15-CONTEXT.md D-15) still resolves ScoreStreamPipeline end-to-end: neither
        // IInfluxDataSource nor IBatchDetectorClient is registered, mirroring Program.cs's
        // Influx-conditional DI block never running.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStatePublisher>(new FakeStatePublisher());
        services.AddSingleton<ILiveEntitiesConfig>(MakeLive(MakeEntitiesConfig()));
        services.AddSingleton(GrpcChannel.ForAddress("http://localhost:1"));
        services.AddSingleton<DetectionGateway>();
        services.AddSingleton(new ConnectionSettings());
        // Deliberately NOT registering IInfluxDataSource / IBatchDetectorClient.

        services.AddSingleton<ScoreStreamPipeline>(sp => new ScoreStreamPipeline(
            sp.GetRequiredService<IStatePublisher>(),
            sp.GetRequiredService<ILogger<ScoreStreamPipeline>>(),
            sp.GetRequiredService<ILiveEntitiesConfig>(),
            sp.GetRequiredService<DetectionGateway>(),
            sp.GetService<IEntityStatusCache>(),
            sp.GetService<IRecentAnomaliesCache>(),
            sp.GetService<IInfluxDataSource>(),
            sp.GetService<IBatchDetectorClient>(),
            sp.GetRequiredService<ConnectionSettings>()));

        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<ScoreStreamPipeline>();

        Assert.NotNull(pipeline);
    }
}

// ─── Fakes ────────────────────────────────────────────────────────────────────

/// <summary>Fake StatePublisher that records calls without a live broker.</summary>
internal sealed class FakeStatePublisher : IStatePublisher
{
    public bool FlagPublished { get; private set; }
    public bool LastFlagValue { get; private set; }
    public bool ScorePublished { get; private set; }
    public double LastScoreValue { get; private set; }
    public bool AvailabilityPublished { get; private set; }
    public bool LastAvailabilityOnline { get; private set; }

    public Task PublishFlagAsync(string entityId, bool on, CancellationToken ct)
    {
        FlagPublished = true;
        LastFlagValue = on;
        return Task.CompletedTask;
    }

    public Task PublishScoreAsync(string entityId, double score, CancellationToken ct)
    {
        ScorePublished = true;
        LastScoreValue = score;
        return Task.CompletedTask;
    }

    public Task PublishAvailabilityAsync(string entityId, bool online, CancellationToken ct)
    {
        AvailabilityPublished = true;
        LastAvailabilityOnline = online;
        return Task.CompletedTask;
    }

    public Task PublishGroupFlagAsync(string groupId, string? memberId, bool on, CancellationToken ct)
        => Task.CompletedTask;

    public Task PublishGroupScoreAsync(string groupId, string? memberId, double score, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>
/// Fake publisher that tracks, per entity, whether a flag and/or a score was published.
/// Used to assert the class invariant: any entity with a published flag must also have a
/// published score (flag/score coherence). Catches paths that publish a flag without a score.
/// </summary>
internal sealed class CoherenceTrackingPublisher : IStatePublisher
{
    public HashSet<string> FlaggedEntities { get; } = new();
    public HashSet<string> ScoredEntities { get; } = new();

    /// <summary>Entities that had a flag published but never a score — must be empty.</summary>
    public IReadOnlyCollection<string> FlaggedEntitiesWithoutScore()
        => FlaggedEntities.Where(e => !ScoredEntities.Contains(e)).ToList();

    public Task PublishFlagAsync(string entityId, bool on, CancellationToken ct)
    {
        FlaggedEntities.Add(entityId);
        return Task.CompletedTask;
    }

    public Task PublishScoreAsync(string entityId, double score, CancellationToken ct)
    {
        ScoredEntities.Add(entityId);
        return Task.CompletedTask;
    }

    public Task PublishAvailabilityAsync(string entityId, bool online, CancellationToken ct)
        => Task.CompletedTask;

    public Task PublishGroupFlagAsync(string groupId, string? memberId, bool on, CancellationToken ct)
        => Task.CompletedTask;

    public Task PublishGroupScoreAsync(string groupId, string? memberId, double score, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>
/// Instrumented fake duplex call that records CompleteAsync and read completion order (PITFALL 3).
/// </summary>
internal sealed class OrderTrackingDuplexCall : IScoreStreamCall
{
    private readonly List<string> _order;
    private readonly Channel<Verdict> _verdicts = Channel.CreateUnbounded<Verdict>();

    /// <summary>Points passed to WriteAsync, in call order (WARM-02 ToPoint assertions).</summary>
    public List<Point> WrittenPoints { get; } = new();

    public OrderTrackingDuplexCall(List<string> order)
    {
        _order = order;
        // Immediately close the verdict channel so the read loop can finish
        _verdicts.Writer.Complete();
    }

    public Task WriteAsync(Point point, CancellationToken ct)
    {
        WrittenPoints.Add(point);
        return Task.CompletedTask;
    }

    public async Task CompleteAsync()
    {
        _order.Add("CompleteAsync");
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<Verdict> ReadAllVerdictsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var v in _verdicts.Reader.ReadAllAsync(ct))
            yield return v;
        _order.Add("ReadTaskDone");
    }
}

/// <summary>
/// Fake history source for PrimeFromHistoryAsync tests (Phase 15-03). Mirrors
/// BatchSchedulerWorkerTests.FakeInfluxDbReader's constructor-injected-rows shape.
/// </summary>
internal sealed class FakeInfluxHistorySource : IInfluxDataSource
{
    private readonly IReadOnlyList<(DateTime Timestamp, double Value)> _rows;
    private readonly Exception? _throwOnHistory;

    public FakeInfluxHistorySource(
        IReadOnlyList<(DateTime Timestamp, double Value)> rows, Exception? throwOnHistory = null)
    {
        _rows = rows;
        _throwOnHistory = throwOnHistory;
    }

    public int QueryHistoryCallCount { get; private set; }

    public Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryAsync(
        string entityId, CancellationToken ct)
        => Task.FromResult(_rows);

    public Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryHistoryAsync(
        string entityId, string lookback, int limit, CancellationToken ct)
    {
        QueryHistoryCallCount++;
        if (_throwOnHistory is not null)
            throw _throwOnHistory;
        return Task.FromResult(_rows);
    }
}

/// <summary>
/// Fake detector client for PrimeFromHistoryAsync tests (Phase 15-03). Mirrors
/// BatchSchedulerWorkerTests.FakeBatchDetectorClient's call-counting shape.
/// </summary>
internal sealed class FakeWarmupDetectorClient : IBatchDetectorClient
{
    public int WarmupCallCount { get; private set; }
    public WarmupRequest? LastWarmupRequest { get; private set; }
    public WarmupResponse WarmupResponse { get; init; } = new() { Ok = true };
    public Exception? ThrowOnWarmup { get; init; }

    public Task<ScoreBatchResponse> ScoreBatchAsync(ScoreBatchRequest request, CancellationToken ct)
        => Task.FromResult(new ScoreBatchResponse { Ok = true });

    public Task<FitResponse> FitAsync(FitRequest request, CancellationToken ct)
        => Task.FromResult(new FitResponse { Ok = true });

    public Task<GroupScoreResponse> ScoreGroupBatchAsync(GroupScoreRequest request, CancellationToken ct)
        => Task.FromResult(new GroupScoreResponse { Ok = true });

    public Task<FitGroupResponse> FitGroupAsync(FitGroupRequest request, CancellationToken ct)
        => Task.FromResult(new FitGroupResponse { Ok = true });

    public Task<WarmupResponse> WarmupAsync(WarmupRequest request, CancellationToken ct)
    {
        WarmupCallCount++;
        LastWarmupRequest = request;
        if (ThrowOnWarmup is not null)
            throw ThrowOnWarmup;
        return Task.FromResult(WarmupResponse);
    }
}

/// <summary>Helper to create IAsyncEnumerable from a fixed set of items.</summary>
internal static class AsyncEnumerableHelper
{
    public static async IAsyncEnumerable<HaReading> FromItems(
        HaReading reading,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return reading;
        await Task.CompletedTask;
    }

    public static async IAsyncEnumerable<HaReading> FromItems(
        IEnumerable<HaReading> readings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var reading in readings)
            yield return reading;
        await Task.CompletedTask;
    }
}
