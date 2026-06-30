using System.Text;
using Argus.Orchestrator.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// MQTTnet 5 client wrapper with per-attempt credential fetch (SUPV-03),
/// LWT-before-connect (PITFALL 6), and exponential backoff reconnect.
/// Uses MqttClientFactory (NOT MqttFactory — D-17/RESEARCH state-of-art).
///
/// Bridge-level LWT: argus/bridge/availability → offline is set in the
/// options built by BuildConnectOptionsAsync BEFORE every ConnectAsync call,
/// so an orchestrator crash marks ALL sensors unavailable (RES-01).
///
/// Credentials are fetched fresh from IMqttCredentialSource on each
/// (re)connect attempt — never cached — so re-provisioning the Mosquitto
/// add-on survives a reconnect without restarting Argus (SUPV-03).
/// </summary>
public sealed class MqttConnection : IAsyncDisposable
{
    public const string BridgeAvailabilityTopic = "argus/bridge/availability";

    private readonly IMqttCredentialSource _credentialSource;
    private readonly ILogger<MqttConnection> _logger;
    private readonly IMqttClient _client;
    private readonly CancellationTokenSource _cts = new();

    // Serializes connect attempts so the worker's initial ConnectAsync and a
    // fired DisconnectedAsync reconnect cannot hit the same IMqttClient
    // concurrently — MQTTnet does not guarantee thread-safety for concurrent
    // connect attempts on one client (WR-02).
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    public MqttConnection(IMqttCredentialSource credentialSource, ILogger<MqttConnection> logger)
    {
        _credentialSource = credentialSource;
        _logger = logger;

        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();

        // Wire reconnect handler
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    /// <summary>
    /// True when the underlying MQTT client is currently connected to the broker.
    /// Used by HealthPublisherWorker as the MQTT component of composite health (HEALTH-01).
    /// </summary>
    public bool IsConnected => _client.IsConnected;

    /// <summary>
    /// Connects to the broker, then immediately publishes "online" to availability topic.
    /// Credentials are fetched fresh on every call — never reused from a prior attempt (SUPV-03).
    /// LWT is configured in the options BEFORE ConnectAsync is called (PITFALL 6, RES-01).
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        await _connectGate.WaitAsync(ct);
        try
        {
            if (_client.IsConnected) return;
            var options = await BuildConnectOptionsAsync(ct);
            await _client.ConnectAsync(options, ct);
            _logger.LogInformation(LogEvents.MqttConnected, "MQTT connected");
            await PublishOnlineAsync(ct);
        }
        finally
        {
            _connectGate.Release();
        }
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

    /// <summary>
    /// Builds MQTT connect options for a single connection attempt by fetching
    /// credentials fresh from the configured source (SUPV-03 — never cached).
    /// LWT is embedded in the returned options so it is always set before
    /// ConnectAsync (PITFALL 6, RES-01).
    ///
    /// Internal visibility: exposed for unit-test LWT assertions without a live broker.
    /// </summary>
    internal async Task<MqttClientOptions> BuildConnectOptionsAsync(CancellationToken ct)
    {
        var creds = await _credentialSource.GetAsync(ct);

        // Log host/port only — never user or password (T-03-03)
        _logger.LogInformation(LogEvents.MqttCredentialsRefreshed,
            "MQTT credentials resolved for {Host}:{Port}", creds.Host, creds.Port);

        return new MqttClientOptionsBuilder()
            .WithTcpServer(creds.Host ?? "localhost", creds.Port)
            .WithCredentials(creds.User, creds.Password)
            .WithWillTopic(BridgeAvailabilityTopic)
            .WithWillPayload("offline")
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
    }

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

                // If another path already reconnected during the backoff, stop —
                // don't burn an attempt or double-connect (WR-02).
                if (_client.IsConnected) return;

                // Serialize the connect so this reconnect cannot race the worker's
                // initial ConnectAsync on the same IMqttClient (WR-02).
                await _connectGate.WaitAsync(_cts.Token);
                try
                {
                    if (_client.IsConnected) return;

                    // Fetch credentials fresh on every reconnect attempt (SUPV-03)
                    var options = await BuildConnectOptionsAsync(_cts.Token);
                    await _client.ConnectAsync(options, _cts.Token);
                    _logger.LogInformation(LogEvents.MqttConnected, "MQTT reconnected");
                    await PublishOnlineAsync(_cts.Token);
                    return;
                }
                finally
                {
                    _connectGate.Release();
                }
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
        _connectGate.Dispose();
    }
}
