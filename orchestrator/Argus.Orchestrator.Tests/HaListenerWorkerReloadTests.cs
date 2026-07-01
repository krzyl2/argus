using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Mqtt;
using Argus.Orchestrator.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for HaListenerWorker inner-CTS restart loop (CFG-04).
/// Covers: reload restarts RunAsync via ConfigChanged → inner CTS cancel;
/// stoppingToken exits the loop; retraction fires for removed entities;
/// no ObjectDisposedException under rapid ConfigChanged events (Pitfall 3).
/// </summary>
public class HaListenerWorkerReloadTests
{
    // ─── Fakes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fake HA event source that blocks until its CancellationToken is cancelled.
    /// </summary>
    private sealed class FakeHaEventSource : IHaEventSource
    {
        public async IAsyncEnumerable<HaReading> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            // Block indefinitely — only way to exit is via cancellation
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // swallow here — outer loop handles it
            }
            yield break;
        }
    }

    /// <summary>
    /// Seam interface for injecting a controllable RunAsync into HaListenerWorker tests.
    /// Introduced because ScoreStreamPipeline is sealed and cannot be subclassed.
    /// </summary>
    public interface IScoreStreamRunner
    {
        Task RunAsync(IAsyncEnumerable<HaReading> readings, CancellationToken ct);
    }

    /// <summary>
    /// Recording implementation: counts invocations, blocks until ct cancelled.
    /// </summary>
    private sealed class RecordingRunner : IScoreStreamRunner
    {
        private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => _callCount;
        public Task FirstStarted => _firstStarted.Task;

        public async Task RunAsync(IAsyncEnumerable<HaReading> readings, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            _firstStarted.TrySetResult();

            // Block until cancelled — simulates the real pipeline's indefinite run
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static EntitiesConfig MakeConfig(params string[] entityIds)
    {
        var cfg = new EntitiesConfig();
        foreach (var id in entityIds)
            cfg.Entities.Add(new EntityConfig
            {
                EntityId = id,
                Detectors = [new DetectorConfig { Name = "hst" }],
            });
        return cfg;
    }

    private static ILiveEntitiesConfig MakeLive(EntitiesConfig cfg) => new LiveEntitiesConfig(cfg);

    // ─── Tests ───────────────────────────────────────────────────────────────

    /// <summary>
    /// CFG-04 core: ConfigChanged fires → inner CTS cancelled → RunAsync restarts.
    /// Asserts RunAsync is called a second time after a ConfigChanged event.
    /// </summary>
    [Fact]
    public async Task ConfigChanged_RestartsRunAsync()
    {
        var liveConfig = new LiveEntitiesConfig(MakeConfig("sensor.a"));
        var runner = new RecordingRunner();
        var eventSource = new FakeHaEventSource();

        using var stoppingCts = new CancellationTokenSource();

        var worker = new TestableHaListenerWorker(
            eventSource,
            liveConfig,
            runner,
            null,
            NullLogger<HaListenerWorker>.Instance);

        // Start worker
        var workerTask = worker.StartAsync(stoppingCts.Token);

        // Wait for first RunAsync invocation
        await runner.FirstStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, runner.CallCount);

        // Fire ConfigChanged → should trigger a second RunAsync
        liveConfig.Swap(MakeConfig("sensor.b"));

        // Wait for second invocation (brief polling)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (runner.CallCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(runner.CallCount >= 2, $"Expected >= 2 RunAsync calls after reload, got {runner.CallCount}");

        // Clean shutdown
        await stoppingCts.CancelAsync();
        await workerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// stoppingToken cancelled → loop exits cleanly without re-entering RunAsync.
    /// </summary>
    [Fact]
    public async Task StoppingToken_ExitsLoop()
    {
        var liveConfig = new LiveEntitiesConfig(MakeConfig("sensor.a"));
        var runner = new RecordingRunner();
        var eventSource = new FakeHaEventSource();

        using var stoppingCts = new CancellationTokenSource();

        var worker = new TestableHaListenerWorker(
            eventSource,
            liveConfig,
            runner,
            null,
            NullLogger<HaListenerWorker>.Instance);

        var workerTask = worker.StartAsync(stoppingCts.Token);

        // Wait for first RunAsync
        await runner.FirstStarted.WaitAsync(TimeSpan.FromSeconds(5));

        // Cancel host stopping token — should exit loop
        await stoppingCts.CancelAsync();

        // Worker task should complete cleanly within a reasonable timeout
        var completed = await Task.WhenAny(workerTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(workerTask, completed);

        // Call count must be exactly 1 — loop exited without restart
        Assert.Equal(1, runner.CallCount);
    }

    /// <summary>
    /// Retraction fires for entities present in old config but absent from new config.
    /// Asserts that retraction publishes empty retained payloads for removed entity topics.
    /// </summary>
    [Fact]
    public async Task ConfigChanged_RetractsRemovedEntities()
    {
        var initialConfig = MakeConfig("sensor.removed", "sensor.kept");
        var liveConfig = new LiveEntitiesConfig(initialConfig);
        var mqttCapture = new TopicCapturingPublisher();

        var runner = new RecordingRunner();
        var eventSource = new FakeHaEventSource();

        using var stoppingCts = new CancellationTokenSource();

        var worker = new TestableHaListenerWorker(
            eventSource,
            liveConfig,
            runner,
            mqttCapture.PublishAsync,
            NullLogger<HaListenerWorker>.Instance);

        var workerTask = worker.StartAsync(stoppingCts.Token);

        // Wait for first RunAsync
        await runner.FirstStarted.WaitAsync(TimeSpan.FromSeconds(5));

        // Swap to config with sensor.removed gone
        liveConfig.Swap(MakeConfig("sensor.kept"));

        // Wait for second RunAsync (restart)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (runner.CallCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        // sensor.removed should have had its discovery topics retracted (empty retained)
        var retracted = mqttCapture.EmptyRetainedPublishes;
        Assert.Contains(retracted, t => t.Contains("sensor_removed"));

        await stoppingCts.CancelAsync();
        await workerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Two rapid ConfigChanged events must not throw ObjectDisposedException (Pitfall 3 / T-03-08).
    /// The null-before-dispose pattern in HaListenerWorker.ExecuteAsync finally block ensures
    /// the ConfigChanged handler never calls Cancel() on a disposed CTS.
    /// </summary>
    [Fact]
    public async Task RapidConfigChanged_NoObjectDisposedException()
    {
        var liveConfig = new LiveEntitiesConfig(MakeConfig("sensor.a"));
        var runner = new RecordingRunner();
        var eventSource = new FakeHaEventSource();

        using var stoppingCts = new CancellationTokenSource();

        var worker = new TestableHaListenerWorker(
            eventSource,
            liveConfig,
            runner,
            null,
            NullLogger<HaListenerWorker>.Instance);

        var workerTask = worker.StartAsync(stoppingCts.Token);

        // Wait for first RunAsync
        await runner.FirstStarted.WaitAsync(TimeSpan.FromSeconds(5));

        // Fire two rapid ConfigChanged events — must not ObjectDisposedException
        var exceptions = new List<Exception>();
        var t1 = Task.Run(() => { try { liveConfig.Swap(MakeConfig("sensor.b")); } catch (Exception ex) { exceptions.Add(ex); } });
        var t2 = Task.Run(() => { try { liveConfig.Swap(MakeConfig("sensor.c")); } catch (Exception ex) { exceptions.Add(ex); } });
        await Task.WhenAll(t1, t2);

        await Task.Delay(300); // let restarts cycle

        Assert.Empty(exceptions);

        await stoppingCts.CancelAsync();
        await workerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

// ─── Test seam infrastructure ─────────────────────────────────────────────────

/// <summary>
/// Captures MQTT publishes with empty retained payloads (retraction calls).
/// </summary>
internal sealed class TopicCapturingPublisher
{
    private readonly List<string> _emptyRetained = new();

    public IReadOnlyList<string> EmptyRetainedPublishes => _emptyRetained;

    public Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        if (retain && payload == string.Empty)
            _emptyRetained.Add(topic);
        return Task.CompletedTask;
    }
}

/// <summary>
/// HaListenerWorker subclass with injected runner and publish seams for testing.
/// - Overrides WaitForDetectorHealthAsync to pass through immediately (no real gRPC call).
/// - Overrides RunPipelineAsync to delegate to the injected IScoreStreamRunner.
/// - Overrides RetractPublishAsync to delegate to the injected publish delegate.
/// </summary>
internal sealed class TestableHaListenerWorker : HaListenerWorker
{
    private readonly HaListenerWorkerReloadTests.IScoreStreamRunner _runner;
    private readonly Func<string, string, bool, CancellationToken, Task>? _mqttPublish;

    public TestableHaListenerWorker(
        IHaEventSource haEventSource,
        ILiveEntitiesConfig liveConfig,
        HaListenerWorkerReloadTests.IScoreStreamRunner runner,
        Func<string, string, bool, CancellationToken, Task>? mqttPublish,
        ILogger<HaListenerWorker> logger)
        : base(haEventSource, (DetectionGateway?)null, liveConfig, (ScoreStreamPipeline?)null, logger)
    {
        _runner = runner;
        _mqttPublish = mqttPublish;
    }

    // Health gate passes immediately in tests
    protected override Task WaitForDetectorHealthAsync(CancellationToken ct) => Task.CompletedTask;

    // Delegate pipeline run to the recording runner
    protected override Task RunPipelineAsync(IAsyncEnumerable<HaReading> readings, CancellationToken ct)
        => _runner.RunAsync(readings, ct);

    // Capture retraction/republish calls if a delegate is provided
    protected override Task RetractPublishAsync(string topic, string payload, bool retain, CancellationToken ct)
        => _mqttPublish is not null
            ? _mqttPublish(topic, payload, retain, ct)
            : Task.CompletedTask;
}
