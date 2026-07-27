using Argus.Detector.V1;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Mqtt;
using Grpc.Core;
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

    private static HaReading MakeReading(string entityId = "sensor.test", double value = 21.0, bool suppress = false)
        => new HaReading(entityId, value, DateTimeOffset.UtcNow, suppress);

    private static Verdict MakeVerdict(string entityId = "sensor.test", double score = 0.8)
        => new Verdict
        {
            EntityId = entityId,
            Score = score,
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
        entityState.RecordReading(); // warm up (window=1 means 1 reading needed)

        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));
        var reading = MakeReading(suppress: false);
        var verdict = MakeVerdict(score: 0.9);

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
        entityState.RecordReading();

        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));
        var reading = MakeReading(suppress: true); // SUPPRESSED
        var verdict = MakeVerdict(score: 0.9);

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
        entityState.RecordReading();

        var recentAnomalies = new RecentAnomaliesCache();
        var pipeline = new ScoreStreamPipeline(
            publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), recentAnomalies: recentAnomalies);
        var reading = MakeReading(suppress: false);
        var verdict = MakeVerdict(score: 0.9);

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
        entityState.RecordReading();

        var recentAnomalies = new RecentAnomaliesCache();
        var pipeline = new ScoreStreamPipeline(
            publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), recentAnomalies: recentAnomalies);
        var reading = MakeReading(suppress: true); // SUPPRESSED
        var verdict = MakeVerdict(score: 0.9);

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
        verdictState.RecordReading();
        await pipeline.ProcessVerdictAsync(
            MakeReading("sensor.verdict", suppress: false),
            MakeVerdict("sensor.verdict", score: 0.9),
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

    public OrderTrackingDuplexCall(List<string> order)
    {
        _order = order;
        // Immediately close the verdict channel so the read loop can finish
        _verdicts.Writer.Complete();
    }

    public Task WriteAsync(Point point, CancellationToken ct) => Task.CompletedTask;

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
}
