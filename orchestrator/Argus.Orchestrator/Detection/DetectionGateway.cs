using Argus.Detector.V1;
using Argus.Orchestrator.Logging;
using Grpc.Health.V1;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace Argus.Orchestrator.Detection;

/// <summary>
/// Holds the singleton mTLS GrpcChannel and exposes typed gRPC clients.
/// Implements the INFRA-07 health gate: WaitForHealthyAsync blocks until detector is SERVING.
/// Backoff: 1s, 2s, 4s, 8s, max 60s — satisfies RES-03 re-establish requirement.
/// T-04-04: Backoff prevents tight-loop hammering when detector is unavailable.
/// T-04-05: OBS-01 structured logs for each health attempt and channel establish.
/// </summary>
public class DetectionGateway : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly ILogger<DetectionGateway> _logger;

    public DetectorService.DetectorServiceClient DetectorClient { get; }
    public Health.HealthClient HealthClient { get; }

    public DetectionGateway(GrpcChannel channel, ILogger<DetectionGateway> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Create stubs from the single shared channel (anti-pattern: multiple channels)
        DetectorClient = new DetectorService.DetectorServiceClient(_channel);
        HealthClient = new Health.HealthClient(_channel);
    }

    /// <summary>
    /// Polls grpc.health.v1 Health/Check until the detector reports SERVING or cancellation.
    /// INFRA-07 gate — must be called before subscribing to HA state_changed events.
    /// </summary>
    /// <returns>True when SERVING; throws OperationCanceledException on cancellation.</returns>
    public async Task<bool> WaitForHealthyAsync(CancellationToken ct)
    {
        var delays = new[] { 1, 2, 4, 8, 16, 30, 60 };
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.Log(LogLevel.Information, LogEvents.StartupHealthCheck,
                    "Health check attempt {Attempt} for argus.v1.DetectorService",
                    attempt + 1);

                var deadline = DateTime.UtcNow.AddSeconds(5);
                var resp = await HealthClient.CheckAsync(
                    new HealthCheckRequest { Service = "argus.v1.DetectorService" },
                    deadline: deadline,
                    cancellationToken: ct);

                if (resp.Status == HealthCheckResponse.Types.ServingStatus.Serving)
                {
                    _logger.Log(LogLevel.Information, LogEvents.StartupHealthCheckServing,
                        "Detector is SERVING — health gate passed on attempt {Attempt}",
                        attempt + 1);
                    return true;
                }

                _logger.Log(LogLevel.Warning, LogEvents.StartupHealthCheckNotServing,
                    "Detector health check returned {Status} on attempt {Attempt}",
                    resp.Status, attempt + 1);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Warning, LogEvents.StartupHealthCheckNotServing,
                    ex, "Detector health check failed on attempt {Attempt}: {Message}",
                    attempt + 1, ex.Message);
            }

            // Exponential backoff with max cap
            int delaySeconds = attempt < delays.Length ? delays[attempt] : delays[^1];
            attempt++;

            _logger.Log(LogLevel.Information, LogEvents.StartupHealthCheckRetry,
                "Retrying health check in {DelaySeconds}s (attempt {Next})",
                delaySeconds, attempt + 1);

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
        }

        ct.ThrowIfCancellationRequested();
        return false;
    }

    public void Dispose()
    {
        _channel.Dispose();
    }
}
