using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Argus.Orchestrator.Workers;

/// <summary>
/// BackgroundService that gates on detector health (INFRA-07) then consumes the HA event
/// stream via IHaEventSource and forwards readings to ScoreStreamPipeline (Plan 08).
/// </summary>
public class HaListenerWorker : BackgroundService
{
    private readonly IHaEventSource _haEventSource;
    private readonly DetectionGateway _gateway;
    private readonly ScoreStreamPipeline _scoreStreamPipeline;
    private readonly ILogger<HaListenerWorker> _logger;

    public HaListenerWorker(
        IHaEventSource haEventSource,
        DetectionGateway gateway,
        ScoreStreamPipeline scoreStreamPipeline,
        ILogger<HaListenerWorker> logger)
    {
        _haEventSource = haEventSource ?? throw new ArgumentNullException(nameof(haEventSource));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _scoreStreamPipeline = scoreStreamPipeline ?? throw new ArgumentNullException(nameof(scoreStreamPipeline));
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
            "Detector healthy — starting ScoreStreamPipeline (Plan 08)");

        // Run the end-to-end pipeline: HA reading -> bidi ScoreStream -> hysteresis/frozen -> MQTT
        // On RpcException the pipeline marks entities unavailable and the exception propagates here;
        // BackgroundService will log and respect cancellation on host shutdown.
        await _scoreStreamPipeline.RunAsync(_haEventSource.ReadAllAsync(stoppingToken), stoppingToken);
    }
}
