using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Argus.Orchestrator.Workers;

/// <summary>
/// BackgroundService stub that gates on detector health before starting HA subscription.
/// Plan 05 replaces the TODO body with real HA WebSocket subscription logic.
/// Constructor signature is stable — Program.cs DI wiring does not change in Plan 05.
/// </summary>
public class HaListenerWorker : BackgroundService
{
    private readonly DetectionGateway _gateway;
    private readonly ILogger<HaListenerWorker> _logger;

    public HaListenerWorker(DetectionGateway gateway, ILogger<HaListenerWorker> logger)
    {
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
            "Detector healthy — HA subscription will start");

        // TODO(plan05): subscribe to HA state_changed events
        // Plan 05 replaces this loop with NetDaemon.Client IHomeAssistantRunner subscription
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
