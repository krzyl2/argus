using System.Text;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// MQTTnet 5 client wrapper with LWT-before-connect (PITFALL 6) and exponential backoff reconnect.
/// Uses MqttClientFactory (NOT MqttFactory — D-17/RESEARCH state-of-art).
/// Bridge-level LWT: argus/bridge/availability → offline configured in connect options
/// BEFORE ConnectAsync, so an orchestrator crash marks ALL sensors unavailable (RES-01).
/// </summary>
public sealed class MqttConnection : IAsyncDisposable
{
    public const string BridgeAvailabilityTopic = "argus/bridge/availability";

    private readonly ConnectionSettings _settings;
    private readonly ILogger<MqttConnection> _logger;
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _connectOptions;
    private readonly CancellationTokenSource _cts = new();

    public MqttConnection(ConnectionSettings settings, ILogger<MqttConnection> logger)
    {
        _settings = settings;
        _logger = logger;

        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();

        // LWT configured in options BEFORE ConnectAsync is ever called (PITFALL 6, D-15)
        // Bridge-level availability: one LWT covers all entities; orchestrator crash → all unavailable
        _connectOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.MqttHost ?? "localhost", _settings.MqttPort)
            .WithCredentials(_settings.MqttUser, _settings.MqttPassword)
            .WithWillTopic(BridgeAvailabilityTopic)
            .WithWillPayload("offline")
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        // Wire reconnect handler
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    /// <summary>
    /// Connects to the broker, then immediately publishes "online" to availability topic.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        await _client.ConnectAsync(_connectOptions, ct);
        _logger.LogInformation(LogEvents.MqttConnected, "MQTT connected to {Host}:{Port}", _settings.MqttHost, _settings.MqttPort);
        await PublishOnlineAsync(ct);
    }

    /// <summary>
    /// Publishes a message with optional retain flag, QoS AtLeastOnce.
    /// </summary>
    public async Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.PublishAsync(message, ct);
    }

    /// <summary>Exposes the raw MqttClientOptions for test assertions (no live broker needed).</summary>
    public MqttClientOptions ConnectOptions => _connectOptions;

    private async Task PublishOnlineAsync(CancellationToken ct)
    {
        await PublishAsync(BridgeAvailabilityTopic, "online", retain: true, ct);
        _logger.LogInformation(LogEvents.MqttBridgeOnline, "MQTT bridge availability published: online");
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        if (args.ClientWasConnected)
        {
            _logger.LogWarning(LogEvents.MqttDisconnected, "MQTT disconnected: {Reason}", args.Reason);
        }

        // Exponential backoff with jitter (Claude's Discretion)
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(60);

        while (true)
        {
            try
            {
                // Add jitter: ±10% of current delay
                var jitter = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 0.1 * (Random.Shared.NextDouble() * 2 - 1));
                var totalDelay = delay + jitter;
                _logger.LogInformation(LogEvents.MqttReconnecting, "MQTT reconnecting in {Delay:F1}s...", totalDelay.TotalSeconds);
                await Task.Delay(totalDelay, _cts.Token);

                await _client.ConnectAsync(_connectOptions, _cts.Token);
                _logger.LogInformation(LogEvents.MqttConnected, "MQTT reconnected to {Host}:{Port}", _settings.MqttHost, _settings.MqttPort);
                await PublishOnlineAsync(_cts.Token);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.MqttReconnecting, ex, "MQTT reconnect attempt failed");
                delay = delay * 2 < maxDelay ? delay * 2 : maxDelay;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _client.DisconnectedAsync -= OnDisconnectedAsync;
        if (_client.IsConnected)
        {
            await _client.DisconnectAsync();
        }
        _client.Dispose();
    }
}
