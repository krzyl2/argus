using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace Argus.Orchestrator.Workers;

/// <summary>
/// BackgroundService that gates on detector health (INFRA-07) then consumes the HA event
/// stream via IHaEventSource and forwards readings to the scoring pipeline.
/// Plan 07 replaces the TODO body with the full ScoreStream + frozen/hysteresis/MQTT path.
/// </summary>
public class HaListenerWorker : BackgroundService
{
    private readonly IHaEventSource _haEventSource;
    private readonly DetectionGateway _gateway;
    private readonly ILogger<HaListenerWorker> _logger;

    // Bounded channel: buffers HaReadings between ingestion and scoring (Plan 07 consumer)
    private readonly Channel<HaReading> _readingChannel = Channel.CreateBounded<HaReading>(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = false,
        });

    public HaListenerWorker(
        IHaEventSource haEventSource,
        DetectionGateway gateway,
        ILogger<HaListenerWorker> logger)
    {
        _haEventSource = haEventSource ?? throw new ArgumentNullException(nameof(haEventSource));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Log(LogLevel.Information, LogEvents.HaListenerStarting,
            "HaListenerWorker starting — waiting for detector health gate (INFRA-07)");

        // INFRA-07: block until detector is SERVING before subscribing to HA events
        await _gateway.WaitForHealthyAsync(stoppingToken);

        if (stoppingToken.IsCancellationRequested)
            return;

        _logger.Log(LogLevel.Information, LogEvents.HaListenerDetectorHealthy,
            "Detector healthy — starting HA event source");

        await foreach (var reading in _haEventSource.ReadAllAsync(stoppingToken))
        {
            _logger.LogDebug(
                "HA reading: entity={EntityId} value={Value} suppress={Suppress}",
                reading.EntityId, reading.Value, reading.SuppressBinarySensor);

            // Forward to the scoring pipeline channel
            if (!_readingChannel.Writer.TryWrite(reading))
            {
                _logger.LogWarning(
                    "Reading channel full — dropping {EntityId}", reading.EntityId);
            }

            // TODO(plan07): forward reading to ScoreStream + frozen/hysteresis/MQTT pipeline
        }
    }

    /// <summary>
    /// Exposes the reading channel reader for Plan 07's scoring pipeline to consume.
    /// </summary>
    public ChannelReader<HaReading> Readings => _readingChannel.Reader;
}
