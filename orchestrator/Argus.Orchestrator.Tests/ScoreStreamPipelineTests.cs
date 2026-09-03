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

    /// <summary>
    /// Builds per-entity state from a raw params map, wiring BOTH HstParams and AlertParams the
    /// way BuildEntityStates does — the alert keys share the HST params map, so a test that only
    /// built HstParams would silently exercise the default gate instead of the configured one.
    /// </summary>
    private static EntityRuntimeState MakeState(Dictionary<string, string> p)
        => new(HstParams.From(p), AlertParams.From(p));

    /// <summary>
    /// Params that make the adaptive gate reachable inside a unit test: a short rank window, a
    /// low calibration target, and no min-duration hold. The gating RULES are untouched — the
    /// fire quantile is still 0.99 and the flag still only moves on a transition.
    /// </summary>
    private static Dictionary<string, string> FastAlertParams(int minConsecutive = 1)
        => new()
        {
            ["window"] = "1",
            ["min_consecutive"] = minConsecutive.ToString(),
            ["rank_window"] = "200",
            ["alert_min_samples"] = "50",
            ["min_duration_sec"] = "0",
        };

    /// <summary>
    /// Drives an entity through <paramref name="count"/> strictly increasing scores. Each score
    /// is a new maximum, so its mid-rank is 1.0 and the score channel is saturated — the last
    /// verdict is guaranteed to be past calibration and above q_fire.
    /// </summary>
    private static async Task DriveToFiringAsync(
        ScoreStreamPipeline pipeline, EntityRuntimeState state, string entityId, int count = 60)
    {
        for (int i = 0; i < count; i++)
            await pipeline.ProcessVerdictAsync(
                MakeReading(entityId),
                MakeVerdict(entityId, score: 0.5 + i * 0.001, warmedUp: true, nSeen: i + 1, window: 1),
                state,
                CancellationToken.None);
    }

    /// <summary>
    /// Fills the entity's raw evidence channel the way the write loop does on every reading, so
    /// a test that calls PublishFrozenAsync directly starts from the state production is in by
    /// the time it gets there.
    /// </summary>
    private static void PrimeRawChannel(EntityRuntimeState state, double value = 21.0, int count = 16)
    {
        for (int i = 0; i < count; i++)
            state.Alert.ObserveValue(value);
    }

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
        // Rewritten for the adaptive gate. The old body fed three verdicts at a constant 0.9 and
        // relied on an absolute threshold; under a rank gate a constant score sits at mid-rank
        // 0.5 forever and would never fire, so the driver now supplies a genuinely rising score
        // that ends on a unique maximum — the shape that SHOULD raise a flag.
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var state = MakeState(FastAlertParams());

        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));
        await DriveToFiringAsync(pipeline, state, "sensor.test");

        Assert.True(publisher.FlagPublished, "Flag should be published when not suppressed");
        Assert.True(publisher.LastFlagValue, "A unique-maximum score past calibration must raise the flag");
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
    public async Task OnVerdict_NotWarmedUp_PublishesOffExactlyOnce_NeverOn()
    {
        // Assertion deliberately INVERTED from the pre-WS2 version. Warm-up used to suppress the
        // publish itself, which left HA holding whatever retained ON survived the last restart —
        // a flag nobody could explain and nothing could clear. Warm-up now suppresses the flag's
        // VALUE instead: exactly one explicit OFF goes out, clearing any stale retained ON, and
        // it is never republished while nothing changes.
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig(); // default window=250

        var entityState = MakeState(cfg.Entities[0].Detectors[0].Params);

        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));

        for (int i = 0; i < 5; i++)
            await pipeline.ProcessVerdictAsync(MakeReading(suppress: false), MakeVerdict(score: 0.9),
                entityState, CancellationToken.None);

        Assert.True(publisher.ScorePublished);
        Assert.Equal(1, publisher.FlagPublishCount);
        Assert.Equal(new[] { false }, publisher.FlagHistory);
    }

    // ─── Recording-gate tests (QUICK-dashboard-real-data) ────────────────────

    [Fact]
    public async Task OnVerdict_PublishedAndAnomalous_RecordsRecentAnomaly()
    {
        // Same rewrite as OnVerdict_NotSuppressed_PublishesFlag: a constant score has no rank,
        // so the driver must actually produce an anomaly for the recording gate to be exercised.
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var state = MakeState(FastAlertParams());

        var recentAnomalies = new RecentAnomaliesCache();
        var pipeline = new ScoreStreamPipeline(
            publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), recentAnomalies: recentAnomalies);

        await DriveToFiringAsync(pipeline, state, "sensor.test");

        var entry = Assert.Single(recentAnomalies.GetRecent());
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

        // The write loop feeds the raw evidence channel before it ever calls PublishFrozenAsync;
        // reproduce that here, because the forced ON is a premise the gate shares (D-H) and both
        // loops have to answer it the same way.
        PrimeRawChannel(entityState);

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

        // Frozen path — raises the flag for a distinct entity
        var frozenState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        PrimeRawChannel(frozenState);
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

        // WS2: the frozen path keeps publishing a flag (it is the ONLY guaranteed publish path
        // for an entity whose detector returns no verdict), but change-only — a second frozen
        // reading must not repeat it. Both halves matter: dropping the publish would strand the
        // entity, repeating it is F8.
        await pipeline.PublishFrozenAsync("sensor.frozen", frozenState, CancellationToken.None);
        Assert.Equal(1, publisher.FlagPublishCounts["sensor.frozen"]);
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

    // ─── Test 3b: stream failures never take the host down ───────────────────

    /// <summary>One reading that ignores cancellation, so the stream reaches the call itself.</summary>
    private static async IAsyncEnumerable<HaReading> SingleReading(string entityId)
    {
        await Task.Yield();
        yield return MakeReading(entityId);
    }

    private static EntityRuntimeState WarmState()
        => new EntityRuntimeState(HstParams.From(new Dictionary<string, string> { ["window"] = "1" }));

    [Fact]
    public async Task RunEntityStream_CancelledRpcDuringShutdown_DoesNotThrow()
    {
        // Client-side cancellation reaches us as RpcException/Cancelled, not OCE. If it
        // escapes, HaListenerWorker fails and StopHost tears the add-on down on every
        // shutdown and every CFG-04 reload.
        var publisher = new FakeStatePublisher();
        var pipeline = new ScoreStreamPipeline(
            publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(MakeEntitiesConfig()));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => pipeline.RunEntityStreamAsync(
            "sensor.test",
            WarmState(),
            SingleReading("sensor.test"),
            _ => new ThrowingScoreStreamCall(
                new RpcException(new Status(StatusCode.Cancelled, "Call canceled by the client."))),
            cts.Token));

        Assert.Null(ex);
        Assert.False(publisher.AvailabilityPublished,
            "Our own cancellation is a clean stop, not a detector failure");
    }

    [Fact]
    public async Task RunEntityStream_CancelledRpcWithoutShutdown_MarksEntityOffline()
    {
        // Detector dropped the call while we were still running — degrade the entity,
        // stay alive (RES-01).
        var publisher = new FakeStatePublisher();
        var pipeline = new ScoreStreamPipeline(
            publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(MakeEntitiesConfig()));

        var ex = await Record.ExceptionAsync(() => pipeline.RunEntityStreamAsync(
            "sensor.test",
            WarmState(),
            SingleReading("sensor.test"),
            _ => new ThrowingScoreStreamCall(
                new RpcException(new Status(StatusCode.Cancelled, "dropped by detector"))),
            CancellationToken.None));

        Assert.Null(ex);
        Assert.True(publisher.AvailabilityPublished);
        Assert.False(publisher.LastAvailabilityOnline);
    }

    [Fact]
    public async Task RunEntityStream_UnavailableRpc_MarksEntityOffline()
    {
        var publisher = new FakeStatePublisher();
        var pipeline = new ScoreStreamPipeline(
            publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(MakeEntitiesConfig()));

        var ex = await Record.ExceptionAsync(() => pipeline.RunEntityStreamAsync(
            "sensor.test",
            WarmState(),
            SingleReading("sensor.test"),
            _ => new ThrowingScoreStreamCall(
                new RpcException(new Status(StatusCode.Unavailable, "detector down"))),
            CancellationToken.None));

        Assert.Null(ex);
        Assert.True(publisher.AvailabilityPublished);
        Assert.False(publisher.LastAvailabilityOnline);
    }

    // ─── Test 4: CompleteAsync ordering ──────────────────────────────────────

    [Fact]
    public async Task RunAsync_CompleteAsyncCalledBeforeReadTaskAwaited()
    {
        // PITFALL 3: RunAsync must call CompleteAsync BEFORE awaiting readTask. On a real
        // gRPC duplex call the response stream only ends after the client half-closes, so
        // the reverse order deadlocks the entity stream forever. OrderTrackingDuplexCall
        // reproduces that dependency: its verdict channel is closed by CompleteAsync, never
        // before. A regression therefore hangs here rather than merely misordering, which is
        // why the call is raced against a timeout instead of simply awaited.
        var callOrder = new List<string>();
        var fakeCall = new OrderTrackingDuplexCall(callOrder);
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));

        using var cts = new CancellationTokenSource();

        // One reading through the write loop; the fake yields no verdicts.
        var readings = AsyncEnumerableHelper.FromItems(
            MakeReading("sensor.test"),
            cancellationToken: cts.Token);

        var run = pipeline.RunAsync(fakeCall, "sensor.test", readings, new EntityRuntimeState(HstParams.From(new Dictionary<string, string> { ["window"] = "1" })), cts.Token);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(finished, run),
            "RunAsync never returned — readTask was awaited before CompleteAsync (PITFALL 3 deadlock)");
        await run;

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

    // ─── Test 6b: F6-3 — measured cadence reaches the status cache ───────────

    [Fact]
    public async Task RunAsync_MeasuresReadingCadence_AndPublishesItOnTheStatusCache()
    {
        // F6-3: window is configured in SAMPLES. Without a measured cadence on the entry the
        // editor degrades to the bare number and the operator cannot see that the SAME
        // window: 720 is ~78 h of baseline on this sensor. The gap this pins is that the two
        // halves existed but were never joined: the write loop is the only place a real HA
        // timestamp exists, and the status cache is written from the verdict read loop.
        var fakeCall = new OrderTrackingDuplexCall(new List<string>()); // yields zero verdicts
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var cache = new EntityStatusCache();
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), statusCache: cache);
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));

        using var cts = new CancellationTokenSource();
        // lodowkababcia_power's measured spacing: 391 s between readings.
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var readings = AsyncEnumerableHelper.FromItems(
            Enumerable.Range(0, 9).Select(i => new HaReading("sensor.test", 100.0 + i, t0.AddSeconds(391.0 * i), false)),
            cancellationToken: cts.Token);

        await pipeline.RunAsync(fakeCall, "sensor.test", readings, entityState, cts.Token);

        // The verdict read loop is what writes the cache — the cadence must survive the hop.
        await pipeline.ProcessVerdictAsync(
            MakeReading("sensor.test"),
            MakeVerdict("sensor.test", score: 0.1, warmedUp: true, nSeen: 720, window: 720),
            entityState,
            CancellationToken.None);

        var entry = cache.Get("sensor.test");
        Assert.NotNull(entry);
        Assert.NotNull(entry!.MedianIntervalSec);
        Assert.Equal(391.0, entry.MedianIntervalSec!.Value, 3);
        // 720 samples at that cadence is 78 h, i.e. past the 48 h the editor warns on.
        Assert.True(720 * entry.MedianIntervalSec!.Value / 3600.0 > 48.0);
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
        // WS2: the same rows must also seed the raw-evidence window. Without this the robust-z
        // channel starts empty after every restart and abstains for its first 10 live readings —
        // on a 225-readings-a-day sensor that is hours of silence the history could have covered.
        Assert.Equal(history.Count, entityState.Alert.RawSampleCount);
    }

    [Fact]
    public async Task PrimeFromHistory_RequestsWindowRows_Not250()
    {
        // WS5: min_samples (60) is when rmad starts answering; 720 is when its median/MAD stop
        // moving with every new point. Priming only the configured legacy window (250) — or worse,
        // min_samples — hands the detector a scale estimated from a fraction of the baseline, and
        // the resulting z-scores are wrong for as long as it takes live traffic to fill the rest
        // (hours to days on a 225-readings-a-day sensor). The request is the baseline, not the
        // readiness threshold.
        var cfg = new EntitiesConfig();
        cfg.Entities.Add(new EntityConfig
        {
            EntityId = "sensor.legacy_window",
            FriendlyName = "Legacy window",
            Detectors = new List<DetectorConfig>
            {
                new DetectorConfig { Name = "hst", Params = new Dictionary<string, string> { ["window"] = "250" } }
            }
        });
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        Assert.Equal(250, entityState.HstParams.Window);

        var historySource = new FakeInfluxHistorySource(MakeHistory(720));
        var detectorClient = new FakeWarmupDetectorClient();
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: historySource, detectorClient: detectorClient,
            connectionSettings: new ConnectionSettings());

        await pipeline.PrimeFromHistoryAsync("sensor.legacy_window", entityState, CancellationToken.None);

        Assert.Equal(720, historySource.LastLimit);
    }

    [Fact]
    public async Task PrimeFromHistory_ConfiguredWindowWiderThanBaseline_KeepsConfiguredWindow()
    {
        // The baseline is a FLOOR, not a replacement: an operator who widened the window did so
        // to get a longer memory, and silently priming only 720 rows would give them a detector
        // whose configured window can never be filled from history.
        var cfg = new EntitiesConfig();
        cfg.Entities.Add(new EntityConfig
        {
            EntityId = "sensor.wide",
            FriendlyName = "Wide",
            Detectors = new List<DetectorConfig>
            {
                new DetectorConfig { Name = "hst", Params = new Dictionary<string, string> { ["window"] = "1500" } }
            }
        });
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        var historySource = new FakeInfluxHistorySource(MakeHistory(10));
        var detectorClient = new FakeWarmupDetectorClient();
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: historySource, detectorClient: detectorClient,
            connectionSettings: new ConnectionSettings());

        await pipeline.PrimeFromHistoryAsync("sensor.wide", entityState, CancellationToken.None);

        Assert.Equal(1500, historySource.LastLimit);
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
        Assert.Equal(history.Count, entityState.Alert.RawSampleCount);
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

    // ─── 15-04 Task 1: cross-plan restart/crash cases (SC-8) ──────────────────

    [Fact]
    public async Task PrimeFromHistoryAsync_CalledTwiceWithSkippedResponse_NoAdditionalPrimingAttempts()
    {
        // SC-8: an orchestrator restart against an already-checkpointed detector must not
        // re-backfill. Simulated here by running PrimeFromHistoryAsync twice against the same
        // fakes (two stream-open attempts) while the detector-side gate reports skipped both
        // times — each run issues exactly one Warmup call (no internal retry/compensation
        // loop), and the response's own counters (n_seen) never grow between runs because the
        // detector never actually re-primed.
        var cfg = MakeEntitiesConfig("sensor.skip_twice");
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        var historySource = new FakeInfluxHistorySource(MakeHistory(250));
        var detectorClient = new FakeWarmupDetectorClient
        {
            WarmupResponse = new WarmupResponse { Ok = true, Skipped = true, NSeen = 300, WarmedUp = true },
        };
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: historySource, detectorClient: detectorClient, connectionSettings: new ConnectionSettings());

        await pipeline.PrimeFromHistoryAsync("sensor.skip_twice", entityState, CancellationToken.None);
        Assert.Equal(1, detectorClient.WarmupCallCount);
        var firstNSeen = detectorClient.LastWarmupRequest is not null ? detectorClient.WarmupResponse.NSeen : 0;

        // Second run — e.g. a second stream-open attempt after an orchestrator restart.
        await pipeline.PrimeFromHistoryAsync("sensor.skip_twice", entityState, CancellationToken.None);

        Assert.Equal(2, detectorClient.WarmupCallCount); // +1 for this run — not a retry loop
        Assert.True(detectorClient.WarmupResponse.Skipped, "second run must also report skipped");
        Assert.Equal(firstNSeen, detectorClient.WarmupResponse.NSeen); // no growth — no re-backfill happened
    }

    [Fact]
    public async Task PrimeFromHistoryAsync_SkippedResponse_FrozenDetectorStillPrimed()
    {
        // SC-8 variant: FrozenDetector priming happens in the same loop that builds the
        // WarmupRequest, BEFORE the (possibly skipped) WarmupAsync response is known — so a
        // detector-side skip must never suppress the orchestrator's own frozen-window priming.
        var cfg = MakeEntitiesConfig("sensor.skip_frozen");
        var entityState = new EntityRuntimeState(HstParams.From(cfg.Entities[0].Detectors[0].Params));
        Assert.False(entityState.FrozenDetector.IsFrozen);

        // Constant-value history longer than the default frozen window (10).
        var history = MakeHistory(20, value: 21.0);
        var historySource = new FakeInfluxHistorySource(history);
        var detectorClient = new FakeWarmupDetectorClient
        {
            WarmupResponse = new WarmupResponse { Ok = true, Skipped = true, NSeen = 300, WarmedUp = true },
        };
        var pipeline = new ScoreStreamPipeline(
            new FakeStatePublisher(), NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), MakeGateway(),
            historySource: historySource, detectorClient: detectorClient, connectionSettings: new ConnectionSettings());

        await pipeline.PrimeFromHistoryAsync("sensor.skip_frozen", entityState, CancellationToken.None);

        Assert.True(
            entityState.FrozenDetector.IsFrozen,
            "Frozen detector must be primed from history even when the detector-side Warmup RPC reports skipped");
    }

    // ─── WS2: publish hygiene, legacy fallback, raw channel ──────────────────

    [Fact]
    public async Task OnVerdict_UnchangedFlagValueAcrossManyVerdicts_PublishesFlagExactlyOnce()
    {
        // F8, measured: ~4 lines of `Flag <entity> -> ...` every 15 s per entity, at
        // Information level, with the flag value unchanged the whole time. LastPublishedFlag
        // existed but was write-only. One hundred verdicts at a value that never changes must
        // produce exactly one publish — the initial explicit OFF.
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var state = MakeState(cfg.Entities[0].Detectors[0].Params);
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));

        for (int i = 0; i < 100; i++)
            await pipeline.ProcessVerdictAsync(
                MakeReading(), MakeVerdict(score: 0.42, warmedUp: true, nSeen: i + 1, window: 250),
                state, CancellationToken.None);

        Assert.Equal(1, publisher.FlagPublishCount);
    }

    [Fact]
    public async Task OnVerdict_FlagTransitionOffOnOff_PublishesAllThreeValuesInOrder()
    {
        // Change-only publishing must not become publish-once: every real transition still goes
        // out, in order. The OFF→ON→OFF sequence is the shape F1 says was impossible in the
        // field (five flags, zero genuine ON→OFF transitions in 24 h).
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var state = MakeState(FastAlertParams());
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));

        await DriveToFiringAsync(pipeline, state, "sensor.test");
        Assert.True(publisher.LastFlagValue);

        // Back into the middle of its own distribution → rank collapses → flag clears.
        for (int i = 0; i < 5; i++)
            await pipeline.ProcessVerdictAsync(
                MakeReading(), MakeVerdict(score: 0.50, warmedUp: true, nSeen: 100 + i, window: 1),
                state, CancellationToken.None);

        Assert.Equal(new[] { false, true, false }, publisher.FlagHistory);
    }

    [Fact]
    public async Task OnVerdict_LegacyMode_UsesHysteresisGateAndPublishesEveryTick()
    {
        // A13: alert_mode: legacy is the no-redeploy rollback path — an operator edits
        // /data/entities.yaml, the file watcher reloads, and the pre-WS2 behaviour is back:
        // absolute thresholds via HysteresisGate, published on every tick. If this branch ever
        // stops existing, the rollback story is a claim rather than a feature.
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var state = MakeState(new Dictionary<string, string>
        {
            ["window"] = "1",
            ["min_consecutive"] = "1",
            ["alert_mode"] = "legacy",
        });
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));

        for (int i = 0; i < 3; i++)
            await pipeline.ProcessVerdictAsync(
                MakeReading(), MakeVerdict(score: 0.9, warmedUp: true, nSeen: 1, window: 1),
                state, CancellationToken.None);

        Assert.Equal(3, publisher.FlagPublishCount);
        Assert.Equal(new[] { true, true, true }, publisher.FlagHistory);
    }

    [Fact]
    public async Task RunAsync_WriteLoop_FeedsRawChannelWithRealReadingValues()
    {
        // The verdict read loop constructs a SYNTHETIC HaReading with value 0.0 (it has no
        // reading in hand — only a verdict). If the raw-evidence channel were fed from there,
        // every robust-z would be computed against a constant zero and the channel that carries
        // sensor.lodowkababcia_power's real alarms would be silently dead. This test fails the
        // moment ObserveValue moves out of the write loop.
        var fakeCall = new OrderTrackingDuplexCall(new List<string>());
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig("sensor.raw");
        var state = MakeState(cfg.Entities[0].Detectors[0].Params);
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));

        using var cts = new CancellationTokenSource();
        var values = Enumerable.Range(0, 100).Select(i => MakeReading("sensor.raw", 100.0 + i % 5))
            .Append(MakeReading("sensor.raw", 984.0));
        var readings = AsyncEnumerableHelper.FromItems(values, cancellationToken: cts.Token);

        await pipeline.RunAsync(fakeCall, "sensor.raw", readings, state, cts.Token);

        Assert.Equal(101, state.Alert.RawSampleCount);
        Assert.True(state.Alert.LastRawZ > 1.0,
            $"Raw channel must see real reading values (z was {state.Alert.LastRawZ:F3})");
        Assert.Equal(984.0, state.LastValue);
    }

    [Fact]
    public async Task RecentAnomalies_OneHundredVerdictsInOneEvent_RecordsExactlyOneEntry()
    {
        // The Dashboard's "Recent anomalies" list is a list of EPISODES. Recording per verdict
        // meant a single firing entity flushed the 50-entry cache within minutes and buried
        // every other sensor's history under its own repetition.
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var state = MakeState(FastAlertParams());
        var recent = new RecentAnomaliesCache();
        var pipeline = new ScoreStreamPipeline(
            publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg), recentAnomalies: recent);

        await DriveToFiringAsync(pipeline, state, "sensor.test");

        // Stay inside the same event: 100 more verdicts, each a new maximum.
        for (int i = 0; i < 100; i++)
            await pipeline.ProcessVerdictAsync(
                MakeReading(), MakeVerdict(score: 1.0 + i, warmedUp: true, nSeen: 200 + i, window: 1),
                state, CancellationToken.None);

        Assert.Single(recent.GetRecent());
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
        services.AddSingleton<AlertStateStore>();
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
            sp.GetRequiredService<ConnectionSettings>(),
            sp.GetRequiredService<AlertStateStore>()));

        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<ScoreStreamPipeline>();

        Assert.NotNull(pipeline);
    }

    // ─── Detector selection on the wire (WS3 / D-A) ──────────────────────────

    /// <summary>
    /// The Python side dispatches on params["algorithm"] then params["detector"], falling back
    /// to "hst". So a migrated rmad entity whose Point carries no detector key is scored by the
    /// rarity detector against thresholds that mean something else entirely (0.5 on an HST
    /// rarity mass reads as "above the 50th percentile") — F0, restored in silence and with no
    /// log line. This test is the only thing standing between the migration and that state.
    /// </summary>
    [Fact]
    public void BuildDetectorParamsMap_RmadEntity_NamesTheDetectorAndItsWindowKeys()
    {
        var state = new EntityRuntimeState(RmadParams.From(
            Argus.Orchestrator.Web.DetectorDefaults.Get("rmad")!));

        var wire = ScoreStreamPipeline.BuildDetectorParamsMap(state);

        Assert.Equal("rmad", wire["detector"]);
        Assert.Equal("720", wire["window"]);
        Assert.Equal("60", wire["min_samples"]);
        Assert.Equal("0", wire["scale_floor"]);
        // n_trees is an HST-only knob; sending it would be noise the rmad detector strips.
        Assert.False(wire.ContainsKey("n_trees"));
    }

    /// <summary>
    /// The hst path stays byte-identical (D-F: hst is the rollback route, not a parity target),
    /// and in particular must NOT gain a "detector" key — servicer.py already defaults to hst.
    /// </summary>
    [Fact]
    public void BuildDetectorParamsMap_HstEntity_IsUnchanged()
    {
        var state = new EntityRuntimeState(new HstParams());

        var wire = ScoreStreamPipeline.BuildDetectorParamsMap(state);

        Assert.Equal(new[] { "n_trees", "window" }, wire.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("250", wire["window"]);
    }

    /// <summary>
    /// The sequence gate EntitiesSchemaMigrator reads before it rewrites entities.yaml. It is
    /// a constant only because the pipeline above genuinely resolves the detector from config;
    /// if anyone reverts BuildEntityStates to a literal "hst", this must go false with it, or
    /// the migration writes a config nothing honours.
    /// </summary>
    [Fact]
    public void SupportsRmad_IsTrue_BecauseBuildEntityStatesResolvesByName()
    {
        Assert.True(ScoreStreamPipeline.SupportsRmad);
    }

    // ─── F1 again, by a new route: the two loops racing on the published flag ─

    [Fact]
    public async Task FlagPublish_WriteLoopInterleavedWithReadLoop_StillPublishesTheNextOff()
    {
        // The verdict read loop and the write loop (PublishFrozenAsync) both decide "publish or
        // not" by comparing the flag they are about to send with the last one published. If that
        // compare and the matching set are not ONE critical section, the write loop can publish
        // ON and record it while the read loop — which compared before its own publish completed
        // — then records its own OFF over the top. The policy is left believing OFF is what HA
        // holds, so the NEXT genuine OFF is skipped as "unchanged", and the flag topic is
        // retained: the ON stays in HA until something else moves it. That is F1 with a new
        // mechanism, and this test is the only place the interleaving is forced.
        var publisher = new GatedFlagPublisher();
        var cfg = MakeEntitiesConfig();
        var state = MakeState(FastAlertParams());
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));

        // The write loop has fed the raw channel by the time it reaches PublishFrozenAsync, so
        // its forced ON is live here (see FrozenBranch_BeforeTheRawChannelIsReady_...).
        PrimeRawChannel(state);

        // Read loop: not warmed up → decision OFF, nothing published yet → publishes OFF and
        // parks inside the broker call, exactly where the two loops can interleave.
        var readLoop = pipeline.ProcessVerdictAsync(
            MakeReading(suppress: false), MakeVerdict(score: 0.9), state, CancellationToken.None);
        await publisher.FirstFlagInFlight;

        // Write loop, concurrently: a frozen reading forces the flag ON. It must WAIT until the
        // read loop's publish has left, because a publish that overtakes it puts the two
        // messages on the wire in the opposite order to the claims (next test).
        var writeLoop = pipeline.PublishFrozenAsync("sensor.test", state, CancellationToken.None);

        publisher.ReleaseFirstFlag();
        await readLoop;
        await writeLoop;

        // The next verdict still says OFF. HA is holding a retained ON, so this OFF MUST go out.
        await pipeline.ProcessVerdictAsync(
            MakeReading(suppress: false), MakeVerdict(score: 0.9), state, CancellationToken.None);

        Assert.Equal(new[] { false, true, false }, publisher.FlagHistory);
    }

    [Fact]
    public async Task FlagPublish_TwoLoopsRacing_LeavesTheBrokerAndThePolicyAgreeing()
    {
        // An atomic claim is not enough. Both loops claim under the policy's lock and then await
        // the broker OUTSIDE it, so the broker can receive the two messages in the opposite
        // order to the claims. The flag topic is retained, so the disagreement is permanent, in
        // both directions:
        //   claims OFF-then-ON, wire ON-then-OFF → HA OFF while the policy believes ON, and the
        //     next real ON is dropped as a duplicate: an alarm silently lost;
        //   claims ON-then-OFF, wire OFF-then-ON → HA lit for good against a policy that
        //     believes OFF, i.e. F1 back by another route.
        // The rule under test is therefore not "which value" but "the last value the BROKER saw
        // is the value the policy believes it published".
        var publisher = new CompletionOrderFlagPublisher();
        var cfg = MakeEntitiesConfig();
        var state = MakeState(FastAlertParams());
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));
        PrimeRawChannel(state);

        // Read loop claims OFF first and parks inside the broker call.
        var readLoop = pipeline.ProcessVerdictAsync(
            MakeReading(suppress: false), MakeVerdict(score: 0.9), state, CancellationToken.None);
        await publisher.FirstFlagInFlight;

        // Write loop tries to overtake it with ON while that OFF is still on the wire.
        var writeLoop = pipeline.PublishFrozenAsync("sensor.test", state, CancellationToken.None);

        publisher.ReleaseFirstFlag();
        await readLoop;
        await writeLoop;

        Assert.NotEmpty(publisher.CompletedFlags);
        Assert.Equal(state.Alert.LastPublishedFlag, publisher.CompletedFlags[publisher.CompletedFlags.Count - 1]);
    }

    [Fact]
    public async Task FrozenBranch_BeforeTheRawChannelIsReady_DoesNotContradictTheGate()
    {
        // frozen_window is operator-editable and independent of the raw channel's own
        // 10-sample floor. Set below it, the frozen detector latches while the gate still has
        // nothing to say — and the two loops then publish opposite values for the SAME readings:
        // the write loop forces ON, the verdict read loop publishes the gate's OFF, and the
        // retained flag flaps. The rule is not "who wins" but "the two loops never contradict
        // each other on one reading".
        var publisher = new FakeStatePublisher();
        var cfg = MakeEntitiesConfig();
        var state = MakeState(FastAlertParams());
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));

        // Read loop first: nothing is ready, so the gate says OFF and that OFF goes out.
        await pipeline.ProcessVerdictAsync(
            MakeReading(suppress: false), MakeVerdict(score: 0.9), state, CancellationToken.None);
        Assert.False(state.Alert.LastPublishedFlag);

        // Write loop, same reading, frozen detector already latched (frozen_window < 10).
        Assert.False(state.Alert.RawChannelReady, "Fixture must reproduce the short-window case");
        await pipeline.PublishFrozenAsync("sensor.test", state, CancellationToken.None);

        Assert.False(state.Alert.LastPublishedFlag,
            "The write loop must not publish an ON the gate is simultaneously publishing OFF");

        // Once the channel IS ready the guaranteed publish path is back — the wait is bounded,
        // not a silent loss of the frozen entity's only flag (D-H).
        PrimeRawChannel(state);
        await pipeline.PublishFrozenAsync("sensor.test", state, CancellationToken.None);
        Assert.True(state.Alert.LastPublishedFlag);
    }

    [Fact]
    public async Task FlagPublish_BrokerThrows_TransitionIsRetriedOnTheNextVerdict()
    {
        // The claim is taken BEFORE the publish, which is what makes the two loops safe against
        // each other -- but it also means a claim can be wrong. When the broker call throws
        // (EnsureConnected, a dropped MQTT session) the policy would otherwise be left believing
        // the transition was delivered and would never send it again. On a retained topic a
        // dropped OFF means HA stays lit indefinitely.
        var publisher = new FailingThenRecordingFlagPublisher(failFirst: true);
        var cfg = MakeEntitiesConfig();
        var state = MakeState(FastAlertParams());
        var pipeline = new ScoreStreamPipeline(publisher, NullLogger<ScoreStreamPipeline>.Instance, MakeLive(cfg));

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ProcessVerdictAsync(
            MakeReading(suppress: false), MakeVerdict(score: 0.9), state, CancellationToken.None));

        Assert.Null(state.Alert.LastPublishedFlag);

        // Same decision, next verdict: the transition must be attempted again, not skipped as
        // "already published".
        await pipeline.ProcessVerdictAsync(
            MakeReading(suppress: false), MakeVerdict(score: 0.9), state, CancellationToken.None);

        Assert.Equal(2, publisher.Attempts);
        Assert.Equal(new[] { false }, publisher.Published);
    }
}

// ─── Fakes ────────────────────────────────────────────────────────────────────

/// <summary>Fake StatePublisher that records calls without a live broker.</summary>
internal sealed class FakeStatePublisher : IStatePublisher
{
    public bool FlagPublished { get; private set; }
    public bool LastFlagValue { get; private set; }

    /// <summary>Number of flag publishes — the executable form of F8 (change-only publishing).</summary>
    public int FlagPublishCount { get; private set; }

    /// <summary>Every flag value published, in order, so transitions can be asserted as a sequence.</summary>
    public List<bool> FlagHistory { get; } = new();
    public bool ScorePublished { get; private set; }
    public double LastScoreValue { get; private set; }
    public bool AvailabilityPublished { get; private set; }
    public bool LastAvailabilityOnline { get; private set; }

    public Task PublishFlagAsync(string entityId, bool on, CancellationToken ct)
    {
        FlagPublished = true;
        LastFlagValue = on;
        FlagPublishCount++;
        FlagHistory.Add(on);
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

    /// <summary>Flag publishes per entity — proves the frozen path publishes change-only (F8).</summary>
    public Dictionary<string, int> FlagPublishCounts { get; } = new();

    /// <summary>Entities that had a flag published but never a score — must be empty.</summary>
    public IReadOnlyCollection<string> FlaggedEntitiesWithoutScore()
        => FlaggedEntities.Where(e => !ScoredEntities.Contains(e)).ToList();

    public Task PublishFlagAsync(string entityId, bool on, CancellationToken ct)
    {
        FlaggedEntities.Add(entityId);
        FlagPublishCounts[entityId] = FlagPublishCounts.GetValueOrDefault(entityId) + 1;
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
/// Fake duplex call whose first write fails with a configured RpcException — exercises the
/// stream failure handling in RunEntityStreamAsync without a live gRPC channel.
/// </summary>
internal sealed class ThrowingScoreStreamCall : IScoreStreamCall
{
    private readonly RpcException _error;

    public ThrowingScoreStreamCall(RpcException error) => _error = error;

    public Task WriteAsync(Point point, CancellationToken ct) => throw _error;

    public Task CompleteAsync() => Task.CompletedTask;

    public async IAsyncEnumerable<Verdict> ReadAllVerdictsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
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
    }

    public Task WriteAsync(Point point, CancellationToken ct)
    {
        WrittenPoints.Add(point);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Half-close. Like a real gRPC duplex call, the response stream ends only once the client
    /// has half-closed — so the read loop cannot finish before this runs. That is precisely why
    /// PITFALL 3 exists: awaiting readTask first would hang forever.
    /// </summary>
    public Task CompleteAsync()
    {
        Record("CompleteAsync");
        _verdicts.Writer.Complete();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<Verdict> ReadAllVerdictsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var v in _verdicts.Reader.ReadAllAsync(ct))
            yield return v;
        Record("ReadTaskDone");
    }

    // The read loop runs on a thread-pool thread; the write loop on the caller's. Both append
    // here, so the list needs a lock even though the ordering itself is now deterministic.
    private void Record(string step)
    {
        lock (_order) _order.Add(step);
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

    /// <summary>Row count the pipeline asked for on the last call (WS5 baseline-window assertion).</summary>
    public int LastLimit { get; private set; }

    public Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryAsync(
        string entityId, CancellationToken ct)
        => Task.FromResult(_rows);

    public Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryHistoryAsync(
        string entityId, string lookback, int limit, CancellationToken ct)
    {
        QueryHistoryCallCount++;
        LastLimit = limit;
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

/// <summary>
/// Publisher that records flag values in the order their publish COMPLETED -- i.e. the order the
/// broker, and therefore HA's retained topic, actually sees them. For a retained flag that is the
/// only order that matters. The first publish parks until the test releases it.
/// </summary>
internal sealed class CompletionOrderFlagPublisher : IStatePublisher
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _flagCalls;

    /// <summary>Flag values in COMPLETION order.</summary>
    public List<bool> CompletedFlags { get; } = new();

    /// <summary>Completes once the first flag publish is in flight and parked.</summary>
    public Task FirstFlagInFlight => _entered.Task;

    public void ReleaseFirstFlag() => _release.TrySetResult();

    public async Task PublishFlagAsync(string entityId, bool on, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _flagCalls) == 1)
        {
            _entered.TrySetResult();
            await _release.Task;
        }

        lock (CompletedFlags)
            CompletedFlags.Add(on);
    }

    public Task PublishScoreAsync(string entityId, double score, CancellationToken ct) => Task.CompletedTask;
    public Task PublishAvailabilityAsync(string entityId, bool online, CancellationToken ct) => Task.CompletedTask;
    public Task PublishGroupFlagAsync(string groupId, string? memberId, bool on, CancellationToken ct) => Task.CompletedTask;
    public Task PublishGroupScoreAsync(string groupId, string? memberId, double score, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Publisher whose first flag publish throws, so a failed claim can be observed.</summary>
internal sealed class FailingThenRecordingFlagPublisher : IStatePublisher
{
    private bool _failNext;

    public FailingThenRecordingFlagPublisher(bool failFirst) => _failNext = failFirst;

    /// <summary>Every flag publish attempt, successful or not.</summary>
    public int Attempts { get; private set; }

    /// <summary>Flag values that actually reached the broker.</summary>
    public List<bool> Published { get; } = new();

    public Task PublishFlagAsync(string entityId, bool on, CancellationToken ct)
    {
        Attempts++;
        if (_failNext)
        {
            _failNext = false;
            throw new InvalidOperationException("MQTT client is not connected");
        }

        Published.Add(on);
        return Task.CompletedTask;
    }

    public Task PublishScoreAsync(string entityId, double score, CancellationToken ct) => Task.CompletedTask;
    public Task PublishAvailabilityAsync(string entityId, bool online, CancellationToken ct) => Task.CompletedTask;
    public Task PublishGroupFlagAsync(string groupId, string? memberId, bool on, CancellationToken ct) => Task.CompletedTask;
    public Task PublishGroupScoreAsync(string groupId, string? memberId, double score, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Publisher whose FIRST flag publish parks until the test releases it, so the interleaving of
/// the verdict read loop and the write loop's PublishFrozenAsync can be reproduced exactly
/// instead of being waited for.
/// </summary>
internal sealed class GatedFlagPublisher : IStatePublisher
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _flagCalls;

    /// <summary>Every flag value published, in the order the publish was ISSUED.</summary>
    public List<bool> FlagHistory { get; } = new();

    /// <summary>Completes once the first flag publish is in flight and parked.</summary>
    public Task FirstFlagInFlight => _entered.Task;

    public void ReleaseFirstFlag() => _release.TrySetResult();

    public async Task PublishFlagAsync(string entityId, bool on, CancellationToken ct)
    {
        bool first = Interlocked.Increment(ref _flagCalls) == 1;
        lock (FlagHistory)
            FlagHistory.Add(on);

        if (first)
        {
            _entered.TrySetResult();
            await _release.Task;
        }
    }

    public Task PublishScoreAsync(string entityId, double score, CancellationToken ct) => Task.CompletedTask;
    public Task PublishAvailabilityAsync(string entityId, bool online, CancellationToken ct) => Task.CompletedTask;
    public Task PublishGroupFlagAsync(string groupId, string? memberId, bool on, CancellationToken ct) => Task.CompletedTask;
    public Task PublishGroupScoreAsync(string groupId, string? memberId, double score, CancellationToken ct) => Task.CompletedTask;
}
