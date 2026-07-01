using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Logging;
using Argus.Orchestrator.Mqtt;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Argus.Orchestrator.Workers;

/// <summary>
/// BackgroundService that gates on detector health (INFRA-07) then runs an inner-CTS
/// restart loop: on <see cref="ILiveEntitiesConfig.ConfigChanged"/>, cancels the inner
/// CancellationTokenSource and re-enters <see cref="ScoreStreamPipeline.RunAsync"/>.
///
/// MQTT and gRPC singletons stay alive across reloads — only the streaming pipeline is
/// restarted. On each reload iteration: removed entities are retracted from MQTT and
/// newly-added entities are re-published via <see cref="DiscoveryPublisher"/> (discovery +
/// availability) before the new pipeline starts (CFG-04).
///
/// Correctness invariants (from 03-RESEARCH.md Q2 / Pitfalls 1/3):
/// - ConfigChanged handler null-checks the inner CTS (Pitfall 1 — rapid saves).
/// - null-before-Dispose pattern in finally (Pitfall 3 — ObjectDisposedException guard).
/// - OCE when stoppingToken.IsCancellationRequested is NOT caught → clean host shutdown.
/// </summary>
public class HaListenerWorker : BackgroundService
{
    private readonly IHaEventSource _haEventSource;
    private readonly DetectionGateway _gateway;
    private readonly ScoreStreamPipeline? _scoreStreamPipeline;
    private readonly ILiveEntitiesConfig _liveConfig;
    private readonly MqttConnection? _mqtt;
    private readonly ILogger<HaListenerWorker> _logger;

    public HaListenerWorker(
        IHaEventSource haEventSource,
        DetectionGateway gateway,
        ILiveEntitiesConfig liveConfig,
        ScoreStreamPipeline scoreStreamPipeline,
        MqttConnection mqtt,
        ILogger<HaListenerWorker> logger)
    {
        _haEventSource = haEventSource ?? throw new ArgumentNullException(nameof(haEventSource));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _liveConfig = liveConfig ?? throw new ArgumentNullException(nameof(liveConfig));
        _scoreStreamPipeline = scoreStreamPipeline ?? throw new ArgumentNullException(nameof(scoreStreamPipeline));
        _mqtt = mqtt; // nullable — tests may omit for retraction-free scenarios
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Protected constructor for test subclasses. Allows null <paramref name="gateway"/>
    /// when <see cref="WaitForDetectorHealthAsync"/> is overridden, and null
    /// <paramref name="scoreStreamPipeline"/> when <see cref="RunPipelineAsync"/> is overridden.
    /// </summary>
    protected HaListenerWorker(
        IHaEventSource haEventSource,
        DetectionGateway? gateway,
        ILiveEntitiesConfig liveConfig,
        ScoreStreamPipeline? scoreStreamPipeline,
        ILogger<HaListenerWorker> logger)
    {
        _haEventSource = haEventSource ?? throw new ArgumentNullException(nameof(haEventSource));
        _gateway = gateway!; // null allowed when WaitForDetectorHealthAsync is overridden in tests
        _liveConfig = liveConfig ?? throw new ArgumentNullException(nameof(liveConfig));
        _scoreStreamPipeline = scoreStreamPipeline;
        _mqtt = null;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Log(LogLevel.Information, LogEvents.HaListenerStarting,
            "HaListenerWorker starting — waiting for detector health gate (INFRA-07)");

        // INFRA-07: block until detector is SERVING before subscribing to HA events
        await WaitForDetectorHealthAsync(stoppingToken);

        if (stoppingToken.IsCancellationRequested)
            return;

        _logger.Log(LogLevel.Information, LogEvents.HaListenerDetectorHealthy,
            "Detector healthy — starting ScoreStreamPipeline restart loop (CFG-04)");

        EntitiesConfig? lastConfig = null;
        CancellationTokenSource? innerCts = null;

        // Subscribe to config changes BEFORE the loop — fires on the save-endpoint request thread.
        // Handler is null-safe: captures innerCts by field reference; null check handles the
        // brief window between innerCts = null (in finally) and new CTS assignment (Pitfall 1).
        void OnConfigChanged(object? sender, EventArgs e)
        {
            var cts = innerCts;
            if (cts is not null && !cts.IsCancellationRequested)
                cts.Cancel();
        }
        _liveConfig.ConfigChanged += OnConfigChanged;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var currentConfig = _liveConfig.Get();

                // Retraction + re-publish diff: run before creating the new inner CTS so the
                // broker calls use stoppingToken (not innerCts) and survive the pipeline cancel.
                // Only after the first iteration (lastConfig == null → skip on startup).
                if (lastConfig is not null)
                {
                    await RetractAndRepublishAsync(lastConfig, currentConfig, stoppingToken);
                }

                lastConfig = currentConfig;

                // Create a fresh inner CTS linked to stoppingToken for this iteration.
                innerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                try
                {
                    _logger.Log(LogLevel.Information, LogEvents.ConfigReloadTriggered,
                        "HaListenerWorker — (re)starting pipeline");

                    await RunPipelineAsync(
                        _haEventSource.ReadAllAsync(innerCts.Token),
                        innerCts.Token);

                    // RunAsync completed without cancellation — host is shutting down or
                    // pipeline exited cleanly. Exit the loop.
                    break;
                }
                catch (OperationCanceledException) when (
                    innerCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    // Inner CTS was cancelled (config reload) — loop re-enters for restart.
                    // stoppingToken is NOT cancelled → this is a CFG-04 reload, not shutdown.
                    _logger.LogInformation(LogEvents.ConfigReloadComplete,
                        "HaListenerWorker — config reload triggered; restarting pipeline");
                }
                finally
                {
                    // Pitfall 3: null the field BEFORE Dispose so OnConfigChanged never
                    // calls Cancel() on a disposed CancellationTokenSource.
                    var toDispose = innerCts;
                    innerCts = null;
                    toDispose?.Dispose();
                }
            }
        }
        finally
        {
            _liveConfig.ConfigChanged -= OnConfigChanged;
        }
    }

    // ─── Virtual seams for testability ────────────────────────────────────────

    /// <summary>
    /// Waits for the detector health gate. Virtual for test overrides (pass-through).
    /// Production path delegates to <see cref="DetectionGateway.WaitForHealthyAsync"/>.
    /// </summary>
    protected virtual Task WaitForDetectorHealthAsync(CancellationToken ct)
        => _gateway.WaitForHealthyAsync(ct);

    /// <summary>
    /// Runs the scoring pipeline for the given readings stream.
    /// Virtual to allow test subclasses to inject a recording seam instead of the real pipeline.
    /// Production path delegates to <see cref="ScoreStreamPipeline.RunAsync"/>.
    /// </summary>
    protected virtual Task RunPipelineAsync(IAsyncEnumerable<HaReading> readings, CancellationToken ct)
    {
        if (_scoreStreamPipeline is null)
            throw new InvalidOperationException("ScoreStreamPipeline is not configured. Use the production constructor.");
        return _scoreStreamPipeline.RunAsync(readings, ct);
    }

    /// <summary>
    /// Publishes a single MQTT message. Virtual to allow test subclasses to capture publishes
    /// without a live broker. Production path delegates to <see cref="MqttConnection.PublishAsync"/>.
    /// </summary>
    protected virtual Task RetractPublishAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        if (_mqtt is null)
            return Task.CompletedTask; // no broker configured — skip silently
        return _mqtt.PublishAsync(topic, payload, retain, ct);
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Diff old vs new entity sets and:
    /// 1. Retract removed entities (empty retained payload to discovery topics).
    /// 2. Re-publish discovery for added entities (idempotent retained payload).
    /// Uses <paramref name="stoppingToken"/> so broker calls survive the inner-CTS cancel.
    /// </summary>
    private async Task RetractAndRepublishAsync(
        EntitiesConfig oldConfig,
        EntitiesConfig newConfig,
        CancellationToken stoppingToken)
    {
        var newIds = newConfig.Entities
            .Select(e => e.EntityId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var oldIds = oldConfig.Entities
            .Select(e => e.EntityId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Removed entities: in old but not in new
        var removed = oldConfig.Entities
            .Where(e => !newIds.Contains(e.EntityId))
            .ToList();

        if (removed.Count > 0)
        {
            await DiscoveryPublisher.RetractAsync(
                (topic, payload, retain, ct) => RetractPublishAsync(topic, payload, retain, ct),
                removed,
                stoppingToken);

            _logger.LogInformation(LogEvents.MqttRetractionPublished,
                "Retracted {Count} removed entities from MQTT discovery", removed.Count);
        }

        // Added entities: in new but not in old — republish discovery so HA shows them
        var added = newConfig.Entities
            .Where(e => !oldIds.Contains(e.EntityId))
            .ToList();

        if (added.Count > 0)
        {
            await DiscoveryPublisher.PublishAllAsync(
                (topic, payload, retain, ct) => RetractPublishAsync(topic, payload, retain, ct),
                added,
                stoppingToken);

            _logger.LogInformation(LogEvents.MqttDiscoveryPublished,
                "Republished discovery for {Count} added entities", added.Count);
        }
    }
}
