using Argus.Orchestrator.Config;
using Argus.Orchestrator.Health;
using Argus.Orchestrator.Logging;
using Microsoft.Extensions.Logging;
using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Extensions;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Argus.Orchestrator.Ha;

/// <summary>
/// IHaEventSource implemented with NetDaemon.Client 23.46.0 (D-06).
///
/// Responsibilities:
///   - Connects to HA WebSocket using HaUrl + HaToken from ConnectionSettings
///   - Subscribes to state_changed events
///   - Filters to the configured entity set (O(1) HashSet lookup)
///   - On every successful reconnection (after the first): calls GetStatesAsync (D-07)
///     and feeds current values; suppresses binary_sensor publication for 60s
///   - Reconnects with exponential backoff: 1s → 2s → 4s → 8s → ... → cap 60s (STRM-01)
///   - Logs connect/reconnect/backoff attempts and dropped events (OBS-01)
///   - HA token never logged (T-05-05)
/// </summary>
public class NetDaemonHaEventSource : IHaEventSource
{
    // Reconnect backoff constants (STRM-01): starts at 1s, doubles, capped at 60s
    private const int BackoffInitialSeconds = 1;
    private const int BackoffMaxSeconds = 60;

    private readonly ConnectionSettings _settings;
    private readonly EntitiesConfig _entitiesConfig;
    private readonly ReconnectCooldown _cooldown;
    private readonly IHomeAssistantClient _haClient;
    private readonly ArgusHealthSignals _signals;
    private readonly ILogger<NetDaemonHaEventSource> _logger;

    // Precomputed O(1) lookup set of configured entity_ids
    private readonly HashSet<string> _configuredEntities;

    public NetDaemonHaEventSource(
        ConnectionSettings settings,
        EntitiesConfig entitiesConfig,
        ReconnectCooldown cooldown,
        IHomeAssistantClient haClient,
        ArgusHealthSignals signals,
        ILogger<NetDaemonHaEventSource> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _entitiesConfig = entitiesConfig ?? throw new ArgumentNullException(nameof(entitiesConfig));
        _cooldown = cooldown ?? throw new ArgumentNullException(nameof(cooldown));
        _haClient = haClient ?? throw new ArgumentNullException(nameof(haClient));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _configuredEntities = new HashSet<string>(
            entitiesConfig.Entities.Select(e => e.EntityId),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<HaReading> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Bounded channel: prevents unbounded queue growth if consumer is slow
        var channel = Channel.CreateBounded<HaReading>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true,
        });

        // Run the HA connection + backoff loop on a background task
        var loopTask = Task.Run(() => RunConnectionLoopAsync(channel.Writer, ct), ct);

        await foreach (var reading in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return reading;
        }

        // Propagate any exception from the background task
        await loopTask; // throws if RunConnectionLoopAsync faulted
    }

    /// <summary>
    /// Outer reconnect loop with exponential backoff (STRM-01).
    /// Handles connect/disconnect and writes HaReadings to the channel.
    /// </summary>
    private async Task RunConnectionLoopAsync(ChannelWriter<HaReading> writer, CancellationToken ct)
    {
        var backoffSeconds = BackoffInitialSeconds;
        var isFirstConnection = true;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation(LogEvents.HaListenerStarting,
                        "Connecting to HA WebSocket at {HaUrl}", _settings.HaUrl);

                    // Parse host/port/ssl from HaUrl (e.g. "ws://homeassistant.local:8123" or "wss://...")
                    var (host, port, ssl) = ParseHaUrl(_settings.HaUrl);

                    var connection = await _haClient.ConnectAsync(
                        host, port, ssl, _settings.HaToken ?? string.Empty, ct)
                        .ConfigureAwait(false);

                    _logger.LogInformation(LogEvents.ChannelEstablished,
                        "Connected to HA WebSocket (host={Host} port={Port} ssl={Ssl})",
                        host, port, ssl);

                    // Signal HA connectivity (HEALTH-01 composite health)
                    _signals.HaConnected = true;

                    // Reset backoff on successful connection
                    backoffSeconds = BackoffInitialSeconds;

                    // On FIRST connect: log discovered numeric sensors not yet configured (UICFG-05)
                    if (isFirstConnection)
                    {
                        await LogDiscoverableSensorsAsync(connection, ct).ConfigureAwait(false);
                    }

                    // On reconnect (not first connect): snapshot get_states + mark cooldown (D-07)
                    if (!isFirstConnection)
                    {
                        _logger.LogInformation("HA reconnect: calling get_states snapshot (D-07, PITFALL 4)");
                        await FeedGetStatesAsync(connection, writer, ct).ConfigureAwait(false);
                        _cooldown.MarkReconnect(DateTimeOffset.UtcNow);
                        _logger.LogInformation(
                            "ReconnectCooldown started — binary_sensor suppressed for {Seconds}s",
                            ReconnectCooldown.SuppressionWindowSeconds);
                    }

                    isFirstConnection = false;

                    // Subscribe to state_changed stream
                    await SubscribeAndForwardAsync(connection, writer, ct).ConfigureAwait(false);

                    // Subscribe returned (connection closed cleanly, no exception):
                    // HA is no longer connected, so clear the signal before the next
                    // reconnect attempt. Without this, a clean WS close would leave
                    // HaConnected=true and HealthPublisherWorker would report healthy
                    // while HA is actually down (WR-01, HEALTH-01).
                    _signals.HaConnected = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Clear HA connectivity signal on any connection loss (HEALTH-01)
                    _signals.HaConnected = false;

                    _logger.LogWarning(ex,
                        "HA WebSocket connection lost — backing off {BackoffSeconds}s before reconnect",
                        backoffSeconds);

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Exponential backoff capped at 60s (STRM-01, T-05-03)
                    backoffSeconds = Math.Min(backoffSeconds * 2, BackoffMaxSeconds);
                }
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>
    /// Calls GetStatesAsync once and feeds current values into the channel (D-07 snapshot).
    /// </summary>
    private async Task FeedGetStatesAsync(
        IHomeAssistantConnection connection,
        ChannelWriter<HaReading> writer,
        CancellationToken ct)
    {
        // get_states: extension method from HomeAssistantConnectionExtensions (D-07)
        var states = await connection.GetStatesAsync(ct).ConfigureAwait(false);
        if (states is null) return;

        var now = DateTimeOffset.UtcNow;
        var suppress = _cooldown.IsSuppressed(now);
        var count = 0;

        foreach (var state in states)
        {
            if (TryMap(state.EntityId, state.State, state.LastChanged, _configuredEntities, suppress, out var reading))
            {
                await writer.WriteAsync(reading!, ct).ConfigureAwait(false);
                count++;
            }
        }

        _logger.LogInformation(
            "get_states snapshot fed {Count} configured entities to pipeline", count);
    }

    /// <summary>
    /// Subscribes to state_changed and forwards matching events until the connection closes or CT fires.
    /// </summary>
    private async Task SubscribeAndForwardAsync(
        IHomeAssistantConnection connection,
        ChannelWriter<HaReading> writer,
        CancellationToken ct)
    {
        var events = await connection.SubscribeToHomeAssistantEventsAsync("state_changed", ct)
            .ConfigureAwait(false);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = events.Subscribe(
            onNext: hassEvent =>
            {
                try
                {
                    // ToStateChangedEvent returns HassStateChangedEventData directly
                    var stateChanged = hassEvent.ToStateChangedEvent();
                    var newState = stateChanged?.NewState;
                    if (newState is null) return;

                    var now = DateTimeOffset.UtcNow;
                    var suppress = _cooldown.IsSuppressed(now);

                    if (TryMap(newState.EntityId, newState.State, newState.LastChanged,
                        _configuredEntities, suppress, out var reading))
                    {
                        // Best-effort write; channel is bounded so we don't block the Rx callback
                        if (!writer.TryWrite(reading!))
                        {
                            _logger.LogWarning(
                                "HaReading channel full — dropping event for {EntityId}", newState.EntityId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing state_changed event");
                }
            },
            onError: ex => tcs.TrySetException(ex),
            onCompleted: () => tcs.TrySetResult(true));

        // Register CT so we unblock when cancellation is requested
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        // Wait for whichever happens first: the WS connection closes, or the Rx
        // subscription signals onError/onCompleted. Awaiting only the WS close
        // would silently discard a stream-level error that does not close the
        // socket, leaving a broken event stream on a still-open connection that
        // never triggers reconnect (WR-05). The faulted tcs.Task rethrows here so
        // the outer loop's catch backs off and reconnects.
        var closeTask = connection.WaitForConnectionToCloseAsync(ct);
        var completed = await Task.WhenAny(closeTask, tcs.Task).ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }

    /// <summary>
    /// Logs discovered numeric sensors (UICFG-05) on the FIRST successful HA connect.
    /// One INFO line per unconfigured numeric sensor (entity_id + last value),
    /// followed by a total-count line.
    /// </summary>
    private async Task LogDiscoverableSensorsAsync(IHomeAssistantConnection connection, CancellationToken ct)
    {
        var states = await connection.GetStatesAsync(ct).ConfigureAwait(false);
        if (states is null) return;

        var discoverable = SelectDiscoverableSensors(
            states.Select(s => (s.EntityId, s.State)),
            _configuredEntities);

        foreach (var (entityId, value) in discoverable)
        {
            _logger.LogInformation(LogEvents.DiscoveredSensorsLogged,
                "Unconfigured numeric sensor: {EntityId} = {Value}", entityId, value);
        }

        _logger.LogInformation(LogEvents.DiscoveredSensorsLogged,
            "Startup sensor discovery: {Count} unconfigured numeric sensors found", discoverable.Count);
    }

    /// <summary>
    /// Pure static selector for UICFG-05: returns all numeric sensors not already in configuredEntities.
    /// A state qualifies when its value parses as double (invariant culture) and its entity_id is
    /// not in the configured set. Internal for unit testing without a live HA connection.
    /// </summary>
    internal static IReadOnlyList<(string EntityId, double Value)> SelectDiscoverableSensors(
        IEnumerable<(string EntityId, string? State)> states,
        HashSet<string> configuredEntities)
    {
        var result = new List<(string, double)>();
        foreach (var (entityId, state) in states)
        {
            if (configuredEntities.Contains(entityId))
                continue;
            if (!double.TryParse(state, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                continue;
            result.Add((entityId, value));
        }
        return result;
    }

    /// <summary>
    /// Maps a raw HA state to an HaReading.
    /// Returns false (and null reading) if:
    ///   - entity_id is not in the configured set, or
    ///   - state value is not parseable as double (e.g. "unavailable", "unknown") (T-05-01)
    /// This method is static/internal so it can be unit-tested without a live HA connection.
    /// </summary>
    internal static bool TryMap(
        string entityId,
        string? stateValue,
        DateTime lastChanged,
        HashSet<string> configuredEntities,
        bool suppressBinarySensor,
        out HaReading? reading)
    {
        reading = null;

        // Entity filter — O(1) HashSet lookup
        if (!configuredEntities.Contains(entityId))
            return false;

        // Numeric validation — invariant culture (T-05-01)
        if (!double.TryParse(stateValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return false;

        reading = new HaReading(
            EntityId: entityId,
            Value: value,
            LastChanged: new DateTimeOffset(lastChanged, TimeSpan.Zero),
            SuppressBinarySensor: suppressBinarySensor);

        return true;
    }

    /// <summary>
    /// Parses HA WebSocket URL into (host, port, ssl) components.
    /// Supports ws://, wss://, http://, https:// schemes.
    /// Default port: 8123.
    /// </summary>
    private static (string host, int port, bool ssl) ParseHaUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return ("localhost", 8123, false);

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var ssl = uri.Scheme is "wss" or "https";
            var port = uri.IsDefaultPort ? (ssl ? 443 : 8123) : uri.Port;
            return (uri.Host, port, ssl);
        }

        // Fallback: treat as plain hostname
        return (url, 8123, false);
    }
}
